using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Path-traversal hardening for <see cref="BackupRepository.Restore"/>.
/// Manifests can arrive from untrusted sources (imported from Usenet, tampered
/// repos), so every manifest-controlled path written during restore must be
/// anchored inside the destination directory. Each test poisons a real
/// manifest (re-stamped under the repo's own key so the manifest itself
/// passes authentication) and asserts the escape is blocked and nothing
/// is written outside the destination.
/// </summary>
public sealed class RestorePathSecurityTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public RestorePathSecurityTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-sec-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string BackupSingleFile()
    {
        File.WriteAllText(Path.Combine(_srcDir, "good.txt"), "legit content");
        using var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);
        return repo.BackupDirectory(_srcDir).BackupId;
    }

    private void PoisonManifest(string backupId, Action<BackupManifest> mutate)
    {
        // The poisoned manifest is re-stamped through the repo's own
        // write path (root hash + root HMAC under this repo's key).
        // Unstamped ("legacy") manifests no longer load at all — that
        // door closed when pre-release repo support was dropped — so
        // the only hostile manifest that can reach Restore's path
        // defenses is a validly stamped one: an attacker-authored repo
        // restored under its own key (audit B-L1), or defense in depth
        // against a future load-path bug. These tests pin that even a
        // fully authenticated manifest cannot drive a write outside
        // the restore destination.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.LoadManifest(backupId);
        mutate(manifest);
        repo.SaveManifest(manifest);
    }

    [Fact]
    public void Restore_BlocksDirectoryTraversalInFilePath()
    {
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string canary = Path.GetFullPath(Path.Combine(dest, "..", "escape-traversal.txt"));

        PoisonManifest(backupId, m => m.Files[0].Path = "../escape-traversal.txt");

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var ex = Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.Contains("escapes the restore directory", ex.Message);
        Assert.False(File.Exists(canary), "Traversal write escaped the restore directory!");
    }

    [Fact]
    public void Restore_BlocksNestedTraversal()
    {
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string canary = Path.GetFullPath(Path.Combine(dest, "..", "escape-nested.txt"));

        // Traversal buried mid-path: sub/../../escape-nested.txt
        PoisonManifest(backupId, m => m.Files[0].Path = "sub/../../escape-nested.txt");

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.False(File.Exists(canary), "Nested traversal write escaped the restore directory!");
    }

    [Fact]
    public void Restore_BlocksAbsolutePathInFilePath()
    {
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string canary = Path.Combine(_workDir, "escape-absolute.txt");

        PoisonManifest(backupId, m => m.Files[0].Path = canary);

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.False(File.Exists(canary), "Absolute-path write escaped the restore directory!");
    }

    [Fact]
    public void Restore_BlocksTraversalInDirectoryEntries()
    {
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string canaryDir = Path.GetFullPath(Path.Combine(dest, "..", "escape-evildir"));

        PoisonManifest(backupId, m => m.Directories = new List<string> { "../escape-evildir" });

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.False(Directory.Exists(canaryDir), "Traversal directory escaped the restore directory!");
    }

    [Fact]
    public void Restore_BlocksTraversalInSymlinkPath()
    {
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string canary = Path.GetFullPath(Path.Combine(dest, "..", "escape-link"));

        PoisonManifest(backupId, m =>
        {
            m.Files[0].Path = "../escape-link";
            m.Files[0].SymlinkTarget = "innocent-target";
            m.Files[0].Chunks.Clear();
        });

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.False(File.Exists(canary) || Directory.Exists(canary),
            "Traversal symlink escaped the restore directory!");
    }

    [Fact]
    public void Restore_LegitimateNestedPathsStillRestore()
    {
        // The anchor must not break normal restores with deep relative paths.
        string nested = Path.Combine(_srcDir, "a", "b", "c");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "deep.txt"), "deep content");
        using (var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations))
        {
            string backupId = repo.BackupDirectory(_srcDir).BackupId;
            string dest = Path.Combine(_workDir, "restored");
            using var repo2 = BackupRepository.Open(_repoDir, Passphrase);
            repo2.Restore(backupId, dest);
            string rel = Path.Combine("a", "b", "c", "deep.txt");
            Assert.Equal("deep content",
                File.ReadAllText(Path.Combine(dest, rel)));
        }
    }

    [Fact]
    public void Restore_BlocksSymlinkRedirectAttack()
    {
        // A symlink created early must not redirect a later file write
        // outside destDir. Two-pass restore (files first, symlinks last)
        // defeats this.
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string outside = Path.Combine(_workDir, "outside");
        Directory.CreateDirectory(outside);
        string canary = Path.Combine(outside, "pwned.txt");

        PoisonManifest(backupId, m =>
        {
            // Entry 0: symlink "evil" -> outside dir. Save its chunks first,
            // then add a file "evil/pwned.txt" reusing them — lexically inside
            // destDir, but would resolve through the symlink to outside/.
            var chunks = m.Files[0].Chunks.ToList();
            string sha = m.Files[0].Sha256;
            m.Files[0].Path = "evil";
            m.Files[0].SymlinkTarget = outside;
            m.Files[0].Chunks.Clear();
            m.Files.Add(new FileEntry
            {
                Path = "evil/pwned.txt",
                Size = 5,
                Sha256 = sha,
                Chunks = chunks,
                MtimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            });
        });

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        // The two-pass restore defeats the attack: pass 1 writes pwned.txt
        // into a real restored/evil/ directory, and pass 2 then cannot
        // replace that directory with the attacker's symlink, so the
        // restore fails closed. The canary must never appear outside dest.
        Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.False(File.Exists(canary),
            "Symlink redirect attack wrote outside the restore directory!");
    }

    // A symlink that already exists in the destination tree must not be
    // followed during restore. ResolveRestorePath is lexical — it cannot
    // see reparse points — so every restore write site additionally walks
    // its path components and fails closed on any reparse point.
    private static bool CanCreateSymlinks()
    {
        string probeDir = Path.Combine(Path.GetTempPath(), "ub-sec-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(probeDir);
            Directory.CreateSymbolicLink(Path.Combine(probeDir, "link"), probeDir);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // Windows without Developer Mode / privilege.
        }
        finally
        {
            try { Directory.Delete(probeDir, recursive: true); } catch { }
        }
    }

    private static void SkipIfNoSymlinkPrivilege()
    {
        if (!CanCreateSymlinks())
            throw Xunit.Sdk.SkipException.ForSkip("Creating symlinks requires privilege on this machine.");
    }

    [Fact]
    public void Restore_BlocksPreexistingSymlinkInParentComponent()
    {
        SkipIfNoSymlinkPrivilege();
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string outside = Path.Combine(_workDir, "outside");
        Directory.CreateDirectory(outside);
        string canary = Path.Combine(outside, "pwned.txt");
        Directory.CreateDirectory(dest);
        Directory.CreateSymbolicLink(Path.Combine(dest, "sub"), outside);

        // Lexically inside destDir ("sub/pwned.txt"), but the directory
        // creation, temp-file write and rename would all traverse the
        // pre-existing symlink into outside/.
        PoisonManifest(backupId, m => m.Files[0].Path = "sub/pwned.txt");

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var ex = Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.Contains("reparse point", ex.Message);
        Assert.False(File.Exists(canary),
            "Restore followed a pre-existing symlink outside the destination!");
    }

    [Fact]
    public void Restore_BlocksTwoRestoreSymlinkPlanting()
    {
        // The realistic exploit: restore A (hostile manifest) plants
        // dest/evil -> outside via a symlink entry — allowed, since the
        // link path itself is inside destDir. A later restore into the
        // same destination must not write regular files through it.
        SkipIfNoSymlinkPrivilege();
        string outside = Path.Combine(_workDir, "outside");
        Directory.CreateDirectory(outside);
        string canary = Path.Combine(outside, "pwned.txt");

        string backupA, backupB;
        using (var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations))
        {
            File.WriteAllText(Path.Combine(_srcDir, "good.txt"), "legit content");
            backupA = repo.BackupDirectory(_srcDir).BackupId;
            backupB = repo.BackupDirectory(_srcDir).BackupId;
        }

        PoisonManifest(backupA, m =>
        {
            m.Files[0].Path = "evil";
            m.Files[0].SymlinkTarget = outside;
            m.Files[0].Chunks.Clear();
        });
        string dest = Path.Combine(_workDir, "restored");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            repo.Restore(backupA, dest);
        // Planted via File.CreateSymbolicLink, which on Windows creates a
        // file-type link even for directory targets (Directory.Exists would
        // be false); assert the link itself exists either way.
        Assert.NotNull(new FileInfo(Path.Combine(dest, "evil")).LinkTarget);

        PoisonManifest(backupB, m => m.Files[0].Path = "evil/pwned.txt");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var ex = Assert.Throws<InvalidDataException>(() => repo.Restore(backupB, dest));
            Assert.Contains("reparse point", ex.Message);
        }
        Assert.False(File.Exists(canary),
            "Second restore wrote through a symlink planted by the first!");
    }

    [Fact]
    public void Restore_BlocksSymlinkAtFileLeaf()
    {
        SkipIfNoSymlinkPrivilege();
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string outside = Path.Combine(_workDir, "outside");
        Directory.CreateDirectory(outside);
        string canary = Path.Combine(outside, "canary.txt");
        Directory.CreateDirectory(dest);
        string linkPath = Path.Combine(dest, "victim.txt");
        File.CreateSymbolicLink(linkPath, canary);

        PoisonManifest(backupId, m => m.Files[0].Path = "victim.txt");

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var ex = Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.Contains("reparse point", ex.Message);
        // Fail closed: the pre-existing link is left untouched, never
        // replaced or followed.
        Assert.NotNull(new FileInfo(linkPath).LinkTarget);
        Assert.False(File.Exists(canary),
            "Restore wrote through a symlink at the destination file path!");
    }

    [Fact]
    public void Restore_BlocksPreexistingSymlinkInDirectoryEntries()
    {
        SkipIfNoSymlinkPrivilege();
        string backupId = BackupSingleFile();
        string dest = Path.Combine(_workDir, "restored");
        string outside = Path.Combine(_workDir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(dest);
        Directory.CreateSymbolicLink(Path.Combine(dest, "sub"), outside);

        PoisonManifest(backupId, m => m.Directories = new List<string> { "sub/child" });

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var ex = Assert.Throws<InvalidDataException>(() => repo.Restore(backupId, dest));
        Assert.Contains("reparse point", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(outside, "child")),
            "Restore created directories through a pre-existing symlink!");
    }

    [Fact]
    public void Restore_LegitimateSymlinkStillRestores()
    {
        // The reparse-point check must not break normal restores of
        // backups that legitimately contain symlinks.
        SkipIfNoSymlinkPrivilege();
        File.WriteAllText(Path.Combine(_srcDir, "target.txt"), "target content");
        File.CreateSymbolicLink(
            Path.Combine(_srcDir, "link.txt"), Path.Combine(_srcDir, "target.txt"));
        using (var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations))
        {
            string backupId = repo.BackupDirectory(_srcDir).BackupId;
            string dest = Path.Combine(_workDir, "restored");
            using var repo2 = BackupRepository.Open(_repoDir, Passphrase);
            repo2.Restore(backupId, dest);
            string linkPath = Path.Combine(dest, "link.txt");
            Assert.NotNull(new FileInfo(linkPath).LinkTarget);
            Assert.Equal("target content",
                File.ReadAllText(Path.Combine(dest, "target.txt")));
        }
    }
}
