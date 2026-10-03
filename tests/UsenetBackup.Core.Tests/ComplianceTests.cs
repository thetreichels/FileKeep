using UsenetBackup.Core;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Requirements-compliance tests: exact tree round-trip (including empty
/// directories) and the append-only operation log. These cover standing
/// project requirements rather than a single milestone.
/// </summary>
public sealed class ComplianceTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public ComplianceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-cmp-test-" + Guid.NewGuid().ToString("N"));
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

    private string ReadLog() => File.ReadAllText(Path.Combine(_repoDir, "operations.log"));

    [Fact]
    public void RoundTrip_PreservesEmptyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "empty"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "empty", "nested-empty"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "has-files"));
        WriteSrc("has-files/a.txt", "x"u8.ToArray());
        WriteSrc("top.txt", "y"u8.ToArray());

        string backupId;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDirectory(_srcDir);
            backupId = manifest.BackupId;
            Assert.NotNull(manifest.Directories);
            Assert.Equal(new[] { "empty", "empty/nested-empty", "has-files" },
                         manifest.Directories);
        }

        string dest = Path.Combine(_workDir, "restored");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            repo.Restore(backupId, dest);

        Assert.True(Directory.Exists(Path.Combine(dest, "empty")));
        Assert.True(Directory.Exists(Path.Combine(dest, "empty", "nested-empty")));
        Assert.True(Directory.Exists(Path.Combine(dest, "has-files")));
        Assert.Equal("x"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "has-files", "a.txt")));
    }

    [Fact]
    public void Incremental_DirectoryAddDelete()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "gone-soon"));
        string fullId;
        using (var repo = InitRepo())
            fullId = repo.BackupDirectory(_srcDir).BackupId;

        Directory.Delete(Path.Combine(_srcDir, "gone-soon"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "brand-new"));

        string incId;
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            incId = repo.BackupIncremental(_srcDir, fullId).BackupId;

        string destInc = Path.Combine(_workDir, "restored-inc");
        string destFull = Path.Combine(_workDir, "restored-full");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            repo.Restore(incId, destInc);
            repo.Restore(fullId, destFull);
        }
        Assert.False(Directory.Exists(Path.Combine(destInc, "gone-soon")));
        Assert.True(Directory.Exists(Path.Combine(destInc, "brand-new")));
        Assert.True(Directory.Exists(Path.Combine(destFull, "gone-soon")));
    }

    [Fact]
    public void Manifest_WithoutDirectoriesField_StillVerifies()
    {
        // A flat source produces no "directories" key at all, so the
        // canonical JSON is byte-identical to what v0.1 wrote: additive
        // fields never break already-written manifests.
        WriteSrc("a.txt", "x"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDirectory(_srcDir);
            backupId = manifest.BackupId;
            Assert.Null(manifest.Directories);
        }

        string json = File.ReadAllText(Path.Combine(_repoDir, "manifests", backupId + ".json"));
        Assert.DoesNotContain("\"directories\"", json);

        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var loaded = repo.LoadManifest(backupId); // verifies root hash
            Assert.True(loaded.VerifyRootHash());
            Assert.Empty(repo.Verify(backupId));
        }
    }

    [Fact]
    public void Operations_AreLogged()
    {
        WriteSrc("a.txt", "x"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;

        string dest = Path.Combine(_workDir, "restored");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            Assert.Empty(repo.Verify(backupId));
            repo.Restore(backupId, dest);
        }

        string log = ReadLog();
        Assert.Contains("[init]", log);
        Assert.Contains("[backup]", log);
        Assert.Contains("[verify]", log);
        Assert.Contains("[restore]", log);
        Assert.Contains($"id={backupId}", log);
        // Every line is UTC-timestamped.
        foreach (string line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z \[", line);
    }

    // ------------------------------------------------------------------
    // Standing directives, extended to milestones 6-7.
    // ------------------------------------------------------------------

    private static string WithPassphraseEnv(string passphrase, Func<string> body)
    {
        string? saved = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, passphrase);
        try { return body(); }
        finally { Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, saved); }
    }

    private static ServiceConfig OneJobConfig(string repoDir, string srcDir, string schedule = "interval 60") =>
        new()
        {
            DashboardBind = "127.0.0.1",
            DashboardPort = 15789,
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "audit", Repo = repoDir, Source = srcDir,
                        Schedule = schedule, Mode = "incremental", BackupPrivilege = false },
            },
        };

    [Fact]
    public void Directives_DiskImageUsesStandardCrypto_NoInventedCrypto()
    {
        // A disk image goes through the exact same chunk pipeline as files:
        // chunk IDs are SHA-256 of plaintext, blobs are AES-256-GCM with the
        // chunk ID as AAD (nonce || ciphertext || tag).
        var rng = new byte[ChunkSize + 100];
        Random.Shared.NextBytes(rng);
        string disk = Path.Combine(_workDir, "disk.bin");
        File.WriteAllBytes(disk, rng);

        string chunkId;
        using (var repo = InitRepo())
            chunkId = repo.BackupDiskImage(disk).Files[0].Chunks[0];

        // Chunk ID is the SHA-256 of the first 64 KiB of plaintext.
        Assert.Equal(Hashing.Sha256Hex(rng.AsSpan(0, ChunkSize)), chunkId);

        byte[] blob = File.ReadAllBytes(Path.Combine(_repoDir, "chunks", chunkId[..2], chunkId[2..]));
        Assert.Equal(ChunkSize + 12 + 16, blob.Length); // plaintext + nonce + GCM tag
        Assert.NotEqual(rng.AsSpan(0, ChunkSize).ToArray(), blob); // encrypted, not stored raw
    }

    [Fact]
    public void Directives_KeysNeverStoredAlongsideBackupData()
    {
        WriteSrc("a.txt", "x"u8.ToArray());
        using (var repo = InitRepo())
            _ = repo.BackupDirectory(_srcDir).BackupId;

        // A scheduled backup also runs: nothing it touches may persist the secret.
        WithPassphraseEnv(Passphrase, () =>
        {
            var scheduler = new BackupScheduler(OneJobConfig(_repoDir, _srcDir));
            var result = scheduler.RunJobNow("audit");
            Assert.True(result.Success);
            return "";
        });

        byte[] secret = System.Text.Encoding.UTF8.GetBytes(Passphrase);
        foreach (string file in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
        {
            byte[] content = File.ReadAllBytes(file);
            Assert.False(ContainsSequence(content, secret),
                $"Passphrase bytes found in repo file {file}");
        }
    }

    [Fact]
    public void Directives_ServiceConfigCannotCarryAPassphrase()
    {
        // Defense in depth: even if someone adds a "passphrase" key to
        // service.json, there is no property to bind it to — it is ignored,
        // and the scheduler still fails fast without the environment variable.
        string json = """
            { "dashboardBind": "127.0.0.1", "dashboardPort": 15789,
              "passphrase": "hunter2",
              "jobs": [ { "name": "audit", "repo": "r", "source": "s",
                          "schedule": "interval 60", "passphrase": "hunter2" } ] }
            """;
        string path = Path.Combine(_workDir, "service.json");
        File.WriteAllText(path, json);
        var config = ServiceConfig.Load(path); // unknown keys are ignored
        Assert.Single(config.Jobs);

        string? saved = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        try
        {
            var scheduler = new BackupScheduler(config);
            var result = scheduler.RunJobNow("audit");
            Assert.False(result.Success); // not silently authenticated by the config file
            Assert.Contains(BackupScheduler.PassphraseEnvVar, result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, saved);
        }
    }

    [Fact]
    public void Directives_ScheduledBackupsAreLogged()
    {
        WriteSrc("a.txt", "x"u8.ToArray());
        using (var repo = InitRepo()) { }

        WithPassphraseEnv(Passphrase, () =>
        {
            var scheduler = new BackupScheduler(OneJobConfig(_repoDir, _srcDir));
            var ok = scheduler.RunJobNow("audit");
            Assert.True(ok.Success);

            // A failing run is logged too, as a failure (repo exists here,
            // so the failure line can be written).
            var badConfig = OneJobConfig(_repoDir, Path.Combine(_workDir, "no-src"));
            var badScheduler = new BackupScheduler(badConfig);
            var failed = badScheduler.RunJobNow("audit");
            Assert.False(failed.Success);
            return "";
        });

        string log = ReadLog();
        Assert.Contains("[scheduled-backup] job=audit", log);
        Assert.Contains("[scheduled-backup-failed] job=audit", log);
    }

    [Fact]
    public void Directives_DiskImageOperationsAreLogged()
    {
        string disk = Path.Combine(_workDir, "disk.bin");
        File.WriteAllBytes(disk, "img"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDiskImage(disk).BackupId;

        string target = Path.Combine(_workDir, "target.bin");
        File.WriteAllBytes(target, new byte[3]);
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            repo.RestoreDiskImage(backupId, target);

        string log = ReadLog();
        Assert.Contains("[backup]", log);
        Assert.Contains("[restore-disk]", log);
        Assert.Contains($"id={backupId}", log);
    }

    [Fact]
    public void Directives_FailedSchedulerRunDeletesNothing()
    {
        WriteSrc("a.txt", "x"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;

        int filesBefore = Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories).Count();
        WithPassphraseEnv(Passphrase, () =>
        {
            // Job points at the real repo but a missing source: it must fail
            // without touching repository data.
            var scheduler = new BackupScheduler(
                OneJobConfig(_repoDir, Path.Combine(_workDir, "no-src")));
            var result = scheduler.RunJobNow("audit");
            Assert.False(result.Success);
            return "";
        });
        int filesAfter = Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories).Count();

        Assert.Equal(filesBefore, filesAfter);
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            Assert.Empty(repo.Verify(backupId)); // pre-existing backup still verifies
            Assert.True(repo.LoadManifest(backupId).VerifyRootHash());
        }
    }

    [Fact]
    public void Directives_DashboardExposesNoSecrets()
    {
        WriteSrc("a.txt", "x"u8.ToArray());
        using (var repo = InitRepo()) { }

        WithPassphraseEnv(Passphrase, () =>
        {
            var scheduler = new BackupScheduler(OneJobConfig(_repoDir, _srcDir));
            Assert.True(scheduler.RunJobNow("audit").Success);

            string statusJson = System.Text.Json.JsonSerializer.Serialize(
                DashboardApi.GetStatus(scheduler));
            Assert.DoesNotContain(Passphrase, statusJson);

            var (_, logPayload) = DashboardApi.GetLog(
                OneJobConfig(_repoDir, _srcDir), _repoDir, lines: 50);
            string logJson = System.Text.Json.JsonSerializer.Serialize(logPayload);
            Assert.DoesNotContain(Passphrase, logJson);
            return "";
        });
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match)
                return true;
        }
        return false;
    }
}
