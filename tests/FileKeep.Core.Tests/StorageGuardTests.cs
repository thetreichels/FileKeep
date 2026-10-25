using FileKeep.Core;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Tests for StorageGuard: the invariant that backup chunks never live on
/// the drive being backed up, and that restore/sync destinations cannot
/// overlap the repository.
/// </summary>
public sealed class StorageGuardTests : IDisposable
{
    private readonly string _workDir;

    public StorageGuardTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnsureRepoNotInSource_ThrowsWhenRepoInsideSource()
    {
        string source = Path.Combine(_workDir, "source");
        string repo = Path.Combine(source, "repo");
        Directory.CreateDirectory(repo);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StorageGuard.EnsureRepoNotInSource(repo, source));
        Assert.Contains("contains the repository", ex.Message);
    }

    [Fact]
    public void EnsureRepoNotInSource_AllowsRepoOutsideSource()
    {
        string source = Path.Combine(_workDir, "source");
        string repo = Path.Combine(_workDir, "repo");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(repo);

        // Should not throw.
        StorageGuard.EnsureRepoNotInSource(repo, source);
    }

    [Fact]
    public void EnsureRepoNotInSource_AllowsUnrelatedPaths()
    {
        // Sibling directories with similar prefixes must not false-positive.
        string source = Path.Combine(_workDir, "data");
        string repo = Path.Combine(_workDir, "data-repo");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(repo);

        StorageGuard.EnsureRepoNotInSource(repo, source);
    }

    [Fact]
    public void EnsureRestoreDestNotInRepo_ThrowsWhenDestInsideRepo()
    {
        string repo = Path.Combine(_workDir, "repo");
        string dest = Path.Combine(repo, "restored");
        Directory.CreateDirectory(repo);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StorageGuard.EnsureRestoreDestNotInRepo(repo, dest));
        Assert.Contains("inside the repository", ex.Message);
    }

    [Fact]
    public void EnsureRestoreDestNotInRepo_ThrowsWhenDestIsRepo()
    {
        string repo = Path.Combine(_workDir, "repo");
        Directory.CreateDirectory(repo);

        Assert.Throws<InvalidOperationException>(() =>
            StorageGuard.EnsureRestoreDestNotInRepo(repo, repo));
    }

    [Fact]
    public void EnsureSmbShareNotInRepo_AllowsUncShare()
    {
        string repo = Path.Combine(_workDir, "repo");
        Directory.CreateDirectory(repo);

        // UNC shares are remote; never overlap the local repo.
        StorageGuard.EnsureSmbShareNotInRepo(repo, @"\\NAS\backups\filekeep");
    }

    [Fact]
    public void EnsureSmbShareNotInRepo_ThrowsWhenShareIsRepo()
    {
        string repo = Path.Combine(_workDir, "repo");
        Directory.CreateDirectory(repo);

        Assert.Throws<InvalidOperationException>(() =>
            StorageGuard.EnsureSmbShareNotInRepo(repo, repo));
    }

    [Fact]
    public void ParsePhysicalDriveNumber_ParsesCorrectly()
    {
        Assert.Equal(0, StorageGuard.ParsePhysicalDriveNumber(@"\\.\PhysicalDrive0"));
        Assert.Equal(3, StorageGuard.ParsePhysicalDriveNumber(@"\\.\PHYSICALDRIVE3"));
        Assert.Null(StorageGuard.ParsePhysicalDriveNumber(@"\\.\C:"));
        Assert.Null(StorageGuard.ParsePhysicalDriveNumber("C:\\repo"));
        Assert.Null(StorageGuard.ParsePhysicalDriveNumber(""));
    }

    [Fact]
    public void BackupDirectory_ThrowsWhenRepoInSource()
    {
        string source = Path.Combine(_workDir, "src");
        string repoDir = Path.Combine(source, "repo");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "hello");

        using var repo = BackupRepository.Init(repoDir, "test-pass", 64 * 1024, 10_000);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            repo.BackupDirectory(source));
        Assert.Contains("contains the repository", ex.Message);
    }
}
