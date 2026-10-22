using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Milestone 7: the dashboard's endpoint logic (status shaping, job lookup,
/// repo scoping, log tailing). The logic lives in transport-free
/// <see cref="DashboardApi"/> so it is testable without binding TCP —
/// loopback TCP from a test process is blocked in this sandbox, and the
/// Kestrel wiring itself is verified by smoke-testing the service binary.
/// </summary>
public sealed class DashboardTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string? _savedPassphrase;

    public DashboardTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(Path.Combine(_workDir, "share"));
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "dash"u8.ToArray());
        BackupRepository.Init(_repoDir, Passphrase, 64 * 1024, 10_000).Dispose();
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private ServiceConfig MakeConfig() => new()
    {
        DashboardBind = "127.0.0.1",
        DashboardPort = 15789,
        // The job targets a local-folder share so runs really deliver.
        // (It used to target a bare "nntp" with no providers configured
        // and "succeed" vacuously — DL-3 removed that path; a
        // provider-less run now fails, as it should.)
        Locations = new List<BackupLocation>
        {
            new() { Name = "vault", Type = "smb",
                    Share = Path.Combine(_workDir, "share") },
        },
        Jobs = new List<BackupJobConfig>
        {
            new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                    Schedule = "interval 60", Mode = "incremental", BackupPrivilege = false,
                    Targets = new List<string> { "vault" } },
        },
    };

    private static JsonDocument ToJson(object payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload));

    [Fact]
    public void ApiStatus_ReflectsSchedulerState()
    {
        var scheduler = new BackupScheduler(MakeConfig());
        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success);

        using var doc = ToJson(DashboardApi.GetStatus(scheduler));
        var job = doc.RootElement.GetProperty("jobs")[0];
        Assert.Equal("docs", job.GetProperty("name").GetString());
        Assert.Equal("interval 60", job.GetProperty("schedule").GetString());
        Assert.Equal("incremental", job.GetProperty("mode").GetString());
        var last = job.GetProperty("lastResult");
        Assert.True(last.GetProperty("success").GetBoolean());
        Assert.Equal(result.BackupId, last.GetProperty("backupId").GetString());
        Assert.Equal(1, last.GetProperty("files").GetInt32());
    }

    [Fact]
    public void IsKnownJob_MatchesCaseInsensitively()
    {
        var scheduler = new BackupScheduler(MakeConfig());
        Assert.True(DashboardApi.IsKnownJob(scheduler, "docs"));
        Assert.True(DashboardApi.IsKnownJob(scheduler, "DOCS"));
        Assert.False(DashboardApi.IsKnownJob(scheduler, "nope"));
    }

    [Fact]
    public void GetBackups_ListsBackupsAndScopesRepos()
    {
        var config = MakeConfig();
        var scheduler = new BackupScheduler(config);
        scheduler.RunJobNow("docs");

        var (status, payload) = DashboardApi.GetBackups(config, _repoDir);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("full", doc.RootElement[0].GetProperty("type").GetString());
        // a.txt is 4 bytes -> one catalogued chunk -> storedBytes 4.
        Assert.Equal(4, doc.RootElement[0].GetProperty("storedBytes").GetInt64());

        var (bad, _) = DashboardApi.GetBackups(config, "/tmp");
        Assert.Equal(400, bad);
    }

    [Fact]
    public void GetBackups_RequiresPassphrase()
    {
        var config = MakeConfig();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        try
        {
            var (status, _) = DashboardApi.GetBackups(config, _repoDir);
            Assert.Equal(500, status);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
        }
    }

    [Fact]
    public void GetLog_TailsOperationsLogAndScopesRepos()
    {
        var config = MakeConfig();
        var (status, payload) = DashboardApi.GetLog(config, _repoDir, lines: 10);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.True(doc.RootElement.GetArrayLength() >= 1);
        Assert.Contains("init", doc.RootElement[0].GetString());

        // Clamp: asking for 5000 lines returns at most 1000.
        var (_, big) = DashboardApi.GetLog(config, _repoDir, lines: 5000);
        using var bigDoc = ToJson(big);
        Assert.True(bigDoc.RootElement.GetArrayLength() <= 1000);

        var (bad, _) = DashboardApi.GetLog(config, "/tmp");
        Assert.Equal(400, bad);
    }

    [Fact]
    public void HtmlPage_ContainsDashboardElements()
    {
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("<title>FileKeep</title>", html);
        Assert.Contains("name=\"csrf-token\" content=\"test-csrf-token\"", html);
        Assert.Contains("X-CSRF-Token", html);
        Assert.Contains("/api/status", html);
        Assert.Contains("/api/jobs/", html);
        Assert.Contains("/api/backups", html);
        Assert.Contains("/api/log", html);
    }

    [Fact]
    public void HtmlPage_VaultHasJobRepoSwitcher()
    {
        // WALK-7: the Banked Vault needs a per-job repo switcher. Without
        // a #repo select in the page, currentRepo() can only ever return
        // the first job's repo, so every other job's backups are
        // invisible — and unrestorable — in the GUI.
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("id=\"vault-repo-row\"", html);
        Assert.Contains("id=\"repo\"", html);
        Assert.Contains("onchange=\"loadRepo()\"", html);
    }

    [Fact]
    public void HtmlPage_LinkBand_FollowsTheMockup()
    {
        // The walkthrough mockup: ONE pill in both states (full width,
        // flush with the cards, facing corners squared). Linked: solid,
        // "Linked — AND". Unlinked: outline, OR tag + "Link". The old
        // two-div version also showed the linked band with display '',
        // dropping its flex centering so the label collapsed to the
        // left edge — showing the pill must set 'flex'.
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("class=\"link-pill is-linked\"", html);
        Assert.Contains("id=\"sec-band-or\"", html);
        Assert.Contains("band.style.display = bothOn ? 'flex' : 'none'", html);
        Assert.Contains("label.textContent = isLinked ? 'Linked — AND' : 'Link'", html);
        Assert.DoesNotContain("sec-link-band-unlinked", html);
    }

    [Fact]
    public void HtmlPage_SidebarWidthIs230()
    {
        // Joel's call (2026-10-21): the nav was 300px — noticeably
        // slimmer at 230px while the sidebar Operations Log stays
        // readable. Pinned so a layout pass doesn't silently widen it.
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("nav { width: 230px;", html);
    }

    [Fact]
    public void HtmlPage_InsightPanelAlignsWithMethodCards()
    {
        // Joel's call (2026-10-21): the "What this means" box must
        // align with the selection cards, not with the "Build your
        // protection" heading. The heading therefore sits ABOVE the
        // flex row, so the row's left column starts with the first
        // card and the insight card (flex-start) shares its top edge.
        string html = DashboardHtml.Page("test-csrf-token");
        int heading = html.IndexOf("Build your protection", StringComparison.Ordinal);
        int row = html.IndexOf("display:flex;gap:20px;align-items:flex-start", StringComparison.Ordinal);
        int firstCard = html.IndexOf("id=\"sec-card-passphrase\"", StringComparison.Ordinal);
        int panel = html.IndexOf("id=\"igPanel\"", StringComparison.Ordinal);
        Assert.True(heading >= 0 && row > heading && firstCard > row && panel > row,
            "heading must precede the flex row; cards and panel live inside it");
    }

    [Fact]
    public void HtmlPage_InsightPanelHasNoIconRow()
    {
        // Joel's call (2026-10-21): the method icon row that sat
        // between the "What this means" heading and the infograph
        // diagram is removed. The diagram and its card/band hover
        // previews stay; nothing may reference the row any more.
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("id=\"igPanel\"", html);
        Assert.Contains("id=\"igYou\"", html);
        Assert.DoesNotContain("igKeys", html);
        Assert.DoesNotContain("CUE_ICONS", html);
        Assert.DoesNotContain("ig-kname", html);
    }

    [Fact]
    public void HtmlPage_SecurityNoticesAreToastsNotBanner()
    {
        // Joel's call (2026-10-21): the "Switched to YubiKey — at
        // least one..." explanation (and its siblings) must arrive as
        // a sliding error-style toast, not an in-flow banner box that
        // appears at the top of the step and moves the layout.
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.DoesNotContain("id=\"sec-banner\"", html);
        int start = html.IndexOf("function secShowBanner(msg)", StringComparison.Ordinal);
        Assert.True(start >= 0, "secShowBanner not found in the page");
        int end = html.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, "secShowBanner body not terminated as expected");
        string body = html.Substring(start, end - start);
        Assert.Contains("toast(msg, 'err')", body);
    }

    [Fact]
    public void HtmlPage_YubiKeyCardTextIsStatic()
    {
        // Joel's call (2026-10-21): the YubiKey card showed a long
        // "No YubiKey?..." reassurance panel only while the method was
        // off, so toggling it changed the card's text and height and
        // the layout jumped. The card now reads the same in both
        // states: "Physical key — touch to unlock."
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("Physical key — touch to unlock.", html);
        Assert.DoesNotContain("sec-yk-reassurance", html);
        Assert.DoesNotContain("No YubiKey? No problem", html);
    }

    [Fact]
    public void HtmlPage_LinkToggleIsIntentOnly()
    {
        // The link pill must NOT save on click: on a fresh system the
        // profile save it fired carried passphraseEnabled=true before
        // any passphrase was configured, so both linking and unlinking
        // failed with "No passphrase is configured..." The choice is
        // intent, persisted by Finish / setup-step saves (secSaveProfile
        // is called there with the same secProfile.linkMode).
        string html = DashboardHtml.Page("test-csrf-token");
        int start = html.IndexOf("function secToggleLink()", StringComparison.Ordinal);
        Assert.True(start >= 0, "secToggleLink not found in the page");
        int end = html.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, "secToggleLink body not terminated as expected");
        string body = html.Substring(start, end - start);
        Assert.Contains("secUpdateLinkBand()", body);
        Assert.DoesNotContain("secSaveProfile", body);
    }

    [Fact]
    public void HtmlPage_BakesBuildTokenAndSidebarCheck()
    {
        string html = DashboardHtml.Page("test-csrf-token");
        // The build placeholder must be fully substituted: an unsubstituted
        // "{{APP_VERSION}}" would bake in as a literal build token and the
        // sidebar stale-page check would reload on every navigation.
        Assert.Contains("name=\"app-build\"", html);
        Assert.False(html.Contains("{{APP_VERSION}}"), "APP_VERSION placeholder was not substituted");
        // The sidebar stale-page gate ships in the page.
        Assert.Contains("reloadIfBuildChanged", html);
        // The build token resolves without throwing and is stable across calls.
        string token = DashboardApi.GetBuildToken();
        Assert.NotNull(token);
        Assert.Equal(token, DashboardApi.GetBuildToken());
    }

    [Fact]
    public void HtmlPage_JobsView_CardLayoutAndRelocatedAddButton()
    {
        string html = DashboardHtml.Page("test-csrf-token");
        // Card-per-job layout pieces ship in the page: the summary strip,
        // the in-list toolbar, the empty state, and the per-card live
        // upload-progress slot the poll writes into.
        Assert.Contains("id=\"jobs-summary\"", html);
        Assert.Contains("jobs-toolbar", html);
        Assert.Contains("No backup jobs yet", html);
        Assert.Contains("data-jobprog", html);
        // Joel's button spec: no add-job button in the page header — the
        // "New backup job" buttons are the ones loadSettings renders into
        // the jobs list itself (toolbar at the head of the list, and the
        // empty state beside its text). The heading stands alone.
        int h1 = html.IndexOf("<h1>Backup jobs</h1>");
        Assert.True(h1 >= 0, "Backup jobs heading missing");
        int len = html.Length - h1;
        if (len > 400) len = 400;
        Assert.False(html.Substring(h1, len).Contains("New backup job"),
            "An add-job button sits by the jobs page heading; it belongs in the jobs list");
        // The locations card carries a disk-usage meter slot per row,
        // filled lazily from the location-disk endpoint.
        Assert.Contains("data-locmeter", html);
        Assert.Contains("/api/config/location-disk", html);
    }

    [Fact]
    public void ProbeDiskUsage_RealRootReportsSpace_BogusPathReturnsNull()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var usage = DashboardApi.ProbeDiskUsage(root);
        Assert.NotNull(usage);
        Assert.True(usage.Value.Total > 0);
        Assert.InRange(usage.Value.Free, 0, usage.Value.Total);
        Assert.Null(DashboardApi.ProbeDiskUsage("/definitely-not-a-real-drive-xyz/"));
        Assert.Null(DashboardApi.ProbeDiskUsage(""));
    }

    [Fact]
    public void GetLocationDiskUsage_NonDiskLocations_ReturnsEmpty()
    {
        // A Usenet location has no disk to measure, and an unplugged
        // USB location resolves to nothing: both stay off the list,
        // which is what keeps their meters hidden in the card.
        var config = new ServiceConfig
        {
            Locations = new List<BackupLocation>
            {
                new() { Name = "Usenet", Type = "nntp" },
                new() { Name = "Stick", Type = "usb", VolumeSerial = "DEADBEEF" },
            },
        };
        var (status, payload) = DashboardApi.GetLocationDiskUsage(config);
        Assert.Equal(200, status);
        Assert.Empty((LocationDiskDto[])payload);
    }

    [Fact]
    public void ValidateCsrfToken_AcceptsOnlyExactMatch()
    {
        Assert.True(DashboardApi.ValidateCsrfToken("abc123", "abc123"));
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", "abc124"));
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", "abc12"));   // length differs
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", "abc1234")); // length differs
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", null));
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", ""));
        Assert.False(DashboardApi.ValidateCsrfToken("", "abc123"));
    }

    // ---------- walk fixes (2026-10-20 GUI verification walk) ----------

    [Fact]
    public void ResolveRestoreSmb_UsesJobsLocationWhenRequestIsBlank()
    {
        // WALK-3: a location-based job's restore must source its share
        // and user from the job's SMB target location — the job's own
        // legacy fields are empty for such jobs.
        var config = MakeConfig();
        var job = config.Jobs[0];
        job.Targets = new List<string> { "vault" };
        job.SmbUser = "";
        config.Locations = new List<BackupLocation>
        {
            new()
            {
                Name = "vault", Type = "smb",
                Share = @"\\nas\backups", SmbUser = @"nas\alice",
            },
        };
        var (share, user, _) = DashboardApi.ResolveRestoreSmb(config, _repoDir, null, null);
        Assert.Equal(@"\\nas\backups", share);
        Assert.Equal(@"nas\alice", user);
    }

    [Fact]
    public void ResolveRestoreSmb_ExplicitRequestWins()
    {
        var config = MakeConfig();
        config.Jobs[0].Targets = new List<string> { "vault" };
        config.Locations = new List<BackupLocation>
        {
            new()
            {
                Name = "vault", Type = "smb",
                Share = @"\\nas\backups", SmbUser = @"nas\alice",
            },
        };
        var (share, user, _) = DashboardApi.ResolveRestoreSmb(
            config, _repoDir, @"\\other\share", "bob");
        Assert.Equal(@"\\other\share", share);
        Assert.Equal("bob", user);
    }

    [Fact]
    public void GetWinReBuildStatus_ReturnsNamedDtoWithThePolledContract()
    {
        // WALK-9: the status used to be an anonymous type returned
        // through Results.Json — under the trimmed service that
        // serialized as HTTP 500 and the dashboard showed no build
        // progress. It must be the named DTO carrying the exact
        // camelCase fields checkWinReStatus polls for.
        var status = DashboardApi.GetWinReBuildStatus();
        Assert.IsType<WinReBuildStatusDto>(status);
        // Serialize through the app's source-generated context — the
        // trimmed service has no reflection fallback, so a DTO missing
        // from DashboardJsonContext 500s in production while passing a
        // plain reflection-based serialization here. (First version of
        // this fix missed the registration and did exactly that.)
        string json = JsonSerializer.Serialize(status,
            DashboardJsonContext.Default.WinReBuildStatusDto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("idle", root.GetProperty("status").GetString());
        Assert.Equal("", root.GetProperty("error").GetString());
        Assert.Equal("", root.GetProperty("drive").GetString());
        Assert.Equal(0, root.GetProperty("percent").GetInt32());
        Assert.Equal("", root.GetProperty("currentStep").GetString());
        Assert.Equal(0, root.GetProperty("estimatedRemainingSec").GetInt32());
        Assert.Equal(0, root.GetProperty("elapsedSec").GetInt32());
        Assert.Equal(0, root.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public void ScanUsbForBackups_ResultSerializesThroughDashboardContext()
    {
        // SF-1: ScanBackupsResultDto (and its nested DiscoveredBackupDto)
        // were registered in NO source-gen context, so the trimmed
        // service 500'd both vault scan endpoints while the in-process
        // auto-scan worked. Serialize the real payload through the app's
        // context, like the WALK-9 status test.
        var config = MakeConfig();
        var (status, payload) = DashboardApi.ScanUsbForBackups(config, _workDir);
        Assert.Equal(200, status);
        var dto = Assert.IsType<ScanBackupsResultDto>(payload);
        string json = JsonSerializer.Serialize(dto,
            DashboardJsonContext.Default.ScanBackupsResultDto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(_workDir, root.GetProperty("scannedPath").GetString());
        Assert.Equal(0, root.GetProperty("found").GetInt32());
        Assert.Equal(0, root.GetProperty("unknown").GetArrayLength());
    }

    [Fact]
    public void WinRePrerequisites_SerializesThroughDashboardContext()
    {
        // SF-24: the prerequisites payload used to be a raw Dictionary —
        // no source-gen metadata, a 500 trap for the first caller. (On
        // this Linux sandbox the non-Windows branch answers.)
        var dto = DashboardApi.CheckWinRePrerequisites();
        Assert.IsType<WinRePrerequisitesDto>(dto);
        string json = JsonSerializer.Serialize(dto,
            DashboardJsonContext.Default.WinRePrerequisitesDto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // The serialization coverage (SF-24) is the point; the branch
        // values differ by platform — the original asserts described
        // only the non-Windows branch and failed on a real Windows box.
        Assert.Equal(OperatingSystem.IsWindows(),
            root.GetProperty("isWindows").GetBoolean());
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(root.GetProperty("ready").GetBoolean());
            Assert.Contains("Windows", root.GetProperty("error").GetString());
            Assert.Equal(0, root.GetProperty("missing").GetArrayLength());
        }
    }

    [Fact]
    public void ResolveRestoreUsb_NoUsbTarget_ReturnsNull()
    {
        // WALK-8: a job without a USB target has no USB chunk source.
        var config = MakeConfig();
        Assert.Null(DashboardApi.ResolveRestoreUsb(config, _repoDir));
    }

    [Fact]
    public void ResolveRestoreUsb_UsbTargetButDriveAbsent_ReturnsNull()
    {
        // The job targets a USB location, but no plugged-in drive carries
        // that volume serial — the resolver must come back empty rather
        // than guessing a drive letter.
        var config = MakeConfig();
        config.Jobs[0].Targets = new List<string> { "stick" };
        config.Locations.Add(new BackupLocation
        {
            Name = "stick", Type = "usb",
            VolumeSerial = "DEADBEEF", UsbFolder = "FileKeep",
        });
        Assert.Null(DashboardApi.ResolveRestoreUsb(config, _repoDir));
    }

    // ---------- DL-6: disk restore stages chunks before writing ----------

    private string MakeDiskImageBackup(byte[] imageBytes)
    {
        string disk = Path.Combine(_workDir, "disk.bin");
        File.WriteAllBytes(disk, imageBytes);
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        return repo.BackupDiskImage(disk, "disk.img").BackupId;
    }

    private ServiceConfig MakeDiskRestoreConfig(string shareDir)
    {
        // The job targets an SMB location holding the backup's chunks —
        // the credential home a disk restore resolves its source from.
        var config = MakeConfig();
        config.Jobs[0].Targets = new List<string> { "vault" };
        config.Locations = new List<BackupLocation>
        {
            new() { Name = "vault", Type = "smb", Share = shareDir },
        };
        return config;
    }

    private static void CopyDir(string src, string dst)
    {
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>
    /// Disk restores run in the background (202); their outcome lands in
    /// the repo's operations log. Polls for this backup's terminal line
    /// and returns "ok", "failed", or "timeout".
    /// </summary>
    private static string WaitForDiskRestoreOutcome(string repoDir, string backupId)
    {
        string logPath = Path.Combine(repoDir, "operations.log");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(logPath))
                {
                    using var fs = new FileStream(logPath, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    foreach (string line in reader.ReadToEnd().Split('\n'))
                    {
                        if (!line.Contains("[manual-disk-restore]") || !line.Contains(backupId))
                            continue;
                        if (line.Contains("FAILED"))
                            return "failed";
                        if (line.Contains(" OK"))
                            return "ok";
                    }
                }
            }
            catch (IOException) { /* log mid-append; retry */ }
            Thread.Sleep(50);
        }
        return "timeout";
    }

    private string MakeSentinelTarget(byte[] imageBytes)
    {
        string target = Path.Combine(_workDir, "target.bin");
        byte[] sentinel = new byte[imageBytes.Length];
        Array.Fill(sentinel, (byte)0xAB);
        File.WriteAllBytes(target, sentinel);
        return target;
    }

    [Fact]
    public void ImportDiscoveredBackup_VerifiesRegistersAndRefusesConflicts()
    {
        // P2-D3: the import used to raw-copy the manifest — unverified,
        // unregistered (invisible in the vault list, yet filtered out of
        // future discovery), and silently overwriting a local manifest.
        // Source repo with a real backup.
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "alpha");
        string backupId;
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            backupId = repo.BackupDirectory(_srcDir).BackupId;

        // Destination repo: initialized, empty catalog.
        string destRepo = Path.Combine(_workDir, "dest-repo");
        using (var repo = BackupRepository.Init(destRepo, Passphrase))
            Assert.Empty(repo.ListBackups());
        var config = MakeConfig();
        config.Jobs[0].Repo = destRepo;

        var (status, _) = DashboardApi.ImportDiscoveredBackup(config, destRepo, backupId, _repoDir);
        Assert.Equal(200, status);
        using (var repo = BackupRepository.Open(destRepo, Passphrase))
            Assert.Contains(repo.ListBackups(), b => b.BackupId == backupId);

        // Idempotent re-import of the identical manifest: still 200.
        var (again, _) = DashboardApi.ImportDiscoveredBackup(config, destRepo, backupId, _repoDir);
        Assert.Equal(200, again);

        // A tampered discovered manifest (content changed, hash stale)
        // is refused and changes nothing.
        string shareDir = Path.Combine(_workDir, "share-tampered");
        CopyDir(_repoDir, shareDir);
        string tamperedPath = Path.Combine(shareDir, "manifests", backupId + ".json");
        string json = File.ReadAllText(tamperedPath).Replace("alpha-src", "evil-src");
        if (json == File.ReadAllText(tamperedPath))
            json = json.Replace("\"source\"", "\"sourceX\"");
        File.WriteAllText(tamperedPath, json);
        string destRepo2 = Path.Combine(_workDir, "dest-repo2");
        using (var repo = BackupRepository.Init(destRepo2, Passphrase)) { }
        config.Jobs[0].Repo = destRepo2;
        var (badStatus, _) = DashboardApi.ImportDiscoveredBackup(config, destRepo2, backupId, shareDir);
        Assert.Equal(400, badStatus);
        Assert.False(File.Exists(Path.Combine(destRepo2, "manifests", backupId + ".json")));

        // A DIFFERENT valid manifest under the same ID (re-stamped with
        // the repo key, as a stale share copy would be) must not replace
        // the local one: 409.
        string shareDir2 = Path.Combine(_workDir, "share-restamped");
        CopyDir(_repoDir, shareDir2);
        string restampedPath = Path.Combine(shareDir2, "manifests", backupId + ".json");
        var alt = System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllText(restampedPath),
            UsenetBackup.Core.Json.FileKeepJsonContext.Default.BackupManifest)!;
        alt.Source = "somewhere-else";
        alt.RootSha256 = alt.ComputeRootHash();
        File.WriteAllText(restampedPath, System.Text.Json.JsonSerializer.Serialize(alt,
            UsenetBackup.Core.Json.FileKeepJsonContext.Default.BackupManifest));
        config.Jobs[0].Repo = destRepo;
        var (conflict, _) = DashboardApi.ImportDiscoveredBackup(config, destRepo, backupId, shareDir2);
        Assert.Equal(409, conflict);
    }

    [Fact]
    public void StartDiskRestore_SameTargetAlreadyRestoring_Refused409()
    {
        if (OperatingSystem.IsWindows())
            return; // Disk-restore targets are physical drives on Windows by design:
                    // StartDiskRestore re-validates the target against
                    // PhysicalDrives.List(), so a file target is refused (400)
                    // before any staging. The staging/claim/gate behavior is
                    // covered on Linux; the Windows refusal itself is asserted
                    // by StartDiskRestore_NonPhysicalTarget_OnWindows_Refused400.
        // P2-C5: two restores writing one device interleaved chunk
        // writes and left a spliced image. The second start must be
        // refused while the target is claimed — and must not touch it.
        byte[] imageBytes = new byte[50_000];
        Random.Shared.NextBytes(imageBytes);
        string backupId = MakeDiskImageBackup(imageBytes);
        string target = MakeSentinelTarget(imageBytes);
        byte[] sentinel = File.ReadAllBytes(target);
        string shareDir = Path.Combine(_workDir, "share");
        CopyDir(_repoDir, shareDir);
        var config = MakeDiskRestoreConfig(shareDir);

        string key = DashboardApi.DiskRestoreTargetKey(target);
        Assert.True(DashboardApi.TryClaimRestoreTarget(key));
        try
        {
            var (status, payload) = DashboardApi.StartDiskRestore(
                config, _repoDir, backupId, target, confirmed: true);
            Assert.Equal(409, status);
            Assert.Contains("already writing", ((ErrorDto)payload).Error);
            Assert.Equal(sentinel, File.ReadAllBytes(target));
        }
        finally
        {
            DashboardApi.ReleaseRestoreTarget(key);
        }

        // Released: a fresh start is accepted and completes.
        var (okStatus, _) = DashboardApi.StartDiskRestore(
            config, _repoDir, backupId, target, confirmed: true);
        Assert.Equal(202, okStatus);
        Assert.Equal("ok", WaitForDiskRestoreOutcome(_repoDir, backupId));
        Assert.Equal(imageBytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void StartRestore_SameDestinationAlreadyRestoring_Refused409()
    {
        string dest = Path.Combine(_workDir, "restore-out");
        var config = MakeConfig();
        string key = DashboardApi.FileRestoreTargetKey(dest);
        Assert.True(DashboardApi.TryClaimRestoreTarget(key));
        try
        {
            var (status, payload) = DashboardApi.StartRestore(
                config, _repoDir, "somebackup", dest,
                UsenetBackup.Core.MergePolicy.Overwrite);
            Assert.Equal(409, status);
            Assert.Contains("already writing", ((ErrorDto)payload).Error);
        }
        finally
        {
            DashboardApi.ReleaseRestoreTarget(key);
        }
    }

    [Fact]
    public void StartDiskRestore_CorruptLocalChunk_NoSource_RefusesBeforeWriting()
    {
        if (OperatingSystem.IsWindows())
            return; // Disk-restore targets are physical drives on Windows by design:
                    // StartDiskRestore re-validates the target against
                    // PhysicalDrives.List(), so a file target is refused (400)
                    // before any staging. The staging/claim/gate behavior is
                    // covered on Linux; the Windows refusal itself is asserted
                    // by StartDiskRestore_NonPhysicalTarget_OnWindows_Refused400.
        // P2-V1 (DL-6 residual): with no banked location resolved, the
        // staging proof was existence-only — a present-but-corrupt local
        // chunk passed it and the restore died mid-write, target partly
        // overwritten. The proof must content-verify in that case.
        byte[] imageBytes = new byte[2_000_000];
        Random.Shared.NextBytes(imageBytes);
        string backupId = MakeDiskImageBackup(imageBytes);

        string[] chunks;
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            chunks = repo.LoadManifest(backupId).Files[0].Chunks.ToArray();
        Assert.True(chunks.Length >= 3, $"chunks={chunks.Length}");
        string victim = Path.Combine(_repoDir, "chunks", chunks[1][..2], chunks[1][2..]);
        byte[] blob = File.ReadAllBytes(victim);
        blob[20] ^= 0xFF;
        File.WriteAllBytes(victim, blob);

        string target = MakeSentinelTarget(imageBytes);
        byte[] before = File.ReadAllBytes(target);
        var config = MakeConfig();
        config.Jobs[0].Targets = new List<string>();
        config.Locations = new List<BackupLocation>();
        var (status, _) = DashboardApi.StartDiskRestore(
            config, _repoDir, backupId, target, confirmed: true);
        Assert.Equal(202, status);

        string outcome = WaitForDiskRestoreOutcome(_repoDir, backupId);
        Assert.StartsWith("failed", outcome);
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void StartDiskRestore_WithScheduler_WaitsForOperationGate()
    {
        if (OperatingSystem.IsWindows())
            return; // Disk-restore targets are physical drives on Windows by design:
                    // StartDiskRestore re-validates the target against
                    // PhysicalDrives.List(), so a file target is refused (400)
                    // before any staging. The staging/claim/gate behavior is
                    // covered on Linux; the Windows refusal itself is asserted
                    // by StartDiskRestore_NonPhysicalTarget_OnWindows_Refused400.
        // P2-C3: dashboard restores bypassed the scheduler's operation
        // lock and ran concurrently with scheduled runs on the same
        // repo. Gated, a restore started while another operation holds
        // the gate must not write (or log an outcome) until it frees.
        byte[] imageBytes = new byte[100_000];
        Random.Shared.NextBytes(imageBytes);
        string backupId = MakeDiskImageBackup(imageBytes);
        string target = MakeSentinelTarget(imageBytes);
        byte[] before = File.ReadAllBytes(target);
        var config = MakeConfig();
        config.Jobs[0].Targets = new List<string>();
        config.Locations = new List<BackupLocation>();
        var scheduler = new BackupScheduler(config);

        using var hold = scheduler.BeginDashboardOperation();
        var (status, _) = DashboardApi.StartDiskRestore(
            config, _repoDir, backupId, target, confirmed: true, scheduler: scheduler);
        Assert.Equal(202, status);

        // While the gate is held: no outcome, target untouched.
        Thread.Sleep(750);
        Assert.Equal(before, File.ReadAllBytes(target));
        string logPath = Path.Combine(_repoDir, "operations.log");
        string logText = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
        Assert.DoesNotContain(" OK", logText.Split('\n').FirstOrDefault(
            l => l.Contains("[manual-disk-restore]") && l.Contains(backupId) && !l.Contains("started")) ?? "");

        hold.Dispose();
        Assert.Equal("ok", WaitForDiskRestoreOutcome(_repoDir, backupId));
        Assert.Equal(imageBytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void StartDiskRestore_NonPhysicalTarget_OnWindows_Refused400()
    {
        if (!OperatingSystem.IsWindows())
            return; // The physical-drive revalidation is Windows-only.
        // The file-target disk-restore tests cannot run on Windows
        // because THIS guard fires first — assert the guard itself:
        // a target that is not a present physical drive is refused
        // synchronously, before any staging or writing.
        string backupId = MakeDiskImageBackup(new byte[100_000]);
        string target = MakeSentinelTarget(new byte[100_000]);
        var config = MakeConfig();
        var (status, payload) = DashboardApi.StartDiskRestore(
            config, _repoDir, backupId, target, confirmed: true);
        Assert.Equal(400, status);
        Assert.Contains("no longer present", ((ErrorDto)payload).Error);
    }

    [Fact]
    public void StartDiskRestore_PreCancelledToken_FailsLoggedBeforeWriting()
    {
        if (OperatingSystem.IsWindows())
            return; // Disk-restore targets are physical drives on Windows by design:
                    // StartDiskRestore re-validates the target against
                    // PhysicalDrives.List(), so a file target is refused (400)
                    // before any staging. The staging/claim/gate behavior is
                    // covered on Linux; the Windows refusal itself is asserted
                    // by StartDiskRestore_NonPhysicalTarget_OnWindows_Refused400.
        // P2-C8: dashboard restores ignored the host's stop token — a
        // service stop killed them mid-write and the operations log
        // showed only "started". With the token threaded, cancellation
        // lands as a logged failure before the first chunk is written.
        byte[] imageBytes = new byte[500_000];
        Random.Shared.NextBytes(imageBytes);
        string backupId = MakeDiskImageBackup(imageBytes);
        string target = MakeSentinelTarget(imageBytes);
        byte[] before = File.ReadAllBytes(target);
        var config = MakeConfig();
        config.Jobs[0].Targets = new List<string>();
        config.Locations = new List<BackupLocation>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (status, _) = DashboardApi.StartDiskRestore(
            config, _repoDir, backupId, target, confirmed: true,
            cancellationToken: cts.Token);
        Assert.Equal(202, status);

        string outcome = WaitForDiskRestoreOutcome(_repoDir, backupId);
        Assert.StartsWith("failed", outcome);
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void StartDiskRestore_ChunksOnlyOnShare_PullsAndRestores()
    {
        if (OperatingSystem.IsWindows())
            return; // Disk-restore targets are physical drives on Windows by design:
                    // StartDiskRestore re-validates the target against
                    // PhysicalDrives.List(), so a file target is refused (400)
                    // before any staging. The staging/claim/gate behavior is
                    // covered on Linux; the Windows refusal itself is asserted
                    // by StartDiskRestore_NonPhysicalTarget_OnWindows_Refused400.
        // DL-6: the image's chunks live only on the job's SMB location
        // (local staging pruned). The restore must pull them first and
        // then write the image — byte-for-byte the original.
        byte[] imageBytes = new byte[200_000];
        Random.Shared.NextBytes(imageBytes);
        string backupId = MakeDiskImageBackup(imageBytes);

        string shareDir = Path.Combine(_workDir, "share");
        CopyDir(_repoDir, shareDir);
        Directory.Delete(Path.Combine(_repoDir, "chunks"), recursive: true);

        string target = MakeSentinelTarget(imageBytes);
        var config = MakeDiskRestoreConfig(shareDir);
        var (status, _) = DashboardApi.StartDiskRestore(
            config, _repoDir, backupId, target, confirmed: true);
        Assert.Equal(202, status);

        Assert.Equal("ok", WaitForDiskRestoreOutcome(_repoDir, backupId));
        Assert.Equal(imageBytes, File.ReadAllBytes(target));
    }

    private static void DeleteChunk(string repoRoot, string chunkId)
    {
        string path = Path.Combine(repoRoot, "chunks", chunkId[..2], chunkId[2..]);
        Assert.True(File.Exists(path));
        File.Delete(path);
    }

    [Fact]
    public void StartDiskRestore_ChunkMissingEverywhere_RefusesBeforeWriting()
    {
        if (OperatingSystem.IsWindows())
            return; // Disk-restore targets are physical drives on Windows by design:
                    // StartDiskRestore re-validates the target against
                    // PhysicalDrives.List(), so a file target is refused (400)
                    // before any staging. The staging/claim/gate behavior is
                    // covered on Linux; the Windows refusal itself is asserted
                    // by StartDiskRestore_NonPhysicalTarget_OnWindows_Refused400.
        // DL-6: the first chunks are staged locally, but a later chunk
        // is missing from BOTH the local repo and the share. The restore
        // must fail WITHOUT touching the target — not one byte written
        // (it used to write the chunks it had and die on the missing
        // one, leaving the target partly overwritten).
        byte[] imageBytes = new byte[2_000_000];
        Random.Shared.NextBytes(imageBytes);
        string backupId = MakeDiskImageBackup(imageBytes);

        string shareDir = Path.Combine(_workDir, "share");
        CopyDir(_repoDir, shareDir);

        string[] chunks;
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            chunks = repo.LoadManifest(backupId).Files[0].Chunks.ToArray();
        Assert.True(chunks.Length >= 3, $"chunks={chunks.Length}");
        // chunks[1]: gone from local AND share — unobtainable. (Chunk 0
        // stays staged locally, so a restore that writes as it goes
        // would overwrite the target's first bytes before dying here.)
        DeleteChunk(_repoDir, chunks[1]);
        DeleteChunk(shareDir, chunks[1]);
        // The last chunk: pruned locally, still banked on the share.
        DeleteChunk(_repoDir, chunks[^1]);

        string target = MakeSentinelTarget(imageBytes);
        byte[] before = File.ReadAllBytes(target);
        var config = MakeDiskRestoreConfig(shareDir);
        var (status, _) = DashboardApi.StartDiskRestore(
            config, _repoDir, backupId, target, confirmed: true);
        Assert.Equal(202, status);

        Assert.Equal("failed", WaitForDiskRestoreOutcome(_repoDir, backupId));
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void TestSmbConnection_CredentialsTakeTheConnectPath()
    {
        // WALK-1: with credentials supplied, the test must go through
        // SmbShare.Connect (which rejects credentials for non-UNC
        // paths) instead of a bare existence check that would happily
        // pass for this existing local directory.
        var (status, payload) = DashboardApi.TestSmbConnection(_srcDir, "alice", "secret");
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("UNC", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void TestSmbConnection_NoCredentials_ExistenceCheck()
    {
        var (status, payload) = DashboardApi.TestSmbConnection(_srcDir);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void HtmlPage_HasSessionUnlockButtonAndHonestPlaceholder()
    {
        // WALK-2: the restore session row carries its own Unlock button
        // (the backups table's Restore buttons never render while
        // locked). WALK-5: the restore destination placeholder reads as
        // an example, not a prefilled value.
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("restore-unlock-btn", html);
        Assert.Contains("Unlock with YubiKey", html);
        Assert.Contains("placeholder=\"e.g. C:\\Restore\"", html);
    }

    [Fact]
    public void DeleteJob_DeletesTheLastJob()
    {
        // Zero jobs is a valid state (the fresh-install state), so the
        // only job deletes like any other. The former last-job guard
        // (7943f06) was an invented invariant; removed at the user's
        // decision 2026-10-20.
        var config = MakeConfig();
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var (status, _) = DashboardApi.DeleteJob(config, scheduler, configPath, "docs");
        Assert.Equal(200, status);
        Assert.Empty(config.Jobs);
        Assert.Empty(ServiceConfig.Load(configPath).Jobs);
    }

    [Fact]
    public void DeleteJob_PersistsViaTheSafeSavePath()
    {
        // SF-20: DeleteJob used to hand-roll serialization with a fixed
        // shared ".tmp" name outside JobsLock. It now persists through
        // ServiceConfig.Save (validated, locked, unique temp file) —
        // observable here as: the delete round-trips AND no temp file
        // of either naming scheme is left behind.
        var config = MakeConfig();
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var (status, _) = DashboardApi.DeleteJob(config, scheduler, configPath, "docs");
        Assert.Equal(200, status);
        Assert.Empty(ServiceConfig.Load(configPath).Jobs);
        Assert.False(File.Exists(configPath + ".tmp"));
        Assert.Empty(Directory.GetFiles(_workDir, "service.json.tmp-*"));
    }

    private static System.Diagnostics.Process StartShell(string unixCmd, string windowsCmd)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows()) psi.Arguments = "/c " + windowsCmd;
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(unixCmd); }
        return System.Diagnostics.Process.Start(psi)!;
    }

    [Fact]
    public void RunBuildProcess_StreamsOutputAndReportsExit()
    {
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var proc = StartShell(
            "echo hello-out; echo hello-err >&2; exit 3",
            "echo hello-out & echo hello-err 1>&2 & exit /b 3");
        var (timedOut, exitCode, stdErr, stdOut) =
            DashboardApi.RunBuildProcess(proc, TimeSpan.FromMinutes(1), lines.Add);
        Assert.False(timedOut);
        Assert.Equal(3, exitCode);
        Assert.Contains("hello-out", stdOut);
        Assert.Contains("hello-err", stdErr);
        // cmd's `echo x &` keeps the space before & — the collected
        // line is "hello-out " on Windows. Compare trimmed.
        Assert.Contains(lines, l => l.Trim() == "hello-out");
    }

    [Fact]
    public void RunBuildProcess_HungChild_TimesOutAndIsKilled()
    {
        // SF-8: the old shape (read stdout to EOF, THEN WaitForExit)
        // blocks here forever — a hung child never EOFs its stdout, so
        // the 60-minute backstop could never fire.
        using var proc = StartShell("sleep 60", "ping -n 61 127.0.0.1 >nul");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (timedOut, _, _, _) =
            DashboardApi.RunBuildProcess(proc, TimeSpan.FromSeconds(1), _ => { });
        sw.Stop();
        Assert.True(timedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"timeout path took {sw.Elapsed}");
        Assert.True(proc.HasExited);
    }

    [Fact]
    public void ReenableWinReAfterKilledBuild_NeverThrows_AlwaysStatesTheOutcome()
    {
        // SF-7: whatever the platform or tool state, the outcome must
        // be a sentence the build status can show — silence about the
        // host's WinRE state is the bug.
        string note = DashboardApi.ReenableWinReAfterKilledBuild();
        Assert.False(string.IsNullOrWhiteSpace(note));
        if (!OperatingSystem.IsWindows())
            Assert.Contains("reagentc /enable", note);
    }

    [Fact]
    public void ServiceConfig_RejectsAutoUploadWithoutProvider()
    {
        var config = new ServiceConfig
        {
            DashboardPort = 15789,
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "daily 02:00", AutoUpload = true },
            },
            Nntp = null,
        };
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("auto-upload", ex.Message);
    }

    [Fact]
    public void ServiceConfig_AcceptsAutoUploadWithProvider()
    {
        var config = new ServiceConfig
        {
            DashboardPort = 15789,
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "daily 02:00", AutoUpload = true },
            },
            Nntp = new NntpConfig { Host = "news.example.com", Port = 119 },
        };
        config.Validate(); // should not throw
    }

    [Fact]
    public void SaveResponses_NeverCarryPasswordBlobs()
    {
        // CX-6: UpsertJob/UpsertLocation used to return the raw config
        // objects — DPAPI blobs included — while GET /api/config strips
        // them by design. Both now return the DTO shape.
        var config = MakeConfig();
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var loc = new BackupLocation
        {
            Name = "nas", Type = "smb",
            Share = Path.Combine(_workDir, "share"), SmbUser = "joel",
        };
        var (locStatus, locPayload) = DashboardApi.UpsertLocation(config, configPath, loc);
        Assert.Equal(200, locStatus);
        var locDto = Assert.IsType<ConfigLocationDto>(locPayload);
        Assert.False(locDto.HasPassword);

        // Simulate a stored blob, then save again: the blob must not
        // ride the response.
        config.Locations.Single(l => l.Name == "nas").SmbPasswordProtected = "FAKE-DPAPI-BLOB";
        var (locStatus2, locPayload2) = DashboardApi.UpsertLocation(config, configPath,
            new BackupLocation
            {
                Name = "nas", Type = "smb",
                Share = Path.Combine(_workDir, "share"), SmbUser = "joel",
            });
        Assert.Equal(200, locStatus2);
        var locDto2 = Assert.IsType<ConfigLocationDto>(locPayload2);
        Assert.True(locDto2.HasPassword);
        string locJson = System.Text.Json.JsonSerializer.Serialize(locDto2,
            UsenetBackup.Service.DashboardJsonContext.Default.ConfigLocationDto);
        Assert.DoesNotContain("FAKE-DPAPI-BLOB", locJson);

        var job = new BackupJobConfig
        {
            Name = "blob-job", Repo = _repoDir, Source = _srcDir,
            Schedule = "interval 60", Mode = "full",
            Targets = new List<string> { "nas" },
        };
        var (jobStatus, jobPayload) = DashboardApi.UpsertJob(config, scheduler, configPath, job);
        Assert.Equal(200, jobStatus);
        Assert.IsType<ConfigJobDto>(jobPayload);
        config.Jobs.Single(j => j.Name == "blob-job").SmbPasswordProtected = "FAKE-JOB-BLOB";
        var (jobStatus2, jobPayload2) = DashboardApi.UpsertJob(config, scheduler, configPath,
            new BackupJobConfig
            {
                Name = "blob-job", Repo = _repoDir, Source = _srcDir,
                Schedule = "interval 60", Mode = "full",
                Targets = new List<string> { "nas" },
            });
        Assert.Equal(200, jobStatus2);
        var jobDto2 = Assert.IsType<ConfigJobDto>(jobPayload2);
        Assert.True(jobDto2.HasSmbPassword);
        string jobJson = System.Text.Json.JsonSerializer.Serialize(jobDto2,
            UsenetBackup.Service.DashboardJsonContext.Default.ConfigJobDto);
        Assert.DoesNotContain("FAKE-JOB-BLOB", jobJson);
    }

    [Fact]
    public void UpsertJob_AcceptsAutoUploadWhenProviderConfigured()
    {
        // Regression: UpsertJob built its validation config without Nntp,
        // so any job with AutoUpload=true failed with 400 even when a
        // provider was configured.
        var config = MakeConfig();
        config.Nntp = new NntpConfig { Host = "news.example.com", Port = 119 };
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var job = new BackupJobConfig
        {
            Name = "upload-job", Repo = _repoDir, Source = _srcDir,
            Schedule = "interval 60", Mode = "incremental",
            BackupPrivilege = false, AutoUpload = true,
        };
        var (status, _) = DashboardApi.UpsertJob(config, scheduler, configPath, job);
        Assert.Equal(200, status);
        Assert.Contains(config.Jobs, j => j.Name == "upload-job" && j.AutoUpload);
    }

    [Fact]
    public void UpsertJob_PreservesAutoUploadOnUpdate()
    {
        // Regression: UpsertJob did not copy AutoUpload when updating an
        // existing job, silently clearing the flag.
        var config = MakeConfig();
        config.Nntp = new NntpConfig { Host = "news.example.com", Port = 119 };
        config.Jobs[0].AutoUpload = true;
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var updated = new BackupJobConfig
        {
            Name = "docs", Repo = _repoDir, Source = _srcDir,
            Schedule = "interval 120", Mode = "incremental",
            BackupPrivilege = true, AutoUpload = true,
        };
        var (status, _) = DashboardApi.UpsertJob(config, scheduler, configPath, updated);
        Assert.Equal(200, status);
        Assert.True(config.Jobs[0].AutoUpload);
        Assert.True(config.Jobs[0].BackupPrivilege);
        Assert.Equal("interval 120", config.Jobs[0].Schedule);
    }

    [Fact]
    public void NntpConfig_PasswordBlobRoundTrips()
    {
        var config = new ServiceConfig
        {
            DashboardPort = 15789,
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "daily 02:00" },
            },
            Nntp = new NntpConfig
            {
                Host = "news.example.com",
                Port = 563,
                Username = "user",
                Ssl = true,
                PasswordProtected = "dGVzdC1ibG9i", // base64 "test-blob"
            },
        };
        string path = Path.Combine(_workDir, "nntp-test.json");
        config.Save(path);
        string json = File.ReadAllText(path);
        Assert.Contains("passwordProtected", json);
        Assert.DoesNotContain("\"password\":", json); // plaintext never persisted

        var loaded = ServiceConfig.Load(path);
        Assert.Equal("dGVzdC1ibG9i", loaded.Nntp?.PasswordProtected);
        Assert.True(loaded.Nntp!.HasPassword);
    }

    [Fact]
    public void BackupJobConfig_JsonRoundTripsCamelCaseFlags()
    {
        // The dashboard UI sends camelCase; the model must accept it.
        string uiJson = @"{""name"":""Test"",""repo"":""/tmp/r"",""source"":""/tmp/s"",""schedule"":""daily 02:00"",""mode"":""incremental"",""backupPrivilege"":true,""autoUpload"":true}";
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var job = JsonSerializer.Deserialize<BackupJobConfig>(uiJson, opts);
        Assert.NotNull(job);
        Assert.True(job.BackupPrivilege);
        Assert.True(job.AutoUpload);
    }

    [Fact]
    public void UpdateNntp_ClearsWhenHostEmpty()
    {
        var config = MakeConfig();
        config.Nntp = new NntpConfig { Host = "news.example.com", Port = 119 };
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var (status, _) = DashboardApi.UpdateNntp(config, scheduler, configPath,
            new NntpConfig { Host = "" });
        Assert.Equal(200, status);
        Assert.Null(config.Nntp);
    }

    [Fact]
    public void Dpapi_ThrowsPlatformNotSupportedOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return; // Real DPAPI test needs Windows; validated manually there.
        Assert.Throws<PlatformNotSupportedException>(() => Dpapi.Protect("test"));
        Assert.Throws<PlatformNotSupportedException>(() => Dpapi.Unprotect("dGVzdA=="));
    }

    // ---- GET /api/browse-folders (server-backed folder picker) ----

    [Fact]
    public void BrowseFolders_ListsSubfoldersOnlyWithParent()
    {
        string root = Path.Combine(_workDir, "browse");
        string subA = Path.Combine(root, "alpha");
        string subB = Path.Combine(root, "Beta");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);
        File.WriteAllText(Path.Combine(root, "file.txt"), "not a folder");

        var (status, payload) = DashboardApi.BrowseFolders(root);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        var el = doc.RootElement;
        Assert.Equal(Path.GetFullPath(root), el.GetProperty("path").GetString());
        Assert.Equal(Path.GetFullPath(_workDir), el.GetProperty("parent").GetString());
        var folders = el.GetProperty("folders");
        Assert.Equal(2, folders.GetArrayLength());
        // Sorted by name (case-insensitive); files excluded.
        Assert.Equal("alpha", folders[0].GetProperty("name").GetString());
        Assert.Equal(Path.GetFullPath(subA), folders[0].GetProperty("path").GetString());
        Assert.Equal("Beta", folders[1].GetProperty("name").GetString());
        Assert.Equal(Path.GetFullPath(subB), folders[1].GetProperty("path").GetString());
        Assert.Equal(0, el.GetProperty("drives").GetArrayLength());
    }

    [Fact]
    public void BrowseFolders_NestedFolder_ParentIsImmediateParent()
    {
        string deep = Path.Combine(_workDir, "a", "b", "c");
        Directory.CreateDirectory(deep);

        var (status, payload) = DashboardApi.BrowseFolders(deep);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.Equal(Path.GetFullPath(Path.Combine(_workDir, "a", "b")),
            doc.RootElement.GetProperty("parent").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("folders").GetArrayLength());
    }

    [Fact]
    public void BrowseFolders_TrailingSeparator_IsNormalized()
    {
        string root = Path.Combine(_workDir, "browse2");
        Directory.CreateDirectory(root);

        var (status, payload) = DashboardApi.BrowseFolders(root + Path.DirectorySeparatorChar);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.Equal(Path.GetFullPath(root), doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void BrowseFolders_EmptyPath_ReturnsDrivesView()
    {
        var (status, payload) = DashboardApi.BrowseFolders("");
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        var el = doc.RootElement;
        Assert.Equal("", el.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, el.GetProperty("parent").ValueKind);
        Assert.Equal(0, el.GetProperty("folders").GetArrayLength());
        var drives = el.GetProperty("drives");
        Assert.True(drives.GetArrayLength() >= 1);
        foreach (var d in drives.EnumerateArray())
            Assert.True(Directory.Exists(d.GetProperty("path").GetString()));
        // Home is the picker's default start folder; it exists on any
        // machine with a user profile.
        Assert.False(string.IsNullOrEmpty(el.GetProperty("home").GetString()));

        var (nullStatus, _) = DashboardApi.BrowseFolders(null);
        Assert.Equal(200, nullStatus);
    }

    [Fact]
    public void BrowseFolders_DriveRoot_HasNoParent()
    {
        string root = Path.GetPathRoot(_workDir)!;
        var (status, payload) = DashboardApi.BrowseFolders(root);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("parent").ValueKind);
        Assert.Equal(root, doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void BrowseFolders_MissingFolder_ReturnsCleanError()
    {
        string missing = Path.Combine(_workDir, "no-such-folder");
        var (status, payload) = DashboardApi.BrowseFolders(missing);
        Assert.Equal(400, status);
        using var doc = ToJson(payload);
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void BrowseFolders_FilePath_ReturnsCleanError()
    {
        string file = Path.Combine(_workDir, "a-file.txt");
        File.WriteAllText(file, "x");
        var (status, payload) = DashboardApi.BrowseFolders(file);
        Assert.Equal(400, status);
        using var doc = ToJson(payload);
        Assert.Contains("not a folder", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void BrowseFolders_AccessDenied_ReturnsCleanError()
    {
        if (OperatingSystem.IsWindows())
            return; // chmod-based access denial is Unix-only. (Early return, not
                    // SkipException: this suite's dotnet-test runner reports dynamic
                    // skips as failures — see TestAssembly.cs notes.)
        string locked = Path.Combine(_workDir, "locked");
        Directory.CreateDirectory(Path.Combine(locked, "child"));
        var prev = File.GetUnixFileMode(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            bool enforced;
            try { Directory.GetDirectories(locked); enforced = false; }
            catch (UnauthorizedAccessException) { enforced = true; }
            if (!enforced)
                throw Xunit.Sdk.SkipException.ForSkip(
                    "Filesystem does not enforce permissions for this user (e.g. running as root).");
            var (status, payload) = DashboardApi.BrowseFolders(locked);
            Assert.Equal(400, status);
            using var doc = ToJson(payload);
            Assert.Contains("Access denied", doc.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            File.SetUnixFileMode(locked, prev);
        }
    }
}
