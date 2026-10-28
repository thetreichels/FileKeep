using System.Text;
using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Config-lifecycle split, Stage 3: the lock, the snapshot copies, and
/// the effective-provider view live in <see cref="ServiceConfigStore"/>;
/// the facade delegates. These tests pin lock identity (same object,
/// per config), copy semantics (snapshots never alias a live list),
/// the P2-C2 concurrency behavior, and the SecurityProof no-lock
/// invariant. No file IO except the source-scan test, which reads the
/// dashboard sources to pin the touch path statically.
/// </summary>
public sealed class ServiceConfigStoreTests
{
    private static BackupJobConfig Job(string name) => new()
    {
        Name = name,
        Repo = "r",
        Sources = new List<string> { "s" },
        Schedule = "interval 60",
    };

    private static BackupLocation Loc(string name) => new()
    {
        Name = name,
        Type = "usb",
        VolumeSerial = "V-" + name,
    };

    private static ServiceConfig ConfigWith(int jobs, int locations, int providers) => new()
    {
        Jobs = Enumerable.Range(0, jobs).Select(i => Job("job" + i)).ToList(),
        Locations = Enumerable.Range(0, locations).Select(i => Loc("loc" + i)).ToList(),
        NntpProviders = Enumerable.Range(0, providers).Select(i => new NntpConfig { Host = "host" + i }).ToList(),
    };

    // ---- Lock identity (invariant 2) ----

    [Fact]
    public void LockIdentity_FacadeExposesStoreLockObject()
    {
        var config = ConfigWith(1, 1, 1);
        Assert.Same(config.JobsLock, config.Store.Lock);
        // Per-config independence: a second config gets its own lock.
        var other = ConfigWith(0, 0, 0);
        Assert.NotSame(config.JobsLock, other.JobsLock);
    }

    // ---- Copy semantics (P2-C2) ----

    [Fact]
    public void Snapshots_AreCopiesNotLiveLists()
    {
        var config = ConfigWith(2, 2, 2);
        var jobs = config.SnapshotJobs();
        var locs = config.SnapshotLocations();
        var provs = config.SnapshotProviders();
        var eff = config.EffectiveProviders;

        // Never alias a live list: ToList always allocates, so a
        // regression returning the live list fails here.
        Assert.NotSame(jobs, config.Jobs);
        Assert.NotSame(locs, config.Locations);
        Assert.NotSame(provs, config.NntpProviders);
        Assert.NotSame(eff, config.NntpProviders);

        Assert.Equal(new[] { "job0", "job1" }, jobs.Select(j => j.Name));
        Assert.Equal(new[] { "loc0", "loc1" }, locs.Select(l => l.Name));
        Assert.Equal(new[] { "host0", "host1" }, provs.Select(p => p.Host));
        Assert.Equal(new[] { "host0", "host1" }, eff.Select(p => p.Host));

        // Mutating a snapshot must not touch the config.
        jobs.Add(Job("extra"));
        locs.Add(Loc("extra"));
        provs.Add(new NntpConfig { Host = "extra" });
        Assert.Equal(2, config.Jobs.Count);
        Assert.Equal(2, config.Locations.Count);
        Assert.Equal(2, config.NntpProviders.Count);

        // Facade and store agree.
        Assert.Equal(config.Store.SnapshotJobs().Select(j => j.Name), config.SnapshotJobs().Select(j => j.Name));
        Assert.Equal(config.Store.EffectiveProviders.Select(p => p.Host), config.EffectiveProviders.Select(p => p.Host));
    }

    [Fact]
    public void Snapshots_DoNotChangeWhenConfigMutates()
    {
        var config = ConfigWith(1, 1, 1);
        var jobs = config.SnapshotJobs();
        var locs = config.SnapshotLocations();
        var provs = config.SnapshotProviders();
        var eff = config.EffectiveProviders;

        lock (config.JobsLock)
        {
            config.Jobs.Add(Job("new"));
            config.Locations.Add(Loc("new"));
            config.NntpProviders.Add(new NntpConfig { Host = "new" });
        }
        Assert.Single(jobs);
        Assert.Single(locs);
        Assert.Single(provs);
        Assert.Single(eff);

        // Replacing a whole list under lock is equally invisible to
        // an old snapshot (handlers replace lists, not just items).
        lock (config.JobsLock)
        {
            config.Jobs = new List<BackupJobConfig>();
        }
        Assert.Single(jobs);
    }

    [Fact]
    public void EffectiveProviders_FallsBackToSingletonThenEmpty()
    {
        var config = ConfigWith(0, 0, 0);
        config.Nntp = new NntpConfig { Host = "solo" };
        Assert.Equal(new[] { "solo" }, config.EffectiveProviders.Select(p => p.Host));
        config.Nntp = null;
        Assert.Empty(config.EffectiveProviders);
    }

    // ---- Concurrency (P2-C2 regression) ----

