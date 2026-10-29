using System.Security.Cryptography;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Json;
using FileKeep.Core.Service;

namespace FileKeep.Reader;

/// <summary>
/// One unlocked reader session: the decrypted manifest, the
/// file-content key, and the chunk stores. The stick never carries
/// a secret: the export passphrase opens the package and the vault
/// secret derives the content key, both are prompted every time,
/// held only in memory, and zeroed on lock/dispose. The original
/// location (USB by volume serial, SMB with credentials re-entered
/// on the spot) is an optional second chunk source — never stored,
/// never written to the package.
/// </summary>
public sealed class ReaderSession : IDisposable
{
    /// <summary>One file in the reader's in-memory manifest.</summary>
    public sealed class Entry
    {
        public required string Path { get; set; }
        public required long Size { get; set; }
        public required DateTimeOffset ModifiedUtc { get; set; }
        public required List<string> ChunkIds { get; set; }
    }

    /// <summary>
    /// Chunk store that reads stick-first, then the original
    /// location, per chunk. Writes go to the stick only: a
    /// PUT-back edits the stick copy, never the original location
    /// and never the main machine's catalog.
    /// </summary>
    public sealed class CompositeStore : IBlobStore
    {
        private readonly IBlobStore _stick;
        private readonly IBlobStore? _original;

        public CompositeStore(IBlobStore stick, IBlobStore? original)
        {
            _stick = stick;
            _original = original;
        }

