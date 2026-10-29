using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Candidate-apply migration (Config lock sites): the store Apply
/// methods in <see cref="ServiceConfigStore"/> swap a validated
/// candidate into the live config under the store lock, and the
/// DashboardApi.Config handlers then Save and reload outside it.
/// These tests pin the per-site atomicity decisions, not just the
/// happy paths:
/// - ApplyJob swaps by name (replace in place, append when new).
/// - Delete/set-paused/attach return found/not-found so handlers
///   return 400 with no Save, exactly as the old lock blocks did.
/// - SetJobPaused and AttachLocation swap a clone, never mutating
///   the live job object before commit; a no-op swaps nothing.
/// - Attach checks location first, then job, inside the lock.
/// - DeleteLocation strips + removes atomically, in job order.
/// - ApplyLocation/ApplyNntp keep the existing blob on blank
///   plaintext and protect a supplied plaintext inside the lock;
///   plaintext tests are Windows-gated (DPAPI throws on Linux),
///   where they assert the Protect call path instead.
/// - ApplyLocation's disguise check leaves the live location
///   untouched, and its field copy is today's list (RedundancyMode
///   is not copied by UpsertLocation today, and is not copied here).
/// No file IO; DPAPI is the only platform-dependent call.
/// </summary>
public sealed class ServiceConfigStoreApplyTests
{
    private static BackupJobConfig Job(string name, bool paused = false) => new()
    {
        Name = name,
        Repo = "r-" + name,
        Sources = new List<string> { "s" },
        Targets = new List<string> { "loc0" },
        Schedule = "interval 60",
        Paused = paused,
    };

    private static BackupLocation Loc(string name, string type = "usb") => new()
    {
        Name = name,
        Type = type,
        VolumeSerial = "V-" + name,
    };

    private static ServiceConfig ConfigWithJobs(params BackupJobConfig[] jobs) => new()
    {
        Jobs = jobs.ToList(),
        Locations = new List<BackupLocation> { Loc("loc0"), Loc("loc1") },
    };

    // ---- ApplyJob ----

    [Fact]
    public void ApplyJob_AppendsNewJob()
    {
        var config = ConfigWithJobs(Job("a"));
        var job = Job("b");
        config.Store.ApplyJob(job);
        Assert.Equal(new[] { "a", "b" }, config.Jobs.Select(j => j.Name));
        Assert.Same(job, config.Jobs[1]);
    }

    [Fact]
    public void ApplyJob_ReplacesByNameCaseInsensitiveInPlace()
    {
        var config = ConfigWithJobs(Job("a"), Job("b"));
        var replacement = Job("B");
        replacement.Repo = "r-new";
        config.Store.ApplyJob(replacement);
        Assert.Equal(new[] { "a", "B" }, config.Jobs.Select(j => j.Name));
        Assert.Same(replacement, config.Jobs[1]);
        Assert.Equal("r-new", config.Jobs[1].Repo);
    }

    // ---- ApplyDeleteJob ----

    [Fact]
    public void ApplyDeleteJob_RemovesFoundJobOnly()
    {
        var config = ConfigWithJobs(Job("a"), Job("b"));
        Assert.True(config.Store.ApplyDeleteJob("B"));
        Assert.Equal(new[] { "a" }, config.Jobs.Select(j => j.Name));
        Assert.False(config.Store.ApplyDeleteJob("missing"));
        Assert.Equal(new[] { "a" }, config.Jobs.Select(j => j.Name));
    }

    // ---- ApplySetJobPaused (clone swap) ----

    [Fact]
    public void ApplySetJobPaused_SwapsCloneNotLiveObject()
    {
        var config = ConfigWithJobs(Job("a"));
        var original = config.Jobs[0];
        Assert.True(config.Store.ApplySetJobPaused("a", true));
        // The live object held before the apply is untouched: the
        // swap, not an in-place mutation, is the commit.
        Assert.False(original.Paused);
        Assert.True(config.Jobs[0].Paused);
        Assert.NotSame(original, config.Jobs[0]);
        // Raw Targets are preserved, not normalized: SetPaused does
        // not touch targets, so a raw list survives the swap.
        Assert.Equal(new[] { "loc0" }, config.Jobs[0].Targets);
        Assert.False(config.Store.ApplySetJobPaused("missing", true));
    }

    [Fact]
    public void ApplySetJobPaused_NoOpSwapsNothing()
    {
        var config = ConfigWithJobs(Job("a", paused: true));
        var original = config.Jobs[0];
        Assert.True(config.Store.ApplySetJobPaused("a", true));
        Assert.Same(original, config.Jobs[0]);
    }