    [Fact]
    public void Concurrent_SnapshotsDuringMutation_NeverTearOrAlias()
    {
        var config = ConfigWith(4, 4, 4);
        const int iterations = 200;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var jobs = config.SnapshotJobs();
                var locs = config.SnapshotLocations();
                var provs = config.SnapshotProviders();
                var eff = config.EffectiveProviders;
                // A snapshot is a copy, never the live list — a
                // regression returning the live list fails here, and
                // mutating the copy must not corrupt the config.
                Assert.NotSame(jobs, config.Jobs);
                Assert.NotSame(locs, config.Locations);
                Assert.NotSame(provs, config.NntpProviders);
                Assert.NotSame(eff, config.NntpProviders);
                jobs.Add(Job("x"));
                locs.Add(Loc("x"));
                provs.Add(new NntpConfig { Host = "x" });
            }
        })).ToArray();
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                lock (config.JobsLock)
                {
                    config.Jobs.Add(Job("w" + i));
                    config.Locations.Add(Loc("w" + i));
                    config.NntpProviders.Add(new NntpConfig { Host = "w" + i });
                    if (config.Jobs.Count > 8)
                    {
                        config.Jobs.RemoveAt(0);
                        config.Locations.RemoveAt(0);
                        config.NntpProviders.RemoveAt(0);
                    }
                    // Handlers replace whole lists as well as items.
                    if (i % 50 == 0)
                        config.Jobs = config.Jobs.ToList();
                }
            }
        });
        try
        {
            Task.WaitAll(readers.Concat(new[] { writer }).ToArray());
        }
        catch (AggregateException ex)
        {
            Assert.Fail("snapshot/mutation task faulted: " +
                string.Join("; ", ex.InnerExceptions.Select(e => e.GetType().Name + ": " + e.Message)));
        }
        Assert.All(readers, t => Assert.True(t.IsCompletedSuccessfully));
        Assert.True(writer.IsCompletedSuccessfully);
        // Final state stays sane and bounded.
        Assert.Equal(config.Jobs.Count, config.Locations.Count);
        Assert.Equal(config.Jobs.Count, config.NntpProviders.Count);
        Assert.InRange(config.Jobs.Count, 4, 9);
    }

    // ---- SecurityProof touch path takes no lock across a wait ----

    [Fact]
    public void SecurityProof_TouchPath_TakesNoLockAcrossWait()
    {
        // Static check (the plan's own option — a deadlock test would
        // be flaky by nature). Two pins:
        // (1) The gate method's file takes no lock at all, so the gate
        // itself never locks.
        // (2) The one lock on the path — SnapshotYubiKeys' list copy —
        // contains no wait call, so no lock is ever held across a
        // hardware wait. Callers were verified by review to call the
        // gate before any lock block (proof-then-lock in every site);
        // the gate's doc comment states the same rule.
        string root = FindRepoRoot();
        string proofSrc = File.ReadAllText(Path.Combine(
            root, "src", "FileKeep.Service", "Dashboard", "DashboardApi.SecurityProof.cs"));
        Assert.DoesNotContain("lock (", proofSrc);

        string protSrc = File.ReadAllText(Path.Combine(
            root, "src", "FileKeep.Service", "Dashboard", "DashboardApi.Protection.cs"));
        string copyBody = ExtractMethodBody(protSrc, "SnapshotYubiKeys");
        foreach (string wait in new[] { "Unlock(", "TryValidateLiveCode", ".Wait(", "Sleep(", ".Result", "GetAwaiter(" })
            Assert.DoesNotContain(wait, copyBody);
    }

    private static string FindRepoRoot()
    {
        // Walk up from the TEST assembly's location, not
        // AppContext.BaseDirectory: the sandbox reflection runner
        // loads this DLL from elsewhere, so the base directory points
        // at the runner. Both validated layouts (sandbox worktree,
        // laptop C:\ub\src) keep the test DLL under
        // tests/FileKeep.Core.Tests/bin/Release/net10.0.
        string? assemblyDir = Path.GetDirectoryName(typeof(ServiceConfigStoreTests).Assembly.Location);
        string[] starts = string.IsNullOrEmpty(assemblyDir)
            ? new[] { AppContext.BaseDirectory }
            : new[] { assemblyDir, AppContext.BaseDirectory };
        foreach (string start in starts)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(
                        dir.FullName, "src", "FileKeep.Service", "Dashboard", "DashboardApi.SecurityProof.cs")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        Assert.Fail("Could not locate the FileKeep repo root from " + assemblyDir +
            " — the touch-path static check needs the dashboard sources.");
        throw new InvalidOperationException("unreachable");
    }

    private static string ExtractMethodBody(string src, string methodName)
    {
        int sig = src.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(sig >= 0, "method " + methodName + " not found in source");
        // Method end = first line that is exactly four spaces + '}'
        // after the signature. The body contains no such line except
        // its own close (inner blocks are deeper).
        var body = new StringBuilder();
        int pos = src.IndexOf('\n', sig);
        Assert.True(pos >= 0, "method " + methodName + " has no body");
        while (pos < src.Length)
        {
            int eol = src.IndexOf('\n', pos + 1);
            string line = (eol < 0 ? src[(pos + 1)..] : src[(pos + 1)..eol]).TrimEnd('\r');
            body.AppendLine(line);
            if (line == "    }")
                return body.ToString();
            if (eol < 0)
                break;
            pos = eol;
        }
        Assert.Fail("method " + methodName + " body end not found");
        throw new InvalidOperationException("unreachable");
    }
}
