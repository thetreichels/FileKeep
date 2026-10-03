using UsenetBackup.Core;
using UsenetBackup.Core.Recovery;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Tests for the USB recovery wizard's workflow logic (WizardState).
/// The WinForms UI is a thin shell; all decisions live here and are tested here.
/// </summary>
public sealed class RecoveryWizardTests : IDisposable
{
    private const string Passphrase = "recovery-test-passphrase";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _repoDir;
    private readonly string _srcDir;

    public RecoveryWizardTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "ub-rec-" + Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(_repoDir, "src");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello");
        BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations).Dispose();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public void ValidateRepoPath_RejectsEmpty()
    {
        using var s = new WizardState { RepoPath = "" };
        var (ok, error) = s.ValidateRepoPath();
        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ValidateRepoPath_RejectsMissingFolder()
    {
        using var s = new WizardState { RepoPath = Path.Combine(_repoDir, "nope") };
        var (ok, _) = s.ValidateRepoPath();
        Assert.False(ok);
    }

    [Fact]
    public void ValidateRepoPath_RejectsFolderWithoutRepoJson()
    {
        string other = Path.Combine(_repoDir, "other");
        Directory.CreateDirectory(other);
        using var s = new WizardState { RepoPath = other };
        var (ok, error) = s.ValidateRepoPath();
        Assert.False(ok);
        Assert.Contains("repo.json", error);
    }

    [Fact]
    public void ValidateRepoPath_AcceptsRealRepo()
    {
        using var s = new WizardState { RepoPath = _repoDir };
        var (ok, error) = s.ValidateRepoPath();
        Assert.True(ok, error);
    }

    [Fact]
    public void ValidatePassphrase_WrongPassphrase_FailsWhenChunksExist()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        repo.BackupDirectory(_srcDir); // ensures local chunks exist
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = "wrong" };
        var (ok, note) = s.ValidatePassphrase();
        Assert.False(ok);
        Assert.Contains("incorrect", note);
    }

    [Fact]
    public void ValidatePassphrase_CorrectPassphrase_PassesWhenChunksExist()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        repo.BackupDirectory(_srcDir);
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        var (ok, note) = s.ValidatePassphrase();
        Assert.True(ok, note);
    }

    [Fact]
    public void ValidatePassphrase_NoChunks_ReturnsUnverified()
    {
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        var (ok, note) = s.ValidatePassphrase();
        Assert.True(ok);
        Assert.Equal("unverified", note);
    }

    [Fact]
    public void ListBackups_RoundTripsThroughState()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        var list = s.ListBackups();
        Assert.Contains(list, b => b.BackupId == manifest.BackupId);
    }

    [Fact]
    public void RestoreDisk_ConfirmationMismatch_Throws()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        s.SelectedBackupId = manifest.BackupId;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            s.RestoreDisk(@"\\.\PhysicalDrive9", @"\\.\PhysicalDrive8"));
        Assert.Contains("confirmation", ex.Message);
    }

    [Fact]
    public void RestoreFiles_RoundTripsThroughState()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        s.SelectedBackupId = manifest.BackupId;
        string dest = Path.Combine(_repoDir, "restored");
        s.RestoreFiles(dest);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void Verify_EmptyOnSuccess()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        s.SelectedBackupId = manifest.BackupId;
        var issues = s.Verify();
        Assert.Empty(issues);
    }

    [Fact]
    public void SelectedIsDiskImage_FalseForFileBackup()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        s.SelectedBackupId = manifest.BackupId;
        Assert.False(s.SelectedIsDiskImage());
    }

    private sealed class FakeDriveEnumerator : IDriveEnumerator
    {
        public IReadOnlyList<PhysicalDriveInfo> ListDrives() => new[]
        {
            new PhysicalDriveInfo(@"\\.\PhysicalDrive0", 0, "Test SSD 512GB", 512UL * 1024 * 1024 * 1024, "SN123"),
            new PhysicalDriveInfo(@"\\.\PhysicalDrive1", 1, "Test HDD 2TB", 2UL * 1024 * 1024 * 1024 * 1024, "SN456"),
        };
    }

    [Fact]
    public void ListPhysicalDrives_ReturnsDrives()
    {
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        var drives = s.ListPhysicalDrives(new FakeDriveEnumerator());
        Assert.Equal(2, drives.Count);
        Assert.Equal(0, drives[0].Index);
        Assert.Contains("Test SSD", drives[0].Model);
        Assert.Contains("512.0 GB", drives[0].Display);
    }

    [Fact]
    public void RestoreDiskToDrive_NullDrive_Throws()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        s.SelectedBackupId = manifest.BackupId;
        Assert.Throws<ArgumentNullException>(() => s.RestoreDiskToDrive(null!));
    }

    [Fact]
    public void RestoreDiskToDrive_ValidDrive_CallsRestore()
    {
        // Uses a fake drive; RestoreDiskImage will fail on the bogus device path,
        // proving the drive object flowed through (not a confirmation error).
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        s.SelectedBackupId = manifest.BackupId;
        var drive = new PhysicalDriveInfo(@"\\.\PhysicalDrive9", 9, "Nonexistent", 1000, "");
        // Should fail on the device itself, not on validation.
        var ex = Assert.ThrowsAny<Exception>(() => s.RestoreDiskToDrive(drive));
        Assert.DoesNotContain("confirmation", ex.Message.ToLower());
    }
}
