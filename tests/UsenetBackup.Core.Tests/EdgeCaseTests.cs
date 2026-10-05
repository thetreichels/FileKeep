using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Backup-engine edge cases: unicode filenames, zero-byte files, and long
/// paths. Locked files are intentionally not covered here: the
/// backup-privilege behavior was proven on the Azure VM (2026-10-03 — plain
/// backup fails with exit 1 on a FileShare.None-locked file, backup with
/// --backup-privilege succeeds with exit 0), and the privilege P/Invoke path
/// is Windows-only, so there is no meaningful cross-platform xunit test for it.
/// </summary>
public sealed class EdgeCaseTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    // SHA-256 of the empty input.
    private const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public EdgeCaseTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-edge-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private BackupRepository InitRepo() =>
        BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);

    private void WriteSrc(string rel, byte[] data)
    {
        string full = Path.Combine(_srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
    }

    private BackupManifest Backup()
    {
        using var repo = InitRepo();
        return repo.BackupDirectory(_srcDir);
    }

    private void RestoreTo(string backupId, string dest)
    {
        // Fresh open: proves the manifest JSON round-trips through disk.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        repo.Restore(backupId, dest);
    }

    private string RestoredPath(string dest, string rel) =>
        Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void RoundTrip_PreservesUnicodeFilenames()
    {
        var names = new[]
        {
            "café.txt",             // precomposed Latin-1
            "日本語のファイル.txt",   // CJK
            "файл.txt",             // Cyrillic
            "📦-archive.txt",        // emoji (surrogate pair)
            "données/résumé.txt",   // unicode directory + file
        };
        var payloads = new Dictionary<string, byte[]>();
        for (int i = 0; i < names.Length; i++)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes($"payload-{i}-ünïcodé-{i * 7}");
            payloads[names[i]] = data;
            WriteSrc(names[i], data);
        }

        var manifest = Backup();

        // The manifest itself must carry the exact unicode paths (forward-slash
        // normalized), proving the manifest store records them faithfully.
        foreach (var name in names)
            Assert.Contains(manifest.Files, f => f.Path == name);

        string dest = Path.Combine(_workDir, "restored");
        RestoreTo(manifest.BackupId, dest);

        foreach (var (name, expected) in payloads)
            Assert.Equal(expected, File.ReadAllBytes(RestoredPath(dest, name)));
    }

    [Fact]
    public void RoundTrip_PreservesZeroByteFiles()
    {
        WriteSrc("empty.bin", Array.Empty<byte>());
        WriteSrc("also-empty.txt", Array.Empty<byte>());
        WriteSrc("sibling.txt", "not empty"u8.ToArray());

        var manifest = Backup();

        var empty = Assert.Single(manifest.Files, f => f.Path == "empty.bin");
        Assert.Equal(0, empty.Size);
        Assert.Equal(EmptySha256, empty.Sha256);

        string dest = Path.Combine(_workDir, "restored");
        RestoreTo(manifest.BackupId, dest);

        Assert.Equal(0, new FileInfo(RestoredPath(dest, "empty.bin")).Length);
        Assert.Equal(0, new FileInfo(RestoredPath(dest, "also-empty.txt")).Length);
        Assert.Equal("not empty"u8.ToArray(), File.ReadAllBytes(RestoredPath(dest, "sibling.txt")));
    }

    [Fact]
    public void RoundTrip_HandlesLongPaths()
    {
        // Build a path well past the legacy Windows MAX_PATH (260): four
        // nested 60-char directory names plus a 200-char filename. ext4 allows
        // 255 bytes per component, so every component stays filesystem-legal
        // while the full path exercises the engine beyond 260 chars.
        string relDir = string.Join("/",
            Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 60)));
        string rel = relDir + "/" + new string('f', 200) + ".txt";
        byte[] data = "long-path payload"u8.ToArray();
        WriteSrc(rel, data);

        string fullSrc = Path.Combine(_srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(fullSrc.Length > 260, $"test setup: path is only {fullSrc.Length} chars");

        var manifest = Backup();
        Assert.Contains(manifest.Files, f => f.Path == rel);

        string dest = Path.Combine(_workDir, "restored");
        RestoreTo(manifest.BackupId, dest);

        Assert.Equal(data, File.ReadAllBytes(RestoredPath(dest, rel)));
    }
}
