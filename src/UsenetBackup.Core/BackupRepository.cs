using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using UsenetBackup.Core.Nntp;

namespace UsenetBackup.Core;

/// <summary>
/// Milestone 1: local repository engine.
/// Init → BackupDirectory → manifest; Restore / Verify from manifest.
/// Milestone 2: incremental backups against a parent manifest.
/// </summary>
public sealed class BackupRepository : IDisposable
{
    public const string FormatVersion = "v1";
    public const int DefaultChunkSize = 4 * 1024 * 1024;
    public const int DefaultKdfIterations = 600_000;

    private readonly string _root;
    private readonly RepositoryConfig _config;
    private readonly byte[] _key;
    private readonly IBlobStore _blobs;
    private readonly Catalog _catalog;
    private bool _disposed;

    private BackupRepository(string root, RepositoryConfig config, byte[] key)
    {
        _root = root;
        _config = config;
        _key = key;
        _blobs = new LocalBlobStore(root);
        _catalog = new Catalog(Path.Combine(root, "catalog.db"));
    }

    // ---------- lifecycle ----------

    public static BackupRepository Init(
        string repoPath,
        string passphrase,
        int chunkSize = DefaultChunkSize,
        int kdfIterations = DefaultKdfIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        if (chunkSize < 4096)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be >= 4 KiB.");

        Directory.CreateDirectory(repoPath);
        if (File.Exists(Path.Combine(repoPath, "repo.json")))
            throw new InvalidOperationException($"A repository already exists at {repoPath}.");

        var config = new RepositoryConfig
        {
            CreatedUtc = DateTime.UtcNow,
            KdfIterations = kdfIterations,
            KdfSaltB64 = Convert.ToBase64String(KeyDerivation.NewSalt()),
            ChunkSize = chunkSize,
        };
        File.WriteAllText(
            Path.Combine(repoPath, "repo.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.Combine(repoPath, "manifests"));
        Directory.CreateDirectory(Path.Combine(repoPath, "chunks"));
        Directory.CreateDirectory(Path.Combine(repoPath, "parity"));

        byte[] key = KeyDerivation.DeriveKey(passphrase, Convert.FromBase64String(config.KdfSaltB64), kdfIterations);
        string fullRoot = Path.GetFullPath(repoPath);
        OperationLog.Append(fullRoot, "init", $"chunk_size={chunkSize} kdf_iterations={kdfIterations}");
        return new BackupRepository(fullRoot, config, key);
    }

    public static BackupRepository Open(string repoPath, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        string configPath = Path.Combine(repoPath, "repo.json");
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"No repository found at {repoPath} (missing repo.json).");

        var config = JsonSerializer.Deserialize<RepositoryConfig>(File.ReadAllText(configPath))
            ?? throw new InvalidDataException("repo.json is corrupt.");
        if (config.FormatVersion != FormatVersion)
            throw new NotSupportedException($"Repository format '{config.FormatVersion}' is not supported by this build (supports {FormatVersion}).");

        byte[] key = KeyDerivation.DeriveKey(
            passphrase, Convert.FromBase64String(config.KdfSaltB64), config.KdfIterations);
        return new BackupRepository(Path.GetFullPath(repoPath), config, key);
    }

    // ---------- backup ----------

    /// <summary>Full backup: every file is chunked.</summary>
    /// <summary>
    /// Full backup of a directory tree. When <paramref name="snapshotProvider"/>
    /// is supplied, files are read from its point-in-time view instead of the
    /// live tree (e.g. a VSS shadow copy on Windows); the caller owns and
    /// disposes the provider. Null reads the live tree directly.
    /// </summary>
    public BackupManifest BackupDirectory(string sourceDir, ISnapshotProvider? snapshotProvider = null)
    {
        var manifest = BackupDirectoryInternal(sourceDir, parent: null, snapshotProvider);
        manifest.Type = "full";
        return FinalizeManifest(manifest);
    }

    /// <summary>
    /// Incremental backup against a parent manifest. Files whose path,
    /// size and mtime match the parent are assumed unchanged and reuse
    /// the parent's chunk list without re-reading. The resulting manifest
    /// is self-contained: it lists every file with complete chunk lists,
    /// so restore/verify never need the parent.
    /// </summary>
    public BackupManifest BackupIncremental(string sourceDir, string parentBackupId, ISnapshotProvider? snapshotProvider = null)
    {
        var parent = LoadManifest(parentBackupId); // validates parent root hash
        if (parent.ChunkSize != _config.ChunkSize)
            throw new InvalidOperationException(
                $"Parent backup {parentBackupId} uses chunk size {parent.ChunkSize}, " +
                $"but this repository uses {_config.ChunkSize}.");
        var manifest = BackupDirectoryInternal(sourceDir, parent, snapshotProvider);
        manifest.Type = "inc";
        manifest.ParentId = parentBackupId;
        return FinalizeManifest(manifest);
    }

    private BackupManifest FinalizeManifest(BackupManifest manifest)
    {
        // Root hash covers type + parent_id, so compute it last.
        manifest.RootSha256 = manifest.ComputeRootHash();
        if (!manifest.VerifyRootHash())
            throw new InvalidOperationException("Internal error: manifest root hash did not verify.");

        WriteManifest(manifest);
        _catalog.RecordBackup(manifest.BackupId, manifest.Type, manifest.Source, manifest.CreatedUtc);
        OperationLog.Append(_root, "backup",
            $"type={manifest.Type} id={manifest.BackupId} files={manifest.Files.Count} " +
            $"chunk_refs={manifest.Files.Sum(f => f.Chunks.Count)} " +
            $"unique_chunks={_blobs.StoredCount} source={manifest.Source}" +
            (manifest.ParentId is null ? "" : $" parent={manifest.ParentId}"));
        return manifest;
    }

    private BackupManifest BackupDirectoryInternal(string sourceDir, BackupManifest? parent, ISnapshotProvider? snapshotProvider)
    {
        string fullSource = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(fullSource))
            throw new DirectoryNotFoundException($"Source directory not found: {fullSource}");

        // The provider is caller-owned; only dispose one we created ourselves.
        ISnapshotProvider? owned = null;
        ISnapshotProvider snap = snapshotProvider ?? (owned = new NullSnapshotProvider(fullSource));
        try
        {
            return BackupDirectoryFromRoot(fullSource, snap, parent);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private BackupManifest BackupDirectoryFromRoot(string fullSource, ISnapshotProvider snap, BackupManifest? parent)
    {
        string readRoot = snap.SnapshotRoot;
        if (!Directory.Exists(readRoot))
            throw new DirectoryNotFoundException($"Snapshot root not found: {readRoot}");

        var manifest = new BackupManifest
        {
            BackupId = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            Source = fullSource,
            ChunkSize = _config.ChunkSize,
            Snapshot = snap.IsSnapshot ? snap.Name : null,
        };

        Dictionary<string, FileEntry>? parentByPath = parent?.Files
            .ToDictionary(f => f.Path, StringComparer.Ordinal);

        string[] files = Directory
            .EnumerateFiles(readRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(readRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        // Record every directory (including empty ones) so restore
        // reproduces the source tree exactly, not just the files.
        string[] dirs = Directory
            .EnumerateDirectories(readRoot, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(readRoot, d).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        if (dirs.Length > 0)
            manifest.Directories = new List<string>(dirs);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.ChunkSize);
        try
        {
            foreach (string rel in files)
            {
                string full = Path.Combine(readRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (parentByPath is not null &&
                    parentByPath.TryGetValue(rel, out var parentEntry) &&
                    IsUnchanged(full, parentEntry))
                {
                    manifest.Files.Add(parentEntry);
                }
                else
                {
                    manifest.Files.Add(BackupOneFile(snap, full, rel, buffer));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return manifest;
    }

    private void WriteManifest(BackupManifest manifest)
    {
        string manifestPath = Path.Combine(_root, "manifests", manifest.BackupId + ".json");
        File.WriteAllText(manifestPath, manifest.ToJson());
    }

    /// <summary>
    /// Fast path: the file is unchanged since the parent backup if path,
    /// size, mtime and symlink target all match. Reads only attributes,
    /// not file contents.
    /// </summary>
    private static bool IsUnchanged(string fullPath, FileEntry parentEntry)
    {
        if (File.GetLastWriteTimeUtc(fullPath) != parentEntry.MtimeUtc)
            return false;
        var info = new FileInfo(fullPath);
        if (info.Length != parentEntry.Size)
            return false;
        bool isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
        if (isLink != (parentEntry.SymlinkTarget is not null))
            return false;
        if (isLink)
        {
            string? target = File.ResolveLinkTarget(fullPath, returnFinalTarget: false)?.ToString();
            if (!string.Equals(target, parentEntry.SymlinkTarget, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private FileEntry BackupOneFile(ISnapshotProvider snap, string fullPath, string relPath, byte[] buffer)
    {
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            return new FileEntry
            {
                Path = relPath,
                MtimeUtc = File.GetLastWriteTimeUtc(fullPath),
                SymlinkTarget = File.ResolveLinkTarget(fullPath, returnFinalTarget: false)?.ToString(),
                Size = 0,
                Sha256 = Hashing.Sha256Hex(ReadOnlySpan<byte>.Empty),
            };
        }

        using var stream = snap.OpenRead(fullPath);
        return BackupStream(stream, relPath, File.GetLastWriteTimeUtc(fullPath), buffer);
    }

    /// <summary>
    /// Chunks, hashes, encrypts and stores a byte stream as one manifest file
    /// entry. Used for regular files and for raw disk images alike.
    /// </summary>
    private FileEntry BackupStream(Stream stream, string relPath, DateTime mtimeUtc, byte[] buffer)
    {
        var entry = new FileEntry
        {
            Path = relPath,
            MtimeUtc = mtimeUtc,
        };

        long size;
        try { size = stream.Length; }
        catch (NotSupportedException) { size = -1; } // e.g. some raw devices
        catch (IOException) { size = -1; }

        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, _config.ChunkSize)) > 0)
        {
            var span = buffer.AsSpan(0, read);
            string chunkId = Hashing.Sha256Hex(span);
            if (!_blobs.Exists(chunkId))
            {
                byte[] blob = ChunkCrypto.Encrypt(span, _key, Hashing.HexToBytes(chunkId));
                _blobs.Put(chunkId, blob);
            }
            _catalog.RecordChunk(chunkId, read);
            entry.Chunks.Add(chunkId);
            fileHash.AppendData(span);
            total += read;
        }
        entry.Size = size >= 0 ? size : total;
        if (size >= 0 && size != total)
            throw new IOException(
                $"Stream length changed during backup of '{relPath}': expected {size} bytes, read {total}.");
        entry.Sha256 = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
        return entry;
    }

    // ---------- disk images ----------

    /// <summary>
    /// Backs up a raw block device (e.g. <c>\\.\C:</c> on Windows,
    /// <c>/dev/sda</c> on Linux) as a single image entry. The device is read
    /// sequentially through the normal chunk/encrypt pipeline, so the image
    /// gets the same per-chunk authentication and deduplication as files.
    /// The device is opened with <see cref="FileShare.ReadWrite"/> so a live
    /// volume can be imaged; for a crash-consistent image, image a VSS
    /// snapshot instead of the live volume.
    /// </summary>
    public BackupManifest BackupDiskImage(string devicePath, string imageName = "disk.img")
    {
        if (string.IsNullOrWhiteSpace(imageName) || imageName.Contains('/') || imageName.Contains('\\'))
            throw new ArgumentException("Image name must be a plain file name.", nameof(imageName));

        using var stream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.ChunkSize);
        try
        {
            var entry = BackupStream(stream, imageName, DateTime.UtcNow, buffer);
            var manifest = new BackupManifest
            {
                BackupId = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                Source = devicePath,
                ChunkSize = _config.ChunkSize,
                Kind = "disk-image",
                Files = { entry },
            };
            manifest.Type = "full";
            return FinalizeManifest(manifest);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Restores a disk-image backup (see <see cref="BackupDiskImage"/>) by
    /// writing the decrypted image back to a block device. Fails closed:
    /// every chunk is AES-GCM authenticated on decrypt, its SHA-256 is
    /// checked against the chunk ID, and the whole-image SHA-256 must match
    /// the manifest — any mismatch aborts the restore with an exception.
    /// </summary>
    public void RestoreDiskImage(string backupId, string devicePath)
    {
        var manifest = LoadManifest(backupId); // validates root hash
        if (!manifest.IsDiskImage || manifest.Files.Count != 1)
            throw new InvalidOperationException(
                $"Backup {backupId} is not a disk-image backup (kind={manifest.Kind ?? "directory"}, files={manifest.Files.Count}).");
        var entry = manifest.Files[0];

        using var device = new FileStream(devicePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        try
        {
            long deviceLen = device.Length;
            if (deviceLen < entry.Size)
                throw new IOException(
                    $"Target device ({deviceLen} bytes) is smaller than the image ({entry.Size} bytes).");
        }
        catch (NotSupportedException) { /* length unknown; write and let it fail naturally */ }
        catch (IOException ex) when (ex.Message.StartsWith("Target device")) { throw; }
        catch (IOException) { /* length unknown; write and let it fail naturally */ }

        using var imageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] chunkBuf = ArrayPool<byte>.Shared.Rent(_config.ChunkSize);
        try
        {
            foreach (string chunkId in entry.Chunks)
            {
                byte[] blob = _blobs.Get(chunkId); // throws if missing
                byte[] plain = ChunkCrypto.Decrypt(blob, _key, Hashing.HexToBytes(chunkId)); // AES-GCM authenticates
                if (!string.Equals(Hashing.Sha256Hex(plain), chunkId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Chunk {chunkId} failed hash verification; restore aborted.");
                device.Write(plain, 0, plain.Length);
                imageHash.AppendData(plain);
                CryptographicOperations.ZeroMemory(plain);
            }
            device.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuf);
        }

        string actual = Convert.ToHexString(imageHash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Restored image hash mismatch for backup {backupId}: manifest says {entry.Sha256}, wrote {actual}.");

        OperationLog.Append(_root, "restore-disk",
            $"id={backupId} device={devicePath} bytes={entry.Size}");
    }

    // ---------- restore & verify ----------

    public BackupManifest LoadManifest(string backupId)
    {
        string path = Path.Combine(_root, "manifests", backupId + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Manifest {backupId} not found in repository.");
        var manifest = BackupManifest.FromJson(File.ReadAllText(path));
        if (!manifest.VerifyRootHash())
            throw new InvalidDataException($"Manifest {backupId} failed root-hash verification (tampered or corrupt).");
        return manifest;
    }

    public IReadOnlyList<BackupSummary> ListBackups() => _catalog.ListBackups();

    public long StoredChunkCount() => _blobs.StoredCount;

    /// <summary>
    /// Raw encrypted blob for a chunk, for upload to remote blob stores.
    /// </summary>
    public byte[] GetChunkBlob(string chunkIdHex) => _blobs.Get(chunkIdHex);

    /// <summary>
    /// Short repository identity used in NNTP message-IDs, derived from
    /// the repository salt (stable; no migration for existing repos).
    /// </summary>
    public string RepoId => ArticleCodec.DeriveRepoId(Convert.FromBase64String(_config.KdfSaltB64));

    /// <summary>Path to catalog.db, which also holds the NNTP upload journal.</summary>
    public string CatalogPath => Path.Combine(_root, "catalog.db");

    /// <summary>
    /// Milestone 5: download every chunk referenced by an NZB index from an
    /// NNTP server into this repository's local chunk store.
    ///
    /// Interruption-safe: chunks already present locally are skipped (and
    /// adopted into the download journal), so re-running after a failure
    /// fetches only what's missing. Every fetched blob is authenticated
    /// (AES-GCM) and its SHA-256(plaintext) is checked against the chunk ID
    /// before it is stored — a corrupt or wrong article is never journaled.
    /// </summary>
    /// <param name="nzb">Parsed NZB index (see <see cref="NzbParser"/>).</param>
    /// <param name="remote">NNTP-backed blob store to fetch from.</param>
    /// <param name="progress">Called as (done, total) after each chunk.</param>
    public DownloadResult DownloadChunks(
        NzbDocument nzb,
        NntpBlobStore remote,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(nzb);
        ArgumentNullException.ThrowIfNull(remote);

        int downloaded = 0, alreadyPresent = 0;
        var files = nzb.Files;
        for (int i = 0; i < files.Count; i++)
        {
            string? chunkId = files[i].ChunkId;
            if (chunkId is null)
                throw new InvalidDataException(
                    $"NZB file #{i + 1} ('{files[i].Subject}') does not reference a usenet-backup article.");
            string messageId = ArticleCodec.MakeMessageId(chunkId, RepoId);

            if (_blobs.Exists(chunkId))
            {
                _catalog.RecordDownload(messageId, chunkId); // adopt into journal
                alreadyPresent++;
            }
            else
            {
                // ARTICLE + yEnc CRC-32 + header chunk-ID check inside.
                byte[] blob = remote.Get(chunkId);
                VerifyDownloadedBlob(chunkId, blob);
                _blobs.Put(chunkId, blob);
                _catalog.RecordChunk(chunkId, blob.Length);
                _catalog.RecordDownload(messageId, chunkId);
                downloaded++;
            }
            progress?.Invoke(i + 1, files.Count);
        }
        return new DownloadResult(downloaded, alreadyPresent, files.Count);
    }

    /// <summary>
    /// Fails closed: decrypts (AES-GCM authentication) and checks
    /// SHA-256(plaintext) against the content-addressed chunk ID.
    /// </summary>
    private void VerifyDownloadedBlob(string chunkIdHex, byte[] blob)
    {
        byte[] plaintext;
        try
        {
            plaintext = ChunkCrypto.Decrypt(blob, _key, Hashing.HexToBytes(chunkIdHex));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                $"Downloaded chunk {chunkIdHex} failed authentication (wrong key or corrupted).", ex);
        }
        try
        {
            string actual = Hashing.Sha256Hex(plaintext);
            if (!actual.Equals(chunkIdHex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Downloaded chunk {chunkIdHex} hash mismatch (SHA-256(plaintext) = {actual}).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Restore(string backupId, string destDir)
    {
        var manifest = LoadManifest(backupId);
        var errors = new List<string>();
        RestoreOrVerify(manifest, destDir, verifyOnly: false, errors);
        OperationLog.Append(_root, "restore",
            $"id={backupId} dest={destDir} files={manifest.Files.Count} errors={errors.Count}");
        if (errors.Count > 0)
            throw new InvalidDataException("Restore failed:\n" + string.Join("\n", errors));
    }

    /// <summary>Full verification. Returns empty list when the backup is intact.</summary>
    public IReadOnlyList<string> Verify(string backupId)
    {
        var errors = new List<string>();
        BackupManifest manifest;
        try
        {
            manifest = LoadManifest(backupId);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            OperationLog.Append(_root, "verify", $"id={backupId} errors={errors.Count} ok=False");
            return errors;
        }
        RestoreOrVerify(manifest, destDir: null, verifyOnly: true, errors);
        OperationLog.Append(_root, "verify",
            $"id={backupId} files={manifest.Files.Count} errors={errors.Count} ok={errors.Count == 0}");
        return errors;
    }

    private void RestoreOrVerify(BackupManifest manifest, string? destDir, bool verifyOnly, List<string> errors)
    {
        if (!verifyOnly && destDir is not null && manifest.Directories is not null)
        {
            foreach (string dir in manifest.Directories)
            {
                try
                {
                    Directory.CreateDirectory(
                        Path.Combine(destDir, dir.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception ex)
                {
                    errors.Add($"{dir}/: {ex.Message}");
                }
            }
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.ChunkSize);
        try
        {
            foreach (var entry in manifest.Files)
            {
                try
                {
                    RestoreOneFile(entry, manifest, destDir, verifyOnly, buffer);
                }
                catch (Exception ex)
                {
                    errors.Add($"{entry.Path}: {ex.Message}");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void RestoreOneFile(FileEntry entry, BackupManifest manifest, string? destDir, bool verifyOnly, byte[] buffer)
    {
        if (entry.SymlinkTarget is not null)
        {
            if (!verifyOnly && destDir is not null)
            {
                string linkPath = Path.Combine(destDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                if (File.Exists(linkPath) || Directory.Exists(linkPath))
                    File.Delete(linkPath);
                File.CreateSymbolicLink(linkPath, entry.SymlinkTarget);
            }
            return;
        }

        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Stream? outStream = null;
        string? outPath = null;
        try
        {
            if (!verifyOnly && destDir is not null)
            {
                outPath = Path.Combine(destDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            }

            foreach (string chunkId in entry.Chunks)
            {
                byte[] plaintext = ReadChunkPlaintext(chunkId);
                fileHash.AppendData(plaintext);
                outStream?.Write(plaintext, 0, plaintext.Length);
            }

            string actual = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"File hash mismatch (manifest {entry.Sha256}, actual {actual}).");
        }
        finally
        {
            outStream?.Dispose();
        }

        if (!verifyOnly && outPath is not null)
            File.SetLastWriteTimeUtc(outPath, entry.MtimeUtc);
    }

    /// <summary>
    /// Reads a blob from the store, AES-GCM-decrypts it (fails closed on
    /// any tampering) and re-hashes the plaintext against the chunk ID.
    /// </summary>
    private byte[] ReadChunkPlaintext(string chunkIdHex)
    {
        byte[] blob = _blobs.Get(chunkIdHex);
        byte[] chunkId = Hashing.HexToBytes(chunkIdHex);
        byte[] plaintext;
        try
        {
            plaintext = ChunkCrypto.Decrypt(blob, _key, chunkId);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                $"Chunk {chunkIdHex} failed authentication (wrong key or tampered data).", ex);
        }

        string actual = Hashing.Sha256Hex(plaintext);
        if (!actual.Equals(chunkIdHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Chunk {chunkIdHex} hash mismatch after decryption.");
        return plaintext;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _catalog.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }
}
