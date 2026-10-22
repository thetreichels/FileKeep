using UsenetBackup.Core;
using UsenetBackup.Core.Recovery;
using UsenetBackup.Core.Security;
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
        Assert.Contains("doesn't look like a FileKeep backup", error);
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

    /// <summary>Creates a repo (with one local chunk, so verification is real) keyed by <paramref name="secret"/>.</summary>
    private string CreateRepoWithChunks(string name, string secret)
    {
        string dir = Path.Combine(_repoDir, name);
        string src = Path.Combine(dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        BackupRepository.Init(dir, secret, ChunkSize, KdfIterations).Dispose();
        using var repo = BackupRepository.Open(dir, secret);
        repo.BackupDirectory(src);
        return dir;
    }

    [Fact]
    public void ValidatePassphrase_GroupedLowercaseSheetSecret_FallsBackToCanonical()
    {
        string canonical = GeneratedSecret.Generate();
        string dir = CreateRepoWithChunks("gen-fallback", canonical);
        // The sheet shows the grouped form; the user types it back lowercase.
        string typed = GeneratedSecret.FormatForDisplay(canonical).ToLowerInvariant();
        using var s = new WizardState { RepoPath = dir, Passphrase = typed };
        var (ok, note) = s.ValidatePassphrase();
        Assert.True(ok, note);
        // The canonical string became the session credential, and the
        // re-opened repo keeps validating with it.
        Assert.Equal(canonical, s.Passphrase);
        var (okAgain, noteAgain) = s.ValidatePassphrase();
        Assert.True(okAgain, noteAgain);
    }

    [Fact]
    public void ValidatePassphrase_CanonicalSheetSecret_AsTypedPasses()
    {
        string canonical = GeneratedSecret.Generate();
        string dir = CreateRepoWithChunks("gen-exact", canonical);
        using var s = new WizardState { RepoPath = dir, Passphrase = canonical };
        var (ok, note) = s.ValidatePassphrase();
        Assert.True(ok, note);
        Assert.Equal(canonical, s.Passphrase);
    }

    [Fact]
    public void ValidatePassphrase_PassphraseWithSpaces_NeverTransformed()
    {
        const string spaced = "a passphrase with spaces and lowercase";
        string dir = CreateRepoWithChunks("spaced", spaced);
        using var s = new WizardState { RepoPath = dir, Passphrase = spaced };
        var (ok, note) = s.ValidatePassphrase();
        Assert.True(ok, note);
        Assert.Equal(spaced, s.Passphrase);
    }

    [Fact]
    public void ValidatePassphrase_WrongSheetSecret_FailsAndRestoresTypedValue()
    {
        string canonical = GeneratedSecret.Generate();
        string dir = CreateRepoWithChunks("gen-wrong", canonical);
        // A different secret's sheet form: plausible shape, wrong value —
        // both the as-typed and the normalized attempt must fail.
        string wrongTyped = GeneratedSecret.FormatForDisplay(GeneratedSecret.Generate()).ToLowerInvariant();
        using var s = new WizardState { RepoPath = dir, Passphrase = wrongTyped };
        var (ok, note) = s.ValidatePassphrase();
        Assert.False(ok);
        Assert.Contains("incorrect", note);
        // The failed fallback leaves no trace: the typed value is restored.
        Assert.Equal(wrongTyped, s.Passphrase);
    }

    /// <summary>Creates a repo keyed by <paramref name="secret"/> with a source dir ready to back up, but stores NO chunks (validation stays unverified — the Usenet-restore case).</summary>
    private string CreateRepoWithoutChunks(string name, string secret)
    {
        string dir = Path.Combine(_repoDir, name);
        string src = Path.Combine(dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        BackupRepository.Init(dir, secret, ChunkSize, KdfIterations).Dispose();
        return dir;
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
    public void ValidatePassphrase_NoChunks_GroupedSheetSecret_AdoptsCanonical()
    {
        string canonical = GeneratedSecret.Generate();
        string dir = CreateRepoWithoutChunks("gen-unverified", canonical);
        // No local chunks: the as-typed grouped/lowercase sheet form is
        // accepted unverified — and must be adopted in canonical form, or
        // the session would keep deriving keys from the display string.
        string typed = GeneratedSecret.FormatForDisplay(canonical).ToLowerInvariant();
        using var s = new WizardState { RepoPath = dir, Passphrase = typed };
        var (ok, note) = s.ValidatePassphrase();
        Assert.True(ok, note);
        Assert.Equal("unverified", note);
        Assert.Equal(canonical, s.Passphrase);
        // The session repo now derives from the canonical string: chunks
        // stored through it verify under it on a second validation.
        using var repo = s.OpenRepo();
        repo.BackupDirectory(Path.Combine(dir, "src"));
        var (okAgain, noteAgain) = s.ValidatePassphrase();
        Assert.True(okAgain, noteAgain);
        Assert.Equal("", noteAgain);
        Assert.Equal(canonical, s.Passphrase);
    }

    [Fact]
    public void ValidatePassphrase_NoChunks_OrdinaryPassphrase_StaysAsTyped()
    {
        const string spaced = "a passphrase with spaces and lowercase";
        string dir = CreateRepoWithoutChunks("plain-unverified", spaced);
        using var s = new WizardState { RepoPath = dir, Passphrase = spaced };
        var (ok, note) = s.ValidatePassphrase();
        Assert.True(ok, note);
        Assert.Equal("unverified", note);
        // Not canonical-shaped after normalization: never touched.
        Assert.Equal(spaced, s.Passphrase);
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
        string dest = Path.Combine(Path.GetTempPath(), "ub-restored-" + Guid.NewGuid().ToString("N"));
        try
        {
            s.RestoreFiles(dest);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); } catch { }
        }
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

    [Fact]
    public void EnsureSmbChunks_PullsMissingChunksFromShare()
    {
        // Simulate: full repo on the "SMB share" (a local dir acts as the
        // share when no credentials are given), USB stick has only metadata.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        string shareDir = Path.Combine(Path.GetTempPath(), "ub-smb-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDir(_repoDir, shareDir);
            // Strip chunks locally to simulate the USB stick.
            Directory.Delete(Path.Combine(_repoDir, "chunks"), recursive: true);

            using var s = new WizardState
            {
                RepoPath = _repoDir,
                Passphrase = Passphrase,
                SelectedBackupId = manifest.BackupId,
                SmbShare = shareDir, // local path = no creds, handy for tests
            };
            int progressCalls = 0;
            s.EnsureSmbChunks((done, total) => progressCalls++);
            Assert.True(progressCalls > 0);

            // Chunks are back; a full restore works without the share.
            string dest = Path.Combine(Path.GetTempPath(), "ub-restored-" + Guid.NewGuid().ToString("N"));
            try
            {
                s.RestoreFiles(dest, MergePolicy.Overwrite);
                Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
            }
            finally
            {
                try { Directory.Delete(dest, recursive: true); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(shareDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureSmbChunks_NoShare_IsNoop()
    {
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        s.SelectedBackupId = "nonexistent";
        // Must not throw when no SMB share was configured.
        s.EnsureSmbChunks();
    }

    [Fact]
    public void EnsureSmbChunks_MissingChunk_ThrowsClearError()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        string shareDir = Path.Combine(Path.GetTempPath(), "ub-smb-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Share has the manifest but no chunks.
            Directory.CreateDirectory(Path.Combine(shareDir, "chunks"));
            Directory.CreateDirectory(Path.Combine(shareDir, "manifests"));
            foreach (string f in Directory.GetFiles(Path.Combine(_repoDir, "manifests")))
                File.Copy(f, Path.Combine(shareDir, "manifests", Path.GetFileName(f)));
            Directory.Delete(Path.Combine(_repoDir, "chunks"), recursive: true);

            using var s = new WizardState
            {
                RepoPath = _repoDir,
                Passphrase = Passphrase,
                SelectedBackupId = manifest.BackupId,
                SmbShare = shareDir,
            };
            var ex = Assert.Throws<FileNotFoundException>(() => s.EnsureSmbChunks());
            Assert.Contains("not found on SMB share", ex.Message);
        }
        finally
        {
            try { Directory.Delete(shareDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureLocalChunks_PullsMissingChunksFromFolder()
    {
        // WALK-8: a USB backup location is a plain folder with the same
        // layout as a share. Back up, copy the repo to a "drive" folder,
        // strip the local chunks (staging pruned after upload), then
        // pull them back from the folder.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        string driveDir = Path.Combine(Path.GetTempPath(), "ub-usb-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDir(_repoDir, driveDir);
            Directory.Delete(Path.Combine(_repoDir, "chunks"), recursive: true);

            using var s = new WizardState
            {
                RepoPath = _repoDir,
                Passphrase = Passphrase,
                SelectedBackupId = manifest.BackupId,
            };
            int progressCalls = 0;
            s.EnsureLocalChunks(driveDir, (done, total) => progressCalls++);
            Assert.True(progressCalls > 0);

            // Chunks are back; a full restore works without the folder.
            string dest = Path.Combine(Path.GetTempPath(), "ub-restored-" + Guid.NewGuid().ToString("N"));
            try
            {
                s.RestoreFiles(dest, MergePolicy.Overwrite);
                Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
            }
            finally
            {
                try { Directory.Delete(dest, recursive: true); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(driveDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureLocalChunks_BlankFolder_IsNoop()
    {
        using var s = new WizardState { RepoPath = _repoDir, Passphrase = Passphrase };
        s.SelectedBackupId = "nonexistent";
        // Must not throw when no folder was resolved.
        s.EnsureLocalChunks("");
    }

    [Fact]
    public void EnsureLocalChunks_MissingChunk_ThrowsClearError()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        string driveDir = Path.Combine(Path.GetTempPath(), "ub-usb-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The folder exists but holds no chunks.
            Directory.CreateDirectory(Path.Combine(driveDir, "chunks"));
            Directory.Delete(Path.Combine(_repoDir, "chunks"), recursive: true);

            using var s = new WizardState
            {
                RepoPath = _repoDir,
                Passphrase = Passphrase,
                SelectedBackupId = manifest.BackupId,
            };
            var ex = Assert.Throws<FileNotFoundException>(() => s.EnsureLocalChunks(driveDir));
            Assert.Contains("not found in folder", ex.Message);
        }
        finally
        {
            try { Directory.Delete(driveDir, recursive: true); } catch { }
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)));
    }
}
