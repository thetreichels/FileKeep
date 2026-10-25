using FileKeep.Core;
using FileKeep.Core.Recovery;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Tests for disguised USB/SMB destination layouts (the "disguise" slice
/// of the Offline Folders design): deterministic derived names, decoys,
/// disguised sync round-trips, disguised restore pulls, and the
/// location-API rules (validation, immutability, type restriction).
/// </summary>
public sealed class DisguisedLayoutTests : IDisposable
{
    private const string Passphrase = "disguise-test-passphrase";
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "fk-disguise-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static DisguisedLayout Layout(string key = "NAS backups", bool decoys = false) =>
        new(key, "Photos", decoys);

    private static string ChunkId(char c) => new(c, 64);

    // ---- Naming ----

    [Fact]
    public void ChunkFileName_IsDeterministic()
    {
        var layout = Layout();
        string a = layout.ChunkFileName(ChunkId('a'));
        Assert.Equal(a, layout.ChunkFileName(ChunkId('a')));
        Assert.Equal(24, a[..24].Length); // 24 hex chars + plausible extension
        Assert.Contains(Path.GetExtension(a), DisguisedLayout.Extensions);
        Assert.True(a[..24].All(c => Uri.IsHexDigit(c)));
    }

    [Fact]
    public void ChunkFileName_DiffersAcrossLocations()
    {
        string id = ChunkId('b');
        Assert.NotEqual(Layout("Location One").ChunkFileName(id),
            Layout("Location Two").ChunkFileName(id));
    }

    [Fact]
    public void ChunkFileName_ExtensionIsFromPlausibleSet()
    {
        var layout = Layout();
        for (int i = 0; i < 50; i++)
        {
            string name = layout.ChunkFileName(ChunkId((char)('a' + i % 26)) + i.ToString("x"));
            Assert.Contains(Path.GetExtension(name), DisguisedLayout.Extensions);
        }
    }

    [Fact]
    public void Names_DifferAcrossDomains()
    {
        var layout = Layout();
        string x = ChunkId('c');
        Assert.NotEqual(layout.ChunkFileName(x), layout.ManifestFileName(x));
        Assert.NotEqual(layout.ChunkFileName(x), layout.ParityFileName(x));
        Assert.NotEqual(layout.ManifestFileName(x), layout.ParityFileName(x));
        Assert.NotEqual(layout.ChunkFileName(x), layout.RepoFileName());
        Assert.NotEqual(layout.DecoyFileName(0), layout.ChunkFileName(x));
    }

    [Fact]
    public void ForLocation_NullUnlessDisguisedUsbOrSmb()
    {
        Assert.Null(DisguisedLayout.ForLocation(
            new BackupLocation { Name = "plain", Type = "usb", UsbFolder = "FileKeep" }));
        Assert.Null(DisguisedLayout.ForLocation(
            new BackupLocation { Name = "n", Type = "nntp", DisguiseEnabled = true, CoverName = "Photos" }));
        Assert.Null(DisguisedLayout.ForLocation(
            new BackupLocation { Name = "bad", Type = "smb", DisguiseEnabled = true, CoverName = "a/b" }));
        Assert.NotNull(DisguisedLayout.ForLocation(
            new BackupLocation { Name = "d", Type = "smb", DisguiseEnabled = true, CoverName = "Photos" }));
        Assert.NotNull(DisguisedLayout.ForLocation(
            new BackupLocation { Name = "d2", Type = "usb", DisguiseEnabled = true, CoverName = "Photos" }));
    }

    [Fact]
    public void UsbFolderFor_UsesCoverWhenDisguised()
    {
        var plain = new BackupLocation { Name = "p", Type = "usb", UsbFolder = "FileKeep" };
        var disguised = new BackupLocation
            { Name = "d", Type = "usb", UsbFolder = "FileKeep", DisguiseEnabled = true, CoverName = "Photos" };
        Assert.Equal("FileKeep", DisguisedLayout.UsbFolderFor(plain));
        Assert.Equal("Photos", DisguisedLayout.UsbFolderFor(disguised));
    }

    [Fact]
    public void CoverName_Validation()
    {
        Assert.True(DisguisedLayout.IsValidCoverName("Photos", out _));
        Assert.True(DisguisedLayout.IsValidCoverName("Camera Uploads 2024", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("   ", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("a/b", out _));
        Assert.False(DisguisedLayout.IsValidCoverName(@"a\b", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("a:b", out _));
        Assert.False(DisguisedLayout.IsValidCoverName(".", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("..", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("CON", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("nul.jpg", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("Photos ", out _));
        Assert.False(DisguisedLayout.IsValidCoverName("Photos.", out _));
        Assert.False(DisguisedLayout.IsValidCoverName(new string('x', 129), out _));
    }

    // ---- Decoys ----

    [Fact]
    public void DecoyContent_IsDeterministicAndHeadered()
    {
        var layout = Layout();
        // Find a decoy index whose derived name carries a .jpg extension.
        int jpgIndex = -1;
        for (int i = 0; i < 64; i++)
        {
            if (Path.GetExtension(layout.DecoyFileName(i)) == ".jpg") { jpgIndex = i; break; }
        }
        Assert.True(jpgIndex >= 0);
        byte[] a = layout.DecoyContent(jpgIndex);
        byte[] b = layout.DecoyContent(jpgIndex);
        Assert.Equal(a, b);
        Assert.Equal(0xFF, a[0]);
        Assert.Equal(0xD8, a[1]);
        Assert.Equal(0xFF, a[2]);
        Assert.Equal(0xE0, a[3]);
        Assert.InRange(a.Length, 64 * 1024, 2 * 1024 * 1024);
    }

    [Fact]
    public void DecoyCount_Clamped()
    {
        Assert.Equal(8, DisguisedLayout.DecoyCount(0));
        Assert.Equal(8, DisguisedLayout.DecoyCount(10));
        Assert.Equal(10, DisguisedLayout.DecoyCount(100));
        Assert.Equal(64, DisguisedLayout.DecoyCount(10000));
    }

    // ---- Sync round-trips ----

    private (string Repo, string BackupId, string SrcFile) MakeRepo(string name)
    {
        string repo = Path.Combine(_tmp, name, "repo");
        string src = Path.Combine(_tmp, name, "src");
        Directory.CreateDirectory(src);
        string srcFile = Path.Combine(src, "hello.txt");
        File.WriteAllText(srcFile, "hello disguised world " + name);
        BackupRepository.Init(repo, Passphrase, 64 * 1024, 10_000).Dispose();
        string backupId;
        using (var r = BackupRepository.Open(repo, Passphrase))
            backupId = r.BackupDirectory(src).BackupId;
        return (repo, backupId, srcFile);
    }

    private static void AssertNoRevealingNames(string root)
    {
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            Assert.NotEqual("repo.json", name);
            Assert.DoesNotContain("chunks", Path.GetDirectoryName(file)!);
            Assert.DoesNotContain("manifests", Path.GetDirectoryName(file)!);
        }
        Assert.False(Directory.Exists(Path.Combine(root, "chunks")));
        Assert.False(Directory.Exists(Path.Combine(root, "manifests")));
    }

    [Fact]
    public void SmbSync_Disguised_WritesFlatDerivedNamesAndDecoys()
    {
        var (repo, _, _) = MakeRepo("smb");
        string shareDir = Path.Combine(_tmp, "smb", "share");
        var layout = Layout("NAS backups", decoys: true);
        using var share = SmbShare.Connect(shareDir, null, null);
        var result = new SmbSync().Sync(repo, share, "Photos", layout);

        string cover = Path.Combine(shareDir, "Photos");
        Assert.True(Directory.Exists(cover));
        AssertNoRevealingNames(cover);
        // Every local chunk/manifest/repo.json has a disguised counterpart.
        // Header spoofing v1: destination bytes are header-wrapped, so
        // compare after stripping (pre-header files would compare raw).
        foreach (string src in Directory.GetFiles(Path.Combine(repo, "chunks"), "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Path.Combine(repo, "chunks"), src);
            string id = rel.Replace(Path.DirectorySeparatorChar.ToString(), "");
            string dst = Path.Combine(cover, layout.ChunkFileName(id));
            Assert.True(File.Exists(dst), $"missing disguised chunk {dst}");
            Assert.Equal(File.ReadAllBytes(src),
                DisguisedLayout.UnwrapContent(File.ReadAllBytes(dst), dst));
        }
        Assert.True(result.ChunksCopied > 0);
        // Decoys: the stable set for this chunk count, JPEG header present on .jpg ones.
        int expectedDecoys = DisguisedLayout.DecoyCount(
            Directory.GetFiles(Path.Combine(repo, "chunks"), "*", SearchOption.AllDirectories).Length);
        for (int i = 0; i < expectedDecoys; i++)
            Assert.True(File.Exists(Path.Combine(cover, layout.DecoyFileName(i))), $"missing decoy {i}");
        // Re-sync is idempotent: nothing new copied, decoys unchanged.
        var again = new SmbSync().Sync(repo, share, "Photos", layout);
        Assert.Equal(0, again.ChunksCopied);
    }

    [Fact]
    public void SmbSync_Plain_Unchanged()
    {
        var (repo, _, _) = MakeRepo("plain");
        string shareDir = Path.Combine(_tmp, "plain", "share");
        using var share = SmbShare.Connect(shareDir, null, null);
        var result = new SmbSync().Sync(repo, share);
        Assert.True(result.ChunksCopied > 0);
        Assert.True(Directory.Exists(Path.Combine(shareDir, "chunks")));
        Assert.True(File.Exists(Path.Combine(shareDir, "repo.json")));
    }

    [Fact]
    public void UsbFolderSync_Disguised_ThenPullAndRestore()
    {
        var (repo, backupId, srcFile) = MakeRepo("usb");
        var layout = Layout("My Stick", decoys: true);
        string usbCover = Path.Combine(_tmp, "usb", "Photos");
        var scheduler = new BackupScheduler(new ServiceConfig());
        var (chunks, _) = scheduler.SyncRepoToLocalFolder(repo, usbCover, default, layout);
        Assert.True(chunks > 0);
        AssertNoRevealingNames(usbCover);

        // Local staging pruned: pull back through the disguised folder, decoys ignored.
        Directory.Delete(Path.Combine(repo, "chunks"), recursive: true);
        using (var ws = new WizardState { RepoPath = repo, Passphrase = Passphrase, SelectedBackupId = backupId })
            ws.EnsureLocalChunks(usbCover, null, default, layout);

        string dest = Path.Combine(_tmp, "usb", "restored");
        using (var r = BackupRepository.Open(repo, Passphrase))
            r.Restore(backupId, dest);
        Assert.Equal(File.ReadAllText(srcFile),
            File.ReadAllText(Path.Combine(dest, "hello.txt")));
    }

    [Fact]
    public void SmbPull_Disguised_ThenRestore()
    {
        var (repo, backupId, srcFile) = MakeRepo("smbpull");
        var layout = Layout("NAS backups", decoys: true);
        string shareDir = Path.Combine(_tmp, "smbpull", "share");
        using (var share = SmbShare.Connect(shareDir, null, null))
            new SmbSync().Sync(repo, share, "Photos", layout);

        Directory.Delete(Path.Combine(repo, "chunks"), recursive: true);
        using (var ws = new WizardState
        {
            RepoPath = repo, Passphrase = Passphrase, SelectedBackupId = backupId,
            SmbShare = shareDir,
        })
            ws.EnsureSmbChunks(null, default, layout);

        string dest = Path.Combine(_tmp, "smbpull", "restored");
        using (var r = BackupRepository.Open(repo, Passphrase))
            r.Restore(backupId, dest);
        Assert.Equal(File.ReadAllText(srcFile),
            File.ReadAllText(Path.Combine(dest, "hello.txt")));
    }

    // ---- Location API rules ----

    private (ServiceConfig Config, string Path) MakeConfig()
    {
        Directory.CreateDirectory(_tmp);
        var config = new ServiceConfig();
        string path = Path.Combine(_tmp, "service.json");
        return (config, path);
    }

    [Fact]
    public void UpsertLocation_DisguiseFields_RoundTrip()
    {
        var (config, path) = MakeConfig();
        var loc = new BackupLocation
        {
            Name = "nas", Type = "smb", Share = @"\\NAS\backups",
            DisguiseEnabled = true, CoverName = "Photos", DisguiseDecoys = true,
        };
        var (status, payload) = DashboardApi.UpsertLocation(config, path, loc);
        Assert.Equal(200, status);
        var dto = Assert.IsType<ConfigLocationDto>(payload);
        Assert.True(dto.DisguiseEnabled);
        Assert.Equal("Photos", dto.CoverName);
        Assert.True(dto.DisguiseDecoys);
        // Persisted: reload keeps the fields.
        var reloaded = ServiceConfig.Load(path);
        var saved = reloaded.Locations.Single(l => l.Name == "nas");
        Assert.True(saved.DisguiseEnabled);
        Assert.Equal("Photos", saved.CoverName);
        Assert.True(saved.DisguiseDecoys);
        // And a config written before disguise existed loads with defaults off.
        string legacyJson = """
            {"dashboardPort": 15789, "locations": [
              {"name": "old", "type": "usb", "volumeSerial": "ABCD-1234", "usbFolder": "FileKeep"}]}
            """;
        string legacyPath = Path.Combine(_tmp, "legacy.json");
        File.WriteAllText(legacyPath, legacyJson);
        var legacy = ServiceConfig.Load(legacyPath);
        var old = legacy.Locations.Single();
        Assert.False(old.DisguiseEnabled);
        Assert.Equal("", old.CoverName);
        Assert.False(old.DisguiseDecoys);
    }

    [Fact]
    public void UpsertLocation_Disguise_ValidationAndImmutability()
    {
        var (config, path) = MakeConfig();
        // NNTP cannot be disguised.
        var (s1, _) = DashboardApi.UpsertLocation(config, path, new BackupLocation
            { Name = "n", Type = "nntp", Host = "news.example.com", DisguiseEnabled = true, CoverName = "Photos" });
        Assert.Equal(400, s1);
        // Bad cover name rejected.
        var (s2, _) = DashboardApi.UpsertLocation(config, path, new BackupLocation
            { Name = "s", Type = "smb", Share = @"\\NAS\b", DisguiseEnabled = true, CoverName = "a/b" });
        Assert.Equal(400, s2);
        // Create disguised, then try to change the disguise: refused.
        var (s3, _) = DashboardApi.UpsertLocation(config, path, new BackupLocation
            { Name = "s", Type = "smb", Share = @"\\NAS\b", DisguiseEnabled = true, CoverName = "Photos" });
        Assert.Equal(200, s3);
        var (s4, _) = DashboardApi.UpsertLocation(config, path, new BackupLocation
            { Name = "s", Type = "smb", Share = @"\\NAS\b", DisguiseEnabled = false, CoverName = "Photos" });
        Assert.Equal(400, s4);
        var (s5, _) = DashboardApi.UpsertLocation(config, path, new BackupLocation
            { Name = "s", Type = "smb", Share = @"\\NAS\b", DisguiseEnabled = true, CoverName = "Videos" });
        Assert.Equal(400, s5);
        // An unchanged re-save still succeeds.
        var (s6, _) = DashboardApi.UpsertLocation(config, path, new BackupLocation
            { Name = "s", Type = "smb", Share = @"\\NAS\b", DisguiseEnabled = true, CoverName = "Photos" });
        Assert.Equal(200, s6);
    }
}
