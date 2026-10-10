using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Path-traversal hardening for <see cref="BackupRepository.Restore"/>.
/// Manifests can arrive from untrusted sources (imported from Usenet, tampered
/// repos), so every manifest-controlled path written during restore must be
/// anchored inside the destination directory. Each test poisons a real
/// manifest (recomputing the root hash so the manifest itself passes
/// validation) and asserts the escape is blocked and nothing is written
/// outside the destination.
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
        string manifestPath = Path.Combine(_repoDir, "manifests", backupId + ".json");
        var manifest = BackupManifest.FromJson(File.ReadAllText(manifestPath));
        mutate(manifest);
        manifest.RootSha256 = manifest.ComputeRootHash();
        File.WriteAllText(manifestPath, manifest.ToJson());
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
}