        public bool Exists(string chunkIdHex)
        {
            try { if (_stick.Exists(chunkIdHex)) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (_original is null) return false;
            try { return _original.Exists(chunkIdHex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        public byte[] Get(string chunkIdHex)
        {
            try { return _stick.Get(chunkIdHex); }
            catch (InvalidDataException) { /* fall through to original */ }
            if (_original is not null)
            {
                try { return _original.Get(chunkIdHex); }
                catch (InvalidDataException) { /* fall through to the throw below */ }
            }
            throw new InvalidDataException(
                $"Chunk {chunkIdHex} is not on the stick or at the original location — the file is incomplete, not garbled.");
        }

        public void Put(string chunkIdHex, byte[] blob) => _stick.Put(chunkIdHex, blob);
        public void Put(string chunkIdHex, ReadOnlySpan<byte> blob) => _stick.Put(chunkIdHex, blob);
        public void Delete(string chunkIdHex) => _stick.Delete(chunkIdHex);
        public long StoredCount => _stick.StoredCount;
    }

    public string FolderName { get; }
    public OfflineExportEnvelope.LocationPointer Location { get; }
    public string PackagePath { get; }
    public IBlobStore StickStore { get; }
    public IBlobStore Store { get; }
    public List<Entry> Files { get; } = new();
    public bool IsUnlocked { get; private set; } = true;

    private byte[] _contentKey;
    private readonly string _exportPassphrase;
    private readonly List<IDisposable> _disposables = new();

    public ReaderSession(
        string packagePath, OfflineExportEnvelope.Payload payload,
        byte[] contentKey, string exportPassphrase,
        IBlobStore stickStore, IBlobStore? originalStore,
        IEnumerable<IDisposable>? disposables = null)
    {
        PackagePath = packagePath;
        FolderName = payload.Folder;
        Location = payload.Location;
        _contentKey = contentKey;
        _exportPassphrase = exportPassphrase;
        StickStore = stickStore;
        Store = new CompositeStore(stickStore, originalStore);
        if (disposables is not null) _disposables.AddRange(disposables);
        foreach (var f in payload.Files)
            Files.Add(new Entry
            {
                Path = f.Path,
                Size = f.Size,
                ModifiedUtc = f.ModifiedUtc,
                ChunkIds = f.ChunkIds.ToList(),
            });
    }

    public byte[] ContentKey
    {
        get
        {
            if (!IsUnlocked) throw new InvalidOperationException("The reader session is locked.");
            return _contentKey;
        }
    }

    /// <summary>Finds an entry by path (case-insensitive, like the main DAV handler).</summary>
    public Entry? Find(string path) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Re-encrypts the in-memory manifest into the package file
    /// (atomically), under the same export passphrase. The original
    /// location and the main catalog are untouched — the stick
    /// becomes the edited copy.
    /// </summary>
    public void SavePackage()
    {
        if (!IsUnlocked) throw new InvalidOperationException("The reader session is locked.");
        var payload = new OfflineExportEnvelope.Payload
        {
            Folder = FolderName,
            Location = Location,
            Files = Files.Select(f => new OfflineExportEnvelope.FileRef
            {
                Path = f.Path,
                Size = f.Size,
                ModifiedUtc = f.ModifiedUtc,
                ChunkIds = f.ChunkIds.ToList(),
            }).ToList(),
        };
        var package = OfflineExportEnvelope.CreatePackage(payload, _exportPassphrase);
        // Source-generated metadata (Core's FileKeepJsonContext):
        // the reader publishes trimmed single-file, where
        // reflection serialization is disabled (the SF-1 class).
        OfflineStickLayout.WritePackageAtomic(PackagePath,
            JsonSerializer.Serialize(package,
                FileKeepJsonContext.Default.Package));
    }

    /// <summary>Locks the session: zeroes the content key and drops
    /// the stores' connections. The manifest stays in memory only
    /// until dispose; no secret is written anywhere.</summary>
    public void Lock()
    {
        if (!IsUnlocked) return;
        IsUnlocked = false;
        CryptographicOperations.ZeroMemory(_contentKey);
        foreach (var d in _disposables)
            try { d.Dispose(); } catch { /* best effort */ }
        _disposables.Clear();
    }

    public void Dispose()
    {
        Lock();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Resolves the original location's blob store from a pointer,
    /// or null when it cannot be reached (the reader still works
    /// from stick chunks). USB resolves by volume serial; SMB
    /// connects with credentials supplied by the caller (re-entered
    /// on the spot, never stored). The returned store may hold a
    /// connection the caller must dispose (returned in
    /// <paramref name="disposables"/>).
    /// </summary>
    public static IBlobStore? ResolveOriginalStore(
        OfflineExportEnvelope.LocationPointer pointer,
        Func<string, string?> promptPassword,
        Func<string>? promptSmbUser,
        out List<IDisposable> disposables)
    {
        disposables = new();
        try
        {
            if (pointer.Type == "usb" && !string.IsNullOrWhiteSpace(pointer.VolumeSerial))
            {
                DisguisedLayout? layout = pointer.DisguiseEnabled
                    ? new DisguisedLayout(pointer.Name, pointer.CoverName ?? "Photos", pointer.DisguiseDecoys)
                    : null;
                string? folder = layout is not null
                    ? UsbDriveResolver.ResolveBackupPath(pointer.VolumeSerial, layout.CoverName)
                    : UsbDriveResolver.ResolveBackupPath(pointer.VolumeSerial, pointer.UsbFolder ?? "FileKeep");
                if (folder is null) return null;
                if (layout is not null)
                {
                    string driveRoot = Path.GetPathRoot(folder) ?? folder;
                    return new LocalBlobStore(driveRoot, layout);
                }
                return new LocalBlobStore(folder);
            }
            if (pointer.Type == "smb" && !string.IsNullOrWhiteSpace(pointer.Share))
            {
                string? user = promptSmbUser?.Invoke();
                string? password = string.IsNullOrWhiteSpace(user)
                    ? null : promptPassword("SMB password");
                // Read-only: a typo must fail, not create directories.
                var share = SmbShare.Connect(pointer.Share, user, password, createIfMissing: false);
                disposables.Add(share);
                DisguisedLayout? layout = pointer.DisguiseEnabled
                    ? new DisguisedLayout(pointer.Name, pointer.CoverName ?? "Photos", pointer.DisguiseDecoys)
                    : null;
                return new SmbBlobStore(share, null, layout);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Original location unreachable: stick chunks still work.
            foreach (var d in disposables)
                try { d.Dispose(); } catch { /* best effort */ }
            disposables.Clear();
            return null;
        }
        return null;
    }
}