    // ---- ApplyAttachLocation ----

    [Fact]
    public void ApplyAttachLocation_AttachesAndIsIdempotent()
    {
        var config = ConfigWithJobs(Job("a"));
        config.Jobs[0].Targets = new List<string>();
        Assert.Equal(ServiceConfigStore.AttachLocationOutcome.Attached,
            config.Store.ApplyAttachLocation("a", "loc1"));
        Assert.Equal(new[] { "loc1" }, config.Jobs[0].Targets);
        var swapped = config.Jobs[0];
        // Already present: no swap, object identity preserved.
        Assert.Equal(ServiceConfigStore.AttachLocationOutcome.Attached,
            config.Store.ApplyAttachLocation("a", "loc1"));
        Assert.Same(swapped, config.Jobs[0]);
        Assert.Equal(new[] { "loc1" }, config.Jobs[0].Targets);
    }

    [Fact]
    public void ApplyAttachLocation_LocationCheckedBeforeJob()
    {
        var config = ConfigWithJobs(Job("a"));
        // Both unknown: location wins, as today.
        Assert.Equal(ServiceConfigStore.AttachLocationOutcome.UnknownLocation,
            config.Store.ApplyAttachLocation("missing", "missing-loc"));
        Assert.Equal(ServiceConfigStore.AttachLocationOutcome.UnknownJob,
            config.Store.ApplyAttachLocation("missing", "loc0"));
        // Neither failure mutates anything.
        Assert.Equal(new[] { "loc0" }, config.Jobs[0].Targets);
    }

    // ---- ApplyDeleteLocation ----

    [Fact]
    public void ApplyDeleteLocation_StripsAndRemovesInJobOrder()
    {
        var config = ConfigWithJobs(Job("a"), Job("b"));
        config.Jobs[1].Targets = new List<string> { "other" };
        var (found, stripped) = config.Store.ApplyDeleteLocation("LOC0");
        Assert.True(found);
        Assert.Equal(new[] { "a" }, stripped);
        Assert.Equal(new[] { "loc1" }, config.Locations.Select(l => l.Name));
        Assert.Empty(config.Jobs[0].EffectiveTargets);
        Assert.Equal(new[] { "other" }, config.Jobs[1].Targets);
        var (found2, stripped2) = config.Store.ApplyDeleteLocation("loc0");
        Assert.False(found2);
        Assert.Empty(stripped2);
    }

    // ---- ApplyLocation ----

    [Fact]
    public void ApplyLocation_CreateAddsAndClearsPlaintext()
    {
        var config = new ServiceConfig();
        var loc = Loc("new", "usb");
        var outcome = config.Store.ApplyLocation(loc);
        Assert.True(outcome.IsCreate);
        Assert.Null(outcome.DisguiseError);
        Assert.Same(loc, config.Locations[0]);
        Assert.Null(loc.PasswordPlaintext);
        Assert.Null(loc.SmbPasswordPlaintext);
    }

    [Fact]
    public void ApplyLocation_BlankKeepsExistingBlob()
    {
        var config = new ServiceConfig();
        var existing = Loc("x", "smb");
        existing.PasswordProtected = "old-blob";
        existing.SmbPasswordProtected = "old-smb-blob";
        config.Locations.Add(existing);
        var loc = Loc("x", "smb");
        loc.Share = "\\\\nas\\share";
        // Blank plaintexts: no DPAPI call, blobs kept, on any platform.
        var outcome = config.Store.ApplyLocation(loc);
        Assert.False(outcome.IsCreate);
        Assert.Null(outcome.DisguiseError);
        Assert.Equal("old-blob", existing.PasswordProtected);
        Assert.Equal("old-smb-blob", existing.SmbPasswordProtected);
        Assert.Equal("\\\\nas\\share", existing.Share);
        Assert.Null(loc.PasswordPlaintext);
    }

