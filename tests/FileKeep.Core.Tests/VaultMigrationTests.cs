using System.Security.Cryptography;
using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Vault migration (the migrate half of rekey): fresh repos under
/// the new secret, jobs re-pointed, previous vaults recorded — and,
/// critically, FRESH pools for shared-pool destinations (SMB/USB
/// chunk pools are keyed by chunk ID alone, so reusing them across
/// keys would corrupt one vault or the other). Usenet is append-only
/// and shared safely. The tests use local temp dirs only: no share
/// or drive is ever touched — mapping creates config objects, and
/// repo Init writes local repo.json files.
/// </summary>
public sealed class VaultMigrationTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string CurrentSecret = "the-current-passphrase";

    public VaultMigrationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, CurrentSecret);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string ConfigPath() => Path.Combine(_workDir, "service-" + Guid.NewGuid().ToString("N") + ".json");

    private string RepoPath(string name) => Path.Combine(_workDir, "repos", name);

    private ServiceConfig ConfigWithJobs()
    {
        var config = new ServiceConfig();
        config.Locations.Add(new BackupLocation { Name = "Usenet", Type = "nntp", Host = "news.example.invalid" });
        config.Locations.Add(new BackupLocation { Name = "NAS", Type = "smb", Share = @"\\NAS\backups\fk", SmbUser = "u" });
        config.Locations.Add(new BackupLocation { Name = "Stick", Type = "usb", VolumeSerial = "ABCDEF", UsbFolder = "FileKeep" });
        config.Jobs.Add(new BackupJobConfig
        {
            Name = "job1", Repo = RepoPath("job1"),
            Sources = new List<string> { @"C:\src1" }, Schedule = "daily 02:00",
            Targets = new List<string> { "Usenet", "NAS", "Stick" },
        });
        config.Jobs.Add(new BackupJobConfig
        {
            Name = "job2", Repo = RepoPath("job1"), // Shares job1's repo.
            Sources = new List<string> { @"C:\src2" }, Schedule = "daily 03:00",
            Targets = new List<string> { "NAS" },
        });
        config.Jobs.Add(new BackupJobConfig
        {
            Name = "job3", Repo = RepoPath("job3"),
            Sources = new List<string> { @"C:\src3" }, Schedule = "daily 04:00",
            Targets = new List<string> { "Usenet" },
        });
        return config;
    }

    [Fact]
    public void Migrate_RepointsJobs_FreshPools_PreviousRecorded()
    {
        var config = ConfigWithJobs();
        string configPath = ConfigPath();
        string oldRepo1 = RepoPath("job1");
        string oldRepo3 = RepoPath("job3");

        var (status, payload) = DashboardApi.MigrateVaults(config, configPath, newSecret: null,
            yubiKeyProvider: null, proofPassphrase: CurrentSecret);
        Assert.Equal(200, status);
        var summary = Assert.IsType<VaultMigrationSummaryDto>(payload);
        Assert.Equal(3, summary.JobsMigrated);
        Assert.Equal(2, summary.Repos.Count);
        Assert.Equal(2, summary.LocationsCreated.Count);
        Assert.Empty(summary.Warnings);

        var job1 = config.Jobs[0];
        var job2 = config.Jobs[1];
        var job3 = config.Jobs[2];
        // Shared repo migrated once, both jobs re-pointed together.
        Assert.Equal(job1.Repo, job2.Repo);
        Assert.NotEqual(oldRepo1, job1.Repo);
        Assert.NotEqual(oldRepo3, job3.Repo);
        Assert.True(File.Exists(Path.Combine(job1.Repo, "repo.json")));
        Assert.True(File.Exists(Path.Combine(job3.Repo, "repo.json")));
        // New repos encrypt under the current secret: back up one
        // file, then prove its staged chunk decrypts under the
        // current secret's key and not under a wrong one. (Open
        // itself is lazy — it derives the key without verifying it —
        // so opening under a wrong passphrase proves nothing.)
        string sourceDir = Path.Combine(_workDir, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "hello.txt"), "hello migrated vault");
        using (var repo = BackupRepository.Open(job1.Repo, CurrentSecret))
        {
            repo.BackupDirectory(sourceDir);
        }
        string[] chunkFiles = Directory.GetFiles(Path.Combine(job1.Repo, "chunks"), "*", SearchOption.AllDirectories);
        Assert.NotEmpty(chunkFiles);
        string chunkId = Path.GetFileName(Path.GetDirectoryName(chunkFiles[0])!) + Path.GetFileName(chunkFiles[0]);
        byte[] blob = File.ReadAllBytes(chunkFiles[0]);
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(job1.Repo, "repo.json"))))
        {
            byte[] salt = Convert.FromBase64String(doc.RootElement.GetProperty("kdf_salt_b64").GetString()!);
            int iterations = doc.RootElement.GetProperty("kdf_iterations").GetInt32();
            byte[] rightKey = KeyDerivation.DeriveKey(CurrentSecret, salt, iterations);
            byte[] wrongKey = KeyDerivation.DeriveKey("wrong-passphrase", salt, iterations);
            _ = ChunkCrypto.Decrypt(blob, rightKey, Hashing.HexToBytes(chunkId));
            Assert.ThrowsAny<CryptographicException>(() => ChunkCrypto.Decrypt(blob, wrongKey, Hashing.HexToBytes(chunkId)));
        }

        // Usenet shared; SMB/USB got fresh pools.
        var nasNew = config.Locations.First(l => l.Type == "smb" && l.Name != "NAS");
        var stickNew = config.Locations.First(l => l.Type == "usb" && l.Name != "Stick");
        Assert.Equal(new[] { "Usenet", nasNew.Name, stickNew.Name }, job1.Targets!);
        Assert.Equal(new[] { nasNew.Name }, job2.Targets!);
        Assert.Equal(@"\\NAS\backups\fk-migrated", nasNew.Share);
        Assert.Equal("u", nasNew.SmbUser);
        Assert.Equal("ABCDEF", stickNew.VolumeSerial);
        Assert.Equal("FileKeep-migrated", stickNew.UsbFolder);

        // Previous vaults recorded, old paths kept.
        Assert.Equal(2, config.PreviousVaults.Count);
        var prev1 = config.PreviousVaults.First(r => r.OldRepo == oldRepo1);
        Assert.Equal(job1.Repo, prev1.NewRepo);
        Assert.Equal(new[] { "job1", "job2" }, prev1.Jobs);
        Assert.Contains("NAS", prev1.OldTargets);

        // The save landed on disk.
        Assert.Contains("previousVaults", File.ReadAllText(configPath));
    }

    [Fact]
    public void Migrate_RefusesWhenJobHasNoRepo()
    {
        var config = ConfigWithJobs();
        config.Jobs.Add(new BackupJobConfig
        {
            Name = "job4", Repo = "",
            Sources = new List<string> { @"C:\src4" }, Schedule = "daily 05:00",
        });
        var (status, _) = DashboardApi.MigrateVaults(config, ConfigPath(), newSecret: null,
            yubiKeyProvider: null, proofPassphrase: CurrentSecret);
        Assert.Equal(400, status);
        // Nothing moved: the refusal comes before any Init or mutation.
        Assert.Equal(RepoPath("job1"), config.Jobs[0].Repo);
        Assert.Empty(config.PreviousVaults);
        Assert.Equal(3, config.Locations.Count);
    }

    [Fact]
    public void Migrate_RefusesWithoutProof()
    {
        var config = ConfigWithJobs();
        var (status, _) = DashboardApi.MigrateVaults(config, ConfigPath(), newSecret: null,
            yubiKeyProvider: null);
        Assert.Equal(403, status);
        Assert.Equal(RepoPath("job1"), config.Jobs[0].Repo);
        Assert.Empty(config.PreviousVaults);
    }

    [Fact]
    public void Migrate_RefusesInGeneratedMode()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        try
        {
            var config = new ServiceConfig
            {
                GeneratedSecretMode = true,
                YubiKeyChallenge = "challenge",
                PassphraseYubiKeyWrapped = "wrapped",
            };
            config.Jobs.Add(new BackupJobConfig { Name = "job1", Repo = RepoPath("job1") });
            var (status, payload) = DashboardApi.MigrateVaults(config, ConfigPath(), newSecret: null,
                yubiKeyProvider: null, proofPassphrase: secret);
            Assert.Equal(400, status);
            var prop = payload.GetType().GetProperty("Error") ?? payload.GetType().GetProperty("error");
            Assert.Contains("passphrase-mode operation", (string?)prop?.GetValue(payload) ?? "");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, CurrentSecret);
        }
    }

    [Fact]
    public void Migrate_RefusesWhenTargetUnmappable()
    {
        var config = new ServiceConfig();
        config.Locations.Add(new BackupLocation { Name = "BrokenNAS", Type = "smb", Share = "" });
        config.Jobs.Add(new BackupJobConfig
        {
            Name = "job1", Repo = RepoPath("job1"),
            Sources = new List<string> { @"C:\src1" }, Schedule = "daily 02:00",
            Targets = new List<string> { "BrokenNAS" },
        });

        var (status, payload) = DashboardApi.MigrateVaults(config, ConfigPath(), newSecret: null,
            yubiKeyProvider: null, proofPassphrase: CurrentSecret);
        Assert.Equal(400, status);
        var prop = payload.GetType().GetProperty("Error") ?? payload.GetType().GetProperty("error");
        Assert.Contains("BrokenNAS", (string?)prop?.GetValue(payload) ?? "");
        // Nothing moved: the refusal comes before any Init or mutation.
        Assert.Equal(RepoPath("job1"), config.Jobs[0].Repo);
        Assert.Single(config.Locations);
        Assert.Empty(config.PreviousVaults);
    }

    [Fact]
    public void Migrate_TypedSecretWarnsAboutPassphraseMismatch()
    {
        var config = ConfigWithJobs();
        var (status, payload) = DashboardApi.MigrateVaults(config, ConfigPath(), newSecret: "typed-new-secret",
            yubiKeyProvider: null, proofPassphrase: CurrentSecret);
        Assert.Equal(200, status);
        var summary = Assert.IsType<VaultMigrationSummaryDto>(payload);
        Assert.Single(summary.Warnings);
        Assert.Contains("not the currently configured secret", summary.Warnings[0]);
        // New repos open under the typed secret, not the current one.
        using (var repo = BackupRepository.Open(config.Jobs[0].Repo, "typed-new-secret")) { Assert.NotNull(repo); }
    }
}
