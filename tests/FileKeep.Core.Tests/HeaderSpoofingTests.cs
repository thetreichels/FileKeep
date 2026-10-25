using FileKeep.Core;
using FileKeep.Core.Recovery;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Tests for header spoofing v1 on disguised USB/SMB destinations: fixed
/// fake media headers prepended on write, detect-and-stripped on read,
/// pre-header files still readable, corrupted headers failing closed.
/// </summary>
public sealed class HeaderSpoofingTests : IDisposable
{
    private const string Passphrase = "header-test-passphrase";
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "fk-headers-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static DisguisedLayout Layout(string key = "NAS backups", bool decoys = false) =>
        new(key, "Photos", decoys);

    // ---- Header table ----

    [Fact]
    public void HeaderTable_HasExpectedBytesAndLengths()
    {
        Assert.Equal(1, DisguisedLayout.HeaderTableVersion);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10,
            (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 },
            DisguisedLayout.HeaderForExtension(".jpg"));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R' },
            DisguisedLayout.HeaderForExtension(".png"));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x18,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' },
            DisguisedLayout.HeaderForExtension(".mp4"));
        Assert.Equal(DisguisedLayout.HeaderForExtension(".mp4"),
            DisguisedLayout.HeaderForExtension(".mov"));
        Assert.Equal(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            0x00, 0x00, 0x00, 0x00, (byte)'A', (byte)'V', (byte)'I', 0x20 },
            DisguisedLayout.HeaderForExtension(".avi"));
        Assert.Empty(DisguisedLayout.HeaderForExtension(".dat"));
        Assert.Empty(DisguisedLayout.HeaderForExtension(".cache"));
        Assert.Empty(DisguisedLayout.HeaderForExtension(".bin"));
        Assert.Empty(DisguisedLayout.HeaderForExtension(".txt"));
        // Case-insensitive: derived names are lowercase, but be tolerant.
        Assert.Equal(DisguisedLayout.HeaderForExtension(".jpg"),
            DisguisedLayout.HeaderForExtension(".JPG"));
    }

    // ---- Wrap / unwrap units ----

    [Fact]
    public void WrapUnwrap_RoundTripsEveryExtension()
    {
        byte[] content = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        foreach (string ext in DisguisedLayout.Extensions)
        {
            string name = "abcdef012345678901234567" + ext;
            byte[] wrapped = DisguisedLayout.WrapContent(content, name);
            byte[] header = DisguisedLayout.HeaderForExtension(ext);
            Assert.Equal(header.Length + content.Length, wrapped.Length);
            Assert.Equal(header, wrapped[..header.Length]);
            Assert.Equal(content, wrapped[header.Length..]);
            Assert.Equal(content, DisguisedLayout.UnwrapContent(wrapped, name));
        }
    }

    [Fact]
    public void Unwrap_PreHeaderFile_ReadsAsIs()
    {
        // A file written by the pre-header disguise slice has no header;
        // detect-and-strip must leave it untouched, for every extension.
        byte[] raw = new byte[] { 9, 8, 7, 6, 5 };
        foreach (string ext in DisguisedLayout.Extensions)
            Assert.Equal(raw, DisguisedLayout.UnwrapContent(raw, "abcdef012345678901234567" + ext));
    }

    [Fact]
    public void Unwrap_CorruptedHeader_ReturnsBytesUnchanged()
    {
        // A wrong header must NOT be stripped: the header bytes then flow
        // into AES-GCM verification downstream and fail authentication,
        // instead of silently decrypting to wrong bytes.
        byte[] content = new byte[] { 1, 2, 3, 4 };
        byte[] wrapped = DisguisedLayout.WrapContent(content, "x.jpg");
        wrapped[0] ^= 0xFF;
        Assert.Equal(wrapped, DisguisedLayout.UnwrapContent(wrapped, "x.jpg"));
        // A truncated file (shorter than its header) is also left alone.
        Assert.Equal(new byte[] { 0xFF, 0xD8 },
            DisguisedLayout.UnwrapContent(new byte[] { 0xFF, 0xD8 }, "x.jpg"));
    }

    // ---- File-level copy helpers, every extension ----

    [Fact]
    public void CopyWrappedAndUnwrapped_RoundTripEveryExtension()
    {
        string dir = Path.Combine(_tmp, "files");
        Directory.CreateDirectory(dir);
        byte[] content = System.Text.Encoding.UTF8.GetBytes("header round trip payload");
        foreach (string ext in DisguisedLayout.Extensions)
        {
            string src = Path.Combine(dir, "src" + ext);
            string dst = Path.Combine(dir, "dst" + ext);
            string back = Path.Combine(dir, "back" + ext);
            File.WriteAllBytes(src, content);
            Assert.True(DisguisedLayout.CopyWrappedIfNeeded(src, dst, out _));
            byte[] stored = File.ReadAllBytes(dst);
            byte[] header = DisguisedLayout.HeaderForExtension(ext);
            Assert.Equal(header.Length + content.Length, stored.Length);
            Assert.Equal(header, stored[..header.Length]);
            // Second copy is a no-op (wrapped content matches).
            Assert.False(DisguisedLayout.CopyWrappedIfNeeded(src, dst, out _));
            Assert.True(DisguisedLayout.WrappedMatches(src, dst));
            DisguisedLayout.CopyUnwrapped(dst, back);
            Assert.Equal(content, File.ReadAllBytes(back));
            // A pre-header destination file reads back through CopyUnwrapped too.
            File.WriteAllBytes(dst, content);
            DisguisedLayout.CopyUnwrapped(dst, back);
            Assert.Equal(content, File.ReadAllBytes(back));
        }
    }

    // ---- Full sync round-trips ----

    private (string Repo, string BackupId, string SrcFile) MakeRepo(string name)
    {
        string repo = Path.Combine(_tmp, name, "repo");
        string src = Path.Combine(_tmp, name, "src");
        Directory.CreateDirectory(src);
        string srcFile = Path.Combine(src, "hello.txt");
        File.WriteAllText(srcFile, "hello header world " + name);
        BackupRepository.Init(repo, Passphrase, 64 * 1024, 10_000).Dispose();
        string backupId;
        using (var r = BackupRepository.Open(repo, Passphrase))
            backupId = r.BackupDirectory(src).BackupId;
        return (repo, backupId, srcFile);
    }

    private (string Repo, string BackupId) MakeMultiRepo(string name)
    {
        string repo = Path.Combine(_tmp, name, "repo");
        string src = Path.Combine(_tmp, name, "src");
        Directory.CreateDirectory(src);
        for (int i = 0; i < 24; i++)
            File.WriteAllText(Path.Combine(src, $"f{i}.txt"), $"header victim file {i} " + new string('x', i * 37));
        BackupRepository.Init(repo, Passphrase, 64 * 1024, 10_000).Dispose();
        string backupId;
        using (var r = BackupRepository.Open(repo, Passphrase))
            backupId = r.BackupDirectory(src).BackupId;
        return (repo, backupId);
    }

    private static void AssertDestinationHeadered(string cover, DisguisedLayout layout, string repo)
    {
        foreach (string src in Directory.GetFiles(Path.Combine(repo, "chunks"), "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Path.Combine(repo, "chunks"), src);
            string id = rel.Replace(Path.DirectorySeparatorChar.ToString(), "");
            string dst = Path.Combine(cover, layout.ChunkFileName(id));
            Assert.True(File.Exists(dst), $"missing disguised chunk {dst}");
            byte[] stored = File.ReadAllBytes(dst);
            byte[] header = DisguisedLayout.HeaderForExtension(Path.GetExtension(dst));
            Assert.Equal(header, stored[..header.Length]);
            Assert.Equal(File.ReadAllBytes(src),
                DisguisedLayout.UnwrapContent(stored, dst));
        }
    }

    [Fact]
    public void SmbSync_Disguised_WritesHeaderedFiles_AndPullRestores()
    {
        var (repo, backupId, srcFile) = MakeRepo("smb");
        var layout = Layout(decoys: true);
        string shareDir = Path.Combine(_tmp, "smb", "share");
        using var share = SmbShare.Connect(shareDir, null, null);
        var result = new SmbSync().Sync(repo, share, "Photos", layout);
        Assert.True(result.ChunksCopied > 0);
        string cover = Path.Combine(shareDir, "Photos");
        AssertDestinationHeadered(cover, layout, repo);
        // Re-sync is idempotent even with headers: nothing recopied.
        Assert.Equal(0, new SmbSync().Sync(repo, share, "Photos", layout).ChunksCopied);
        // Decoys are NOT double-wrapped: bytes equal the decoy generator's output.
        int expectedDecoys = DisguisedLayout.DecoyCount(
            Directory.GetFiles(Path.Combine(repo, "chunks"), "*", SearchOption.AllDirectories).Length);
        for (int i = 0; i < expectedDecoys; i++)
        {
            string decoy = Path.Combine(cover, layout.DecoyFileName(i));
            Assert.True(File.Exists(decoy), $"missing decoy {i}");
            Assert.Equal(layout.DecoyContent(i), File.ReadAllBytes(decoy));
        }

        // Pull through the header-stripping read path and restore byte-exact.
        Directory.Delete(Path.Combine(repo, "chunks"), recursive: true);
        using (var ws = new WizardState
        {
            RepoPath = repo, Passphrase = Passphrase, SelectedBackupId = backupId,
            SmbShare = shareDir,
        })
            ws.EnsureSmbChunks(null, default, layout);
        string dest = Path.Combine(_tmp, "smb", "restored");
        using (var r = BackupRepository.Open(repo, Passphrase))
            r.Restore(backupId, dest);
        Assert.Equal(File.ReadAllText(srcFile),
            File.ReadAllText(Path.Combine(dest, "hello.txt")));
    }

    [Fact]
    public void UsbFolderSync_Disguised_WritesHeaderedFiles_AndPullRestores()
    {
        var (repo, backupId, srcFile) = MakeRepo("usb");
        var layout = new DisguisedLayout("My Stick", "Photos", false);
        string usbCover = Path.Combine(_tmp, "usb", "Photos");
        var scheduler = new BackupScheduler(new ServiceConfig());
        var (chunks, _) = scheduler.SyncRepoToLocalFolder(repo, usbCover, default, layout);
        Assert.True(chunks > 0);
        AssertDestinationHeadered(usbCover, layout, repo);

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
    public void SmbBlobStore_Disguised_PutGetRoundTrips()
    {
        var layout = Layout();
        string shareDir = Path.Combine(_tmp, "store", "share");
        var share = SmbShare.Connect(shareDir, null, null);
        var store = new SmbBlobStore(share, "Photos", layout);
        string id = new string('a', 64);
        byte[] blob = new byte[] { 10, 20, 30, 40, 50 };
        store.Put(id, blob);
        string path = store.PathFor(id);
        // Stored form is header-wrapped when the derived extension is media.
        byte[] header = DisguisedLayout.HeaderForExtension(Path.GetExtension(path));
        byte[] stored = File.ReadAllBytes(path);
        Assert.Equal(header, stored[..header.Length]);
        Assert.Equal(blob, store.Get(id));
        // A pre-header blob already on the share reads back as-is.
        File.WriteAllBytes(path, blob);
        Assert.Equal(blob, store.Get(id));
    }

    // ---- Corrupted header fails closed ----

    [Fact]
    public void CorruptedHeader_FailsVerification_NotSilentGarbage()
    {
        // Several files so the repo holds several chunks: the victim must
        // be a chunk whose derived extension is a media one, and a single
        // chunk could land on a headerless extension by chance.
        var (repo, backupId) = MakeMultiRepo("corrupt");
        var layout = Layout();
        string shareDir = Path.Combine(_tmp, "corrupt", "share");
        using var share = SmbShare.Connect(shareDir, null, null);
        new SmbSync().Sync(repo, share, "Photos", layout);
        string cover = Path.Combine(shareDir, "Photos");

        // Find one disguised chunk file with a media extension and corrupt its header.
        string? victim = null;
        string? victimId = null;
        foreach (string src in Directory.GetFiles(Path.Combine(repo, "chunks"), "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Path.Combine(repo, "chunks"), src);
            string id = rel.Replace(Path.DirectorySeparatorChar.ToString(), "");
            string dst = Path.Combine(cover, layout.ChunkFileName(id));
            if (DisguisedLayout.HeaderForExtension(Path.GetExtension(dst)).Length > 0)
            {
                victim = dst;
                victimId = id;
                break;
            }
        }
        Assert.NotNull(victim);
        byte[] bytes = File.ReadAllBytes(victim!);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(victim!, bytes);

        // The delivery-confirmation check sees through the corruption too.
        using (var r = BackupRepository.Open(repo, Passphrase))
        {
            Assert.False(r.VerifyDisguisedChunkBlobAt(victim!, victimId!));
            // And a good file still verifies.
            foreach (string src in Directory.GetFiles(Path.Combine(repo, "chunks"), "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(Path.Combine(repo, "chunks"), src);
                string id = rel.Replace(Path.DirectorySeparatorChar.ToString(), "");
                string dst = Path.Combine(cover, layout.ChunkFileName(id));
                if (dst != victim)
                    Assert.True(r.VerifyDisguisedChunkBlobAt(dst, id));
            }
        }

        // The pull fails closed with a verification error, and stores nothing.
        Directory.Delete(Path.Combine(repo, "chunks"), recursive: true);
        using (var ws = new WizardState
        {
            RepoPath = repo, Passphrase = Passphrase, SelectedBackupId = backupId,
            SmbShare = shareDir,
        })
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                ws.EnsureSmbChunks(null, default, layout));
            Assert.Contains("failed verification", ex.Message);
        }
        // Chunks pulled before the corrupted one are legitimately stored;
        // the corrupted chunk itself must not be.
        string victimLocal = Path.Combine(repo, "chunks", victimId![..2], victimId![2..]);
        Assert.False(File.Exists(victimLocal),
            "failed pull must not store the corrupted chunk");
    }

    // ---- Plain locations untouched ----

    [Fact]
    public void SmbSync_Plain_BytesAreRaw()
    {
        var (repo, _, _) = MakeRepo("plain");
        string shareDir = Path.Combine(_tmp, "plain", "share");
        using var share = SmbShare.Connect(shareDir, null, null);
        new SmbSync().Sync(repo, share);
        foreach (string src in Directory.GetFiles(Path.Combine(repo, "chunks"), "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Path.Combine(repo, "chunks"), src);
            Assert.Equal(File.ReadAllBytes(src),
                File.ReadAllBytes(Path.Combine(shareDir, "chunks", rel)));
        }
    }
}