    [Fact]
    public void ApplyLocation_PlaintextReplacesBlobInsideLock()
    {
        var config = new ServiceConfig();
        var existing = Loc("x", "nntp");
        existing.Host = "old.example";
        existing.PasswordProtected = "old-blob";
        config.Locations.Add(existing);
        var loc = Loc("x", "nntp");
        loc.Host = "new.example";
        loc.PasswordPlaintext = "secret";
        if (!OperatingSystem.IsWindows())
        {
            // DPAPI is Windows-only: on Linux the Protect call inside
            // ApplyLocation throws, proving the plaintext path runs
            // inside the lock rather than being skipped.
            Assert.Throws<PlatformNotSupportedException>(() => config.Store.ApplyLocation(loc));
            return;
        }
        var outcome = config.Store.ApplyLocation(loc);
        Assert.Null(outcome.DisguiseError);
        Assert.NotEqual("old-blob", existing.PasswordProtected);
        Assert.NotNull(existing.PasswordProtected);
        Assert.Null(loc.PasswordPlaintext);
        Assert.Equal("new.example", existing.Host);
    }

    [Fact]
    public void ApplyLocation_DisguiseErrorLeavesLiveUntouched()
    {
        var config = new ServiceConfig();
        var existing = Loc("x", "usb");
        existing.DisguiseEnabled = true;
        existing.CoverName = "Photos";
        existing.Host = "before";
        config.Locations.Add(existing);
        var loc = Loc("x", "usb");
        loc.DisguiseEnabled = false;
        loc.CoverName = "Photos";
        loc.Host = "after";
        var outcome = config.Store.ApplyLocation(loc);
        Assert.NotNull(outcome.DisguiseError);
        Assert.False(outcome.IsCreate);
        Assert.True(existing.DisguiseEnabled);
        Assert.Equal("before", existing.Host);
        Assert.Single(config.Locations);
    }

    [Fact]
    public void ApplyLocation_FieldCopyPreservesTodaysList()
    {
        // UpsertLocation today does NOT copy RedundancyMode into an
        // existing location; ApplyLocation preserves that omission
        // rather than silently fixing it.
        var config = new ServiceConfig();
        var existing = Loc("x", "usb");
        existing.RedundancyMode = "xor";
        config.Locations.Add(existing);
        var loc = Loc("x", "usb");
        loc.RedundancyMode = "par2";
        var outcome = config.Store.ApplyLocation(loc);
        Assert.Null(outcome.DisguiseError);
        Assert.Equal("xor", existing.RedundancyMode);
    }

    // ---- ApplyNntp ----

    [Fact]
    public void ApplyNntp_BlankHostClearsWithoutSyncingUsenetLocation()
    {
        var config = new ServiceConfig();
        var usenet = Loc("Usenet", "nntp");
        usenet.Host = "old.example";
        config.Locations.Add(usenet);
        config.Nntp = new NntpConfig { Host = "old.example" };
        var outcome = config.Store.ApplyNntp(new NntpConfig { Host = "" });
        Assert.True(outcome.Cleared);
        Assert.Null(config.Nntp);
        Assert.Equal("old.example", usenet.Host);
    }

    [Fact]
    public void ApplyNntp_BlankKeepsBlobAndSyncsUsenetLocation()
    {
        var config = new ServiceConfig();
        var usenet = Loc("Usenet", "nntp");
        usenet.Host = "old.example";
        config.Locations.Add(usenet);
        config.Nntp = new NntpConfig { Host = "old.example", PasswordProtected = "old-blob" };
        var outcome = config.Store.ApplyNntp(new NntpConfig
        {
            Host = "new.example",
            Port = 119,
            Username = "u",
            Ssl = false,
            Connections = 4,
        });
        Assert.False(outcome.Cleared);
        Assert.Equal("new.example", outcome.Host);
        Assert.Equal("old-blob", config.Nntp!.PasswordProtected);
        Assert.Null(config.Nntp.PasswordPlaintext);
        Assert.Equal("new.example", usenet.Host);
        Assert.Equal(119, usenet.Port);
        Assert.False(usenet.Ssl);
        Assert.Equal("u", usenet.Username);
        Assert.Equal("old-blob", usenet.PasswordProtected);
    }

    [Fact]
    public void ApplyNntp_PlaintextReplacesBlobInsideLock()
    {
        var config = new ServiceConfig();
        config.Nntp = new NntpConfig { Host = "old.example", PasswordProtected = "old-blob" };
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                config.Store.ApplyNntp(new NntpConfig { Host = "new.example", PasswordPlaintext = "secret" }));
            return;
        }
        var outcome = config.Store.ApplyNntp(new NntpConfig { Host = "new.example", PasswordPlaintext = "secret" });
        Assert.False(outcome.Cleared);
        Assert.NotEqual("old-blob", config.Nntp!.PasswordProtected);
        Assert.Null(config.Nntp.PasswordPlaintext);
    }
}
