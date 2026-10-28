using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Structured audit records (security review rec 6): format, the
/// null-sink contract, and the synchronous wiring points — unlock
/// attempts, failed proofs, and mode changes. Repo-open and verify
/// records (background tasks) are covered by
/// <see cref="AuditRepoTests"/>. All secret-shaped values below are
/// fakes invented for these tests.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    public AuditLogTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "fk-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string ConfigPath() => Path.Combine(_workDir, "service-" + Guid.NewGuid().ToString("N") + ".json");

    private static (List<string> Lines, Action<string> Log) Capture()
    {
        var lines = new List<string>();
        return (lines, line => lines.Add(line));
    }

    [Fact]
    public void Format_IsStableGreppableLine()
    {
        Assert.Equal("AUDIT unlock.attempt ok method=passphrase",
            AuditLog.Format(AuditLog.UnlockAttempt, AuditLog.Ok, "method=passphrase"));
        Assert.Equal("AUDIT config.load fail path=/tmp/service.json",
            AuditLog.Format(AuditLog.ConfigLoad, AuditLog.Fail, "path=/tmp/service.json"));
    }

    [Fact]
    public void Record_NullSink_IsNoOp()
    {
        AuditLog.Record(null, AuditLog.UnlockAttempt, AuditLog.Ok, "method=passphrase");
    }

    [Fact]
    public void UnlockAttempt_Failure_Recorded_NoSecretLeak()
    {
        // Fake long blob as the stored (unresolvable) passphrase copy
        // and a fake 52-char secret as the attempted passphrase: the
        // audit record must mention neither.
        string fakeBlob = new string('Z', 120);
        string fakeSecret = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRST";
        var config = new ServiceConfig { PassphraseProtected = fakeBlob };
        var (lines, log) = Capture();
        var session = new RestoreSession();
        var (status, _) = DashboardApi.UnlockRestoreSessionWithPassphrase(config, session, fakeSecret, log);
        Assert.Equal(400, status);
        Assert.Single(lines.Where(l => l.StartsWith("AUDIT ")));
        Assert.Equal("AUDIT unlock.attempt fail method=passphrase", lines[0]);
        Assert.DoesNotContain(fakeBlob, lines[0]);
        Assert.DoesNotContain(fakeSecret, lines[0]);
    }

    [Fact]
    public void UnlockAttempt_Success_Recorded()
    {
        // Proof resolves through the env var (checked first), so a
        // fake stored blob does not block the unlock.
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "test-pass-123");
        var config = new ServiceConfig { PassphraseProtected = new string('Z', 120) };
        var (lines, log) = Capture();
        var session = new RestoreSession();
        var (status, _) = DashboardApi.UnlockRestoreSessionWithPassphrase(config, session, "test-pass-123", log);
        Assert.Equal(200, status);
        Assert.Contains("AUDIT unlock.attempt ok method=passphrase", lines);
    }

    [Fact]
    public void SecurityChangeProof_Refusal_RecordedAsAuthFailure()
    {
        // A method is enabled (fake stored blob) but no proof is
        // supplied, so the gate refuses — and audits the refusal.
        var config = new ServiceConfig { PassphraseProtected = new string('Z', 120) };
        var (lines, log) = Capture();
        var refusal = DashboardApi.CheckSecurityChangeProof(config, null, null, false, log, null);
        Assert.NotNull(refusal);
        Assert.Equal(403, refusal.Value.Status);
        Assert.Contains("AUDIT auth.fail fail proof=security-change", lines);
    }

    [Fact]
    public void ModeChange_ProfileSave_Recorded()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "test-pass-123");
        var config = new ServiceConfig { PassphraseProtected = new string('Z', 120) };
        var (lines, log) = Capture();
        var request = new ProtectionProfileRequest
        {
            PassphraseEnabled = true,
            YubiKeyEnabled = false,
            PhoneEnabled = false,
            LinkMode = "or",
            ProofPassphrase = "test-pass-123",
        };
        var (status, _) = DashboardApi.SetProtectionProfile(config, ConfigPath(), request, log, null);
        Assert.Equal(200, status);
        Assert.Contains("AUDIT security.mode ok passphrase=True yubikey=False phone=False link=or", lines);
    }
}
