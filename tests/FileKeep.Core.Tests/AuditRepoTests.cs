using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Audit records for background restore/verify operations (security
/// review rec 6): repo.open and restore.verify, produced by the
/// dashboard's StartRestore/StartVerify background tasks. The tasks
/// return 202 immediately and audit asynchronously, so tests poll
/// the captured log instead of asserting synchronously.
/// </summary>
public sealed class AuditRepoTests : IDisposable
{
    private const string Passphrase = "repo-pass-123";
    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string _backupId;
    private readonly string? _savedPassphrase;

    public AuditRepoTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "fk-auditrepo-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "hello.txt"), "hello world");
        using (var repo = BackupRepository.Init(_repoDir, Passphrase))
        {
            _backupId = repo.BackupDirectory(_srcDir).BackupId;
        }
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private ServiceConfig Config() => new()
    {
        Jobs = new List<BackupJobConfig>
        {
            new() { Name = "job", Repo = _repoDir },
        },
    };

    private static (List<string> Lines, object Gate, Action<string> Log) Capture()
    {
        var lines = new List<string>();
        var gate = new object();
        Action<string> log = line => { lock (gate) lines.Add(line); };
        return (lines, gate, log);
    }

    // GetRestorePassphrase returns the session passphrase whenever
    // any unlock method is enabled — and the env var counts as a
    // configured passphrase — so session-less calls 401 here. Tests
    // pass an unlocked session holding the repo passphrase.
    private static RestoreSession UnlockedSession()
    {
        var session = new RestoreSession();
        session.Unlock(Passphrase);
        return session;
    }

    private static void WaitFor(List<string> lines, object gate, string fragment, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (lines.Any(l => l.Contains(fragment)))
                    return;
            }
            Thread.Sleep(100);
        }
        lock (gate)
        {
            Assert.Fail($"Timed out waiting for audit record containing '{fragment}'. Got:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
        }
    }

    [Fact]
    public void StartVerify_CleanBackup_RecordsOpenOkAndVerifyOk()
    {
        var (lines, gate, log) = Capture();
        var (status, _) = DashboardApi.StartVerify(Config(), _repoDir, _backupId, UnlockedSession(), null, log);
        Assert.Equal(202, status);
        WaitFor(lines, gate, "AUDIT repo.open ok");
        WaitFor(lines, gate, "AUDIT restore.verify ok");
    }

    [Fact]
    public void StartVerify_UnknownBackupId_RecordsVerifyFail()
    {
        var (lines, gate, log) = Capture();
        var (status, _) = DashboardApi.StartVerify(Config(), _repoDir, "no-such-backup", UnlockedSession(), null, log);
        Assert.Equal(202, status);
        WaitFor(lines, gate, "AUDIT repo.open ok");
        WaitFor(lines, gate, "AUDIT restore.verify fail");
    }

    [Fact]
    public void StartRestore_MissingRepo_RecordsOpenFail()
    {
        // BackupRepository.Open derives the key without verifying the
        // passphrase, so a wrong passphrase would NOT fail the open
        // (it fails later at chunk decrypt) — the reliable open
        // failure is a repo that does not exist at all. IsKnownRepo
        // matches the configured job path without checking existence,
        // so the background task reaches Open and throws there.
        string missingRepo = Path.Combine(_workDir, "no-such-repo");
        var config = new ServiceConfig
        {
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "job", Repo = missingRepo },
            },
        };
        var (lines, gate, log) = Capture();
        string dest = Path.Combine(_workDir, "restored");
        var (status, _) = DashboardApi.StartRestore(config, missingRepo, "dummy-id", dest,
            MergePolicy.Overwrite, null, null, UnlockedSession(), default, null, log);
        Assert.Equal(202, status);
        WaitFor(lines, gate, "AUDIT repo.open fail");
    }

    [Fact]
    public void StartRestore_RealBackup_RecordsOpenOk()
    {
        var (lines, gate, log) = Capture();
        string dest = Path.Combine(_workDir, "restored");
        var (status, _) = DashboardApi.StartRestore(Config(), _repoDir, _backupId, dest,
            MergePolicy.Overwrite, null, null, UnlockedSession(), default, null, log);
        Assert.Equal(202, status);
        WaitFor(lines, gate, "AUDIT repo.open ok");
    }
}
