using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using UsenetBackup.Core.Json;
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
    public const long DefaultVolumeSizeBytes = 100L * 1000 * 1000;
    public const int DefaultKdfIterations = 600_000;

    /// <summary>Fixed-size chunking: every chunk is <c>ChunkSize</c> bytes except the last (v1 behavior).</summary>
    public const string ChunkingSchemeFixed = "fixed";
    /// <summary>Content-defined chunking: Gear rolling hash, target average = <c>ChunkSize</c>. Default for new repositories.</summary>
    public const string ChunkingSchemeCdc = "cdc";

    /// <summary>
    /// Largest plaintext chunk this repository can produce. For fixed
    /// chunking this is <c>ChunkSize</c>; for CDC it is 4x the target
    /// average (the chunker's hard cap).
    /// </summary>
    public int EffectiveMaxChunkSize => _config.ChunkingScheme == ChunkingSchemeCdc
        ? _config.ChunkSize * 4
        : _config.ChunkSize;

    /// <summary>
    /// Hard upper bound on a single downloaded chunk blob (encrypted bytes),
    /// as a multiple of the repository's maximum chunk size. Blobs larger than
    /// this are rejected before decryption. Guards the download/decrypt
    /// boundary against malicious oversized articles (memory-exhaustion DoS).
    /// The 4x headroom covers AES-GCM overhead (28 bytes) plus future format
    /// growth; legitimate blobs are always ~chunkSize + 28 bytes.
    /// </summary>
    public long MaxChunkBlobBytes => (long)EffectiveMaxChunkSize * 4;

    /// <summary>
    /// Read-time cap for a single chunk download (NNTP article or HTTP
    /// response), in bytes. yEnc inflates binary data (worst case ~2x when
    /// every byte needs escaping), plus article headers, so this is 2x the
    /// blob cap.
    /// </summary>
    public long MaxDownloadBytes => MaxChunkBlobBytes * 2;

    private readonly string _root;
    private readonly RepositoryConfig _config;
    private readonly byte[] _key;
    private readonly IBlobStore _blobs;
    private readonly Catalog _catalog;
    private bool _disposed;

    /// <summary>Repository root directory (for tracker files, etc.).</summary>
    public string RepoRoot => _root;

    /// <summary>
    /// Returns the stable NNTP poster pseudonym for this repository,
    /// generating and persisting one (poster-&lt;8hex&gt;) on first use.
    /// The name is a neutral pseudonym — it never advertises the software.
    /// An explicit --poster run flag overrides this for the run without
    /// persisting.
    /// </summary>
    public string GetOrCreatePoster()
    {
        if (!string.IsNullOrWhiteSpace(_config.Poster))
            return _config.Poster!;
        string poster = "poster-" + Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        _config.Poster = poster;
        SaveConfig();
        return poster;
    }

    private void SaveConfig()
    {
        string repoJsonPath = Path.Combine(_root, "repo.json");
        string tmpPath = repoJsonPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmpPath,
            FileKeepJsonOptions.Serialize(_config,
                FileKeepJsonOptions.Indented, FileKeepJsonContext.Default.RepositoryConfig));
        File.Move(tmpPath, repoJsonPath, overwrite: true);
    }

    private BackupRepository(string root, RepositoryConfig config, byte[] key)
    {
        _root = root;
        _config = config;
        _key = key;
        _blobs = new LocalBlobStore(root);
        _catalog = new Catalog(Path.Combine(root, "catalog.db"));
    }

    /// <summary>
    /// Opens a repo using a custom blob store (e.g., SmbBlobStore for remote chunks).
    /// The repo directory must still exist locally for manifests and catalog.
    /// </summary>
    internal BackupRepository(string root, RepositoryConfig config, byte[] key, IBlobStore blobs)
    {
        _root = root;
        _config = config;
        _key = key;
        _blobs = blobs;
        _catalog = new Catalog(Path.Combine(root, "catalog.db"));
    }

    // ---------- lifecycle ----------

    public static BackupRepository Init(
        string repoPath,
        string passphrase,
        int chunkSize = DefaultChunkSize,
        int kdfIterations = DefaultKdfIterations,
        long volumeSizeBytes = DefaultVolumeSizeBytes,
        string chunkingScheme = ChunkingSchemeCdc)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        if (chunkSize < 4096)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be >= 4 KiB.");
        if (volumeSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(volumeSizeBytes), "Volume size must be positive.");
        string scheme = (chunkingScheme ?? ChunkingSchemeCdc).ToLowerInvariant();
        if (scheme != ChunkingSchemeFixed && scheme != ChunkingSchemeCdc)
            throw new ArgumentOutOfRangeException(nameof(chunkingScheme),
                $"Unknown chunking scheme '{chunkingScheme}'. Expected '{ChunkingSchemeFixed}' or '{ChunkingSchemeCdc}'.");
        if (scheme == ChunkingSchemeCdc)
        {
            // The Gear mask trick needs a power-of-two target; the 64 KiB
            // floor keeps the derived minimum (avg/4) at a sane 16 KiB.
            if ((chunkSize & (chunkSize - 1)) != 0 || chunkSize < 65536)
                throw new ArgumentOutOfRangeException(nameof(chunkSize),
                    "CDC chunking requires a power-of-two chunk size >= 64 KiB (it is the target average).");
        }

        Directory.CreateDirectory(repoPath);
        string repoJsonPath = Path.Combine(repoPath, "repo.json");
        if (File.Exists(repoJsonPath))
            throw new InvalidOperationException($"A repository already exists at {repoPath}.");

        var config = new RepositoryConfig
        {
            CreatedUtc = DateTime.UtcNow,
            KdfIterations = kdfIterations,
            KdfSaltB64 = Convert.ToBase64String(KeyDerivation.NewSalt()),
            ChunkSize = chunkSize,
            ChunkingScheme = scheme,
            VolumeSizeBytes = volumeSizeBytes,
        };
        // Atomic write: a crash mid-write must not leave a truncated repo.json
        // (Open would reject it as corrupt while Init refuses to overwrite).
        string tmpPath = repoJsonPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmpPath,
            FileKeepJsonOptions.Serialize(config,
                FileKeepJsonOptions.Indented, FileKeepJsonContext.Default.RepositoryConfig));
        try { File.Move(tmpPath, repoJsonPath); }
        catch (IOException)
        {
            // Lost the race with a concurrent Init: clean up and report.
            try { File.Delete(tmpPath); } catch { }
            throw new InvalidOperationException($"A repository already exists at {repoPath}.");
        }
        catch
        {
            // Any other move failure: don't leak the temp file.
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
        Directory.CreateDirectory(Path.Combine(repoPath, "manifests"));
        Directory.CreateDirectory(Path.Combine(repoPath, "chunks"));
        Directory.CreateDirectory(Path.Combine(repoPath, "parity"));

        byte[] key = KeyDerivation.DeriveKey(passphrase, Convert.FromBase64String(config.KdfSaltB64), kdfIterations);
        string fullRoot = Path.GetFullPath(repoPath);
        OperationLog.Append(fullRoot, "init", $"chunk_size={chunkSize} chunking={scheme} kdf_iterations={kdfIterations}");
        return new BackupRepository(fullRoot, config, key);
    }

    public static BackupRepository Open(string repoPath, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        string configPath = Path.Combine(repoPath, "repo.json");
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"No repository found at {repoPath} (missing repo.json).");

        var config = JsonSerializer.Deserialize(File.ReadAllText(configPath), FileKeepJsonContext.Default.RepositoryConfig)
            ?? throw new InvalidDataException("repo.json is corrupt.");
        if (config.FormatVersion != FormatVersion)
            throw new NotSupportedException($"Repository format '{config.FormatVersion}' is not supported by this build (supports {FormatVersion}).");

        byte[] key = KeyDerivation.DeriveKey(
            passphrase, Convert.FromBase64String(config.KdfSaltB64), config.KdfIterations);
        return new BackupRepository(Path.GetFullPath(repoPath), config, key);
    }

    /// <summary>
    /// Opens a repo using a custom blob store (e.g., SmbBlobStore for remote).
    /// Manifests and catalog are still read from the local repo directory.
    /// </summary>
    public static BackupRepository OpenWithStore(string repoPath, string passphrase, IBlobStore blobs)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(blobs);
        string configPath = Path.Combine(repoPath, "repo.json");
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"No repository found at {repoPath} (missing repo.json).");

        var config = JsonSerializer.Deserialize(File.ReadAllText(configPath), FileKeepJsonContext.Default.RepositoryConfig)
            ?? throw new InvalidDataException("repo.json is corrupt.");
        if (config.FormatVersion != FormatVersion)
            throw new NotSupportedException($"Repository format '{config.FormatVersion}' is not supported by this build (supports {FormatVersion}).");

        byte[] key = KeyDerivation.DeriveKey(
            passphrase, Convert.FromBase64String(config.KdfSaltB64), config.KdfIterations);
        return new BackupRepository(Path.GetFullPath(repoPath), config, key, blobs);
    }

    // ---------- backup ----------

    /// <summary>Full backup: every file is chunked.</summary>
    /// <summary>
    /// Full backup of a directory tree. When <paramref name="snapshotProvider"/>
    /// is supplied, files are read through the provider instead of the live
    /// tree (e.g. with Windows backup privilege to bypass exclusive locks);
    /// the caller owns and disposes the provider. Null reads the live tree directly.
    /// </summary>
    public BackupManifest BackupDirectory(string sourceDir, ISnapshotProvider? snapshotProvider = null)
    {
        StorageGuard.EnsureRepoNotInSource(_root, sourceDir);
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
    ///
    /// The <paramref name="verificationMode"/> controls how "unchanged" is
    /// determined: <see cref="VerificationMode.Fast"/> uses metadata only,
    /// <see cref="VerificationMode.Verify"/> adds SHA-256 confirmation for
    /// metadata matches, <see cref="VerificationMode.Paranoid"/> hashes
    /// every file.
    /// </summary>
    public BackupManifest BackupIncremental(
        string sourceDir,
        string parentBackupId,
        ISnapshotProvider? snapshotProvider = null,
        VerificationMode verificationMode = VerificationMode.Fast)
    {
        var parent = LoadManifest(parentBackupId); // validates parent root hash
        if (parent.ChunkSize != _config.ChunkSize)
            throw new InvalidOperationException(
                $"Parent backup {parentBackupId} uses chunk size {parent.ChunkSize}, " +
                $"but this repository uses {_config.ChunkSize}.");
        string parentScheme = parent.Chunking ?? ChunkingSchemeFixed; // absent = v1 fixed
        if (parentScheme != _config.ChunkingScheme)
            throw new InvalidOperationException(
                $"Parent backup {parentBackupId} uses chunking '{parentScheme}', " +
                $"but this repository uses '{_config.ChunkingScheme}'.");
        var manifest = BackupDirectoryInternal(sourceDir, parent, snapshotProvider, verificationMode);
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

    private BackupManifest BackupDirectoryInternal(
        string sourceDir,
        BackupManifest? parent,
        ISnapshotProvider? snapshotProvider,
        VerificationMode verificationMode = VerificationMode.Fast,
        IBatchedBackupContext? batch = null)
    {
        string fullSource = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(fullSource))
            throw new DirectoryNotFoundException($"Source directory not found: {fullSource}");

        // The provider is caller-owned; only dispose one we created ourselves.
        ISnapshotProvider? owned = null;
        ISnapshotProvider snap = snapshotProvider ?? (owned = new NullSnapshotProvider(fullSource));
        try
        {
            return BackupDirectoryFromRoot(fullSource, snap, parent, verificationMode, batch);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// File backup with batched staging: chunks are staged through
    /// <paramref name="batch"/> and flushed (upload → confirm → prune) as
    /// batches fill, so the local staging area never holds more than about
    /// one batch. Returns the manifest WITHOUT finalizing it — the caller
    /// must flush the remainder, inherit parent volumes for incrementals,
    /// then call <see cref="FinalizeBatchedManifest"/>.
    /// </summary>
    public BackupManifest BackupDirectoryBatched(
        string sourceDir,
        BackupManifest? parent,
        ISnapshotProvider? snapshotProvider,
        IBatchedBackupContext batch,
        VerificationMode verificationMode = VerificationMode.Fast,
        string? backupId = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        StorageGuard.EnsureRepoNotInSource(_root, sourceDir);
        var manifest = BackupDirectoryInternal(sourceDir, parent, snapshotProvider, verificationMode, batch);
        manifest.BackupId = backupId ?? manifest.BackupId;
        manifest.Type = parent is null ? "full" : "inc";
        if (parent is not null)
            manifest.ParentId = parent.BackupId;
        return manifest;
    }

    /// <summary>
    /// Completes a batched backup: computes the root hash, writes the
    /// manifest, and records it in the catalog. Manifests are never pruned.
    /// </summary>
    public BackupManifest FinalizeBatchedManifest(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return FinalizeManifest(manifest);
    }

    /// <summary>
    /// Whole-disk image backup with batched staging: the device is read
    /// sequentially (sparse zero-block gaps are recorded as empty chunk IDs
    /// in the manifest, exactly like the non-batched paths) and chunks are
    /// flushed per batch. Returns the manifest WITHOUT finalizing it —
    /// the caller must flush the remainder, then finalize.
    /// </summary>
    public BackupManifest BackupDiskImageBatched(
        string devicePath,
        string imageName,
        bool sparse,
        string? volumeLetter,
        IBatchedBackupContext batch,
        string? backupId = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (string.IsNullOrWhiteSpace(imageName) || imageName.Contains('/') || imageName.Contains('\\'))
            throw new ArgumentException("Image name must be a plain file name.", nameof(imageName));
        StorageGuard.EnsureRepoNotOnDrive(_root, devicePath);

        using var stream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.ChunkSize);
        try
        {
            FileEntry entry;
            if (sparse)
                entry = BackupStreamSparse(stream, imageName, DateTime.UtcNow, buffer,
                    batch.Tracker, volumeLetter, batch);
            else
                entry = BackupStream(stream, imageName, DateTime.UtcNow, buffer,
                    batch.Tracker, batch);
            var manifest = new BackupManifest
            {
                BackupId = backupId ?? Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                Source = devicePath,
                ChunkSize = _config.ChunkSize,
                Chunking = _config.ChunkingScheme,
                Kind = "disk-image",
                Files = { entry },
            };
            manifest.Type = "full";
            return manifest;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private BackupManifest BackupDirectoryFromRoot(string fullSource, ISnapshotProvider snap, BackupManifest? parent, VerificationMode verificationMode, IBatchedBackupContext? batch = null)
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
            Chunking = _config.ChunkingScheme,
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

        // Parallel file processing: hash/chunk/encrypt across CPU cores.
        // Each file is independent (content-addressed chunk IDs); results are
        // collected by index and added to the manifest in sorted path order
        // for determinism. The tracker is thread-safe; flushes are serialized.
        var entries = new FileEntry?[files.Length];
        var parallelOptions = new ParallelOptions
        {
            // Leave one core for the upload pipeline and OS.
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };
        Parallel.For(0, files.Length, parallelOptions, i =>
        {
            string rel = files[i];
            string full = Path.Combine(readRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (parentByPath is not null &&
                parentByPath.TryGetValue(rel, out var parentEntry) &&
                IsUnchangedForMode(full, parentEntry, snap, verificationMode))
            {
                entries[i] = parentEntry;
            }
            else
            {
                // Per-thread buffer (ArrayPool is thread-safe).
                byte[] threadBuffer = ArrayPool<byte>.Shared.Rent(_config.ChunkSize);
                try
                {
                    entries[i] = BackupOneFile(snap, full, rel, threadBuffer, batch);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(threadBuffer);
                }
            }
        });

        // Deterministic order: sorted by path, matching the sequential behavior.
        foreach (var entry in entries)
            manifest.Files.Add(entry!);

        return manifest;
    }

    private void WriteManifest(BackupManifest manifest)
    {
        string manifestPath = Path.Combine(_root, "manifests", manifest.BackupId + ".json");
        // Atomic write via temp file: a crash mid-write must not leave a
        // truncated manifest that the catalog already references.
        string tmpPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmpPath, manifest.ToJson());
        try { File.Move(tmpPath, manifestPath, overwrite: true); }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Imports a manifest discovered on Usenet (already authenticated via
    /// AES-GCM). Writes it to manifests/ and records it in the catalog so
    /// download/restore treat it like a local backup.
    /// </summary>
    public void ImportManifest(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.VerifyRootHash())
            throw new InvalidDataException(
                $"Imported manifest {manifest.BackupId} failed root-hash verification.");
        WriteManifest(manifest);
        _catalog.RecordBackup(manifest.BackupId, manifest.Type, manifest.Source ?? "",
            manifest.CreatedUtc);
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

    /// <summary>
    /// Determines if a file is unchanged according to the verification mode.
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="VerificationMode.Fast"/>: metadata (size+mtime) only.
    /// </description></item>
    /// <item><description>
    /// <see cref="VerificationMode.Verify"/>: metadata fast path, plus
    /// SHA-256 verification for files that pass. Catches timestamp spoofing.
    /// </description></item>
    /// <item><description>
    /// <see cref="VerificationMode.Paranoid"/>: SHA-256 every file, ignore
    /// metadata entirely.
    /// </description></item>
    /// </list>
    /// </summary>
    private bool IsUnchangedForMode(
        string fullPath,
        FileEntry parentEntry,
        ISnapshotProvider snap,
        VerificationMode mode)
    {
        // Symlinks: metadata check is sufficient in all modes (target is
        // the content; hashing doesn't apply).
        bool isLink = (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
        if (isLink)
            return IsUnchanged(fullPath, parentEntry);

        switch (mode)
        {
            case VerificationMode.Fast:
                return IsUnchanged(fullPath, parentEntry);

            case VerificationMode.Verify:
                // Fast path first; if metadata matches, verify with hash.
                if (!IsUnchanged(fullPath, parentEntry))
                    return false;
                return HashMatches(fullPath, parentEntry, snap);

            case VerificationMode.Paranoid:
                // Skip metadata entirely; hash everything.
                return HashMatches(fullPath, parentEntry, snap);

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>
    /// Computes SHA-256 of the file and compares to the parent entry's hash.
    /// </summary>
    private bool HashMatches(string fullPath, FileEntry parentEntry, ISnapshotProvider snap)
    {
        try
        {
            using var stream = snap.OpenRead(fullPath);
            string hash = Hashing.Sha256Hex(stream);
            return string.Equals(hash, parentEntry.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If we can't read the file, it's not "unchanged" — let the
            // backup path handle (and report) the error.
            return false;
        }
    }

    private FileEntry BackupOneFile(ISnapshotProvider snap, string fullPath, string relPath, byte[] buffer,
        IBatchedBackupContext? batch = null)
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
        return BackupStream(stream, relPath, File.GetLastWriteTimeUtc(fullPath), buffer,
            destination: batch?.Tracker, batch: batch);
    }

    /// <summary>
    /// Chunks, hashes, encrypts and stores a byte stream as one manifest file
    /// entry. Used for regular files and for raw disk images alike.
    /// </summary>
    /// <summary>
    /// Chunks, hashes, encrypts and stores a byte stream as one manifest file
    /// entry. Used for regular files and for raw disk images alike.
    /// The chunking scheme comes from the repository config: fixed-size
    /// chunks (v1 behavior) or content-defined chunking.
    /// </summary>
    private FileEntry BackupStream(Stream stream, string relPath, DateTime mtimeUtc, byte[] buffer,
        IBlobStore? destination = null, IBatchedBackupContext? batch = null)
    {
        // Destination for chunk blobs: local repo by default, or a remote
        // store (SMB/NNTP) for streaming backups where local staging of the
        // full payload is infeasible. Chunks are encrypted before Put in
        // either case; only the blob location differs.
        var blobs = destination ?? _blobs;

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
        long total = _config.ChunkingScheme == ChunkingSchemeCdc
            ? ChunkStreamCdc(stream, entry, fileHash, blobs, batch)
            : ChunkStreamFixed(stream, entry, fileHash, buffer, blobs, batch);

        entry.Size = size >= 0 ? size : total;
        if (size >= 0 && size != total)
            throw new IOException(
                $"Stream length changed during backup of '{relPath}': expected {size} bytes, read {total}.");
        entry.Sha256 = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
        return entry;
    }

    /// <summary>
    /// Batches chunk dedup checks to reduce SQLite round-trips. Accumulates
    /// (chunkId, plaintext) pairs; on <see cref="Flush"/>, does a single
    /// <see cref="Catalog.HasChunks"/> query, encrypts+stores only unknown
    /// chunks, records all in a single transaction, and updates file entries
    /// and hashes in order. Bounds memory to <see cref="MaxPendingChunks"/>
    /// chunks (64MB max for 1MB chunks).
    /// </summary>
    private sealed class BatchedChunkStager : IDisposable
    {
        private const int MaxPendingChunks = 64;

        private readonly BackupRepository _repo;
        private readonly byte[] _blobBuf;
        private readonly List<PendingChunk> _pending = new();
        private bool _disposed;

        private sealed record PendingChunk(
            string Id, byte[] Plaintext, FileEntry Entry,
            IncrementalHash FileHash, IBlobStore Blobs);

        public BatchedChunkStager(BackupRepository repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            // Size for the largest possible chunk (CDC max is 4x average).
            _blobBuf = ArrayPool<byte>.Shared.Rent(
                repo.EffectiveMaxChunkSize + ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes);
        }

        /// <summary>
        /// Queues a chunk for batched processing. The plaintext is copied
        /// (the caller's buffer is reused). Flushes automatically when full.
        /// </summary>
        public void AddChunk(ReadOnlySpan<byte> plaintext, FileEntry entry,
            IncrementalHash fileHash, IBlobStore blobs, IBatchedBackupContext? batch)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string id = Hashing.Sha256Hex(plaintext);
            _pending.Add(new PendingChunk(id, plaintext.ToArray(), entry, fileHash, blobs));
            if (_pending.Count >= MaxPendingChunks)
                Flush(batch);
        }

        /// <summary>
        /// Processes all pending chunks: single HasChunks query, encrypt+store
        /// unknowns, record all in one transaction, update entries/hashes in
        /// order. Calls <paramref name="batch"/>.CheckFlush() afterwards.
        /// </summary>
        public void Flush(IBatchedBackupContext? batch)
        {
            if (_pending.Count == 0)
                return;

            // Single DB round-trip for the whole batch.
            var ids = new List<string>(_pending.Count);
            foreach (var p in _pending)
                ids.Add(p.Id);
            HashSet<string> known = _repo._catalog.HasChunks(ids);

            var toRecord = new List<(string id, long sizeBytes)>(_pending.Count);
            foreach (var p in _pending)
            {
                // Skip encrypt+store if catalog knows it OR local store has it
                // (covers crash-recovery edge where Put succeeded but
                // RecordChunk didn't).
                if (!known.Contains(p.Id) && !p.Blobs.Exists(p.Id))
                {
                    var blobSpan = _blobBuf.AsSpan(0,
                        p.Plaintext.Length + ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes);
                    ChunkCrypto.EncryptInto(p.Plaintext, _repo._key,
                        Hashing.HexToBytes(p.Id), blobSpan);
                    p.Blobs.Put(p.Id, blobSpan);
                }
                toRecord.Add((p.Id, p.Plaintext.Length));
                p.Entry.Chunks.Add(p.Id);
                p.FileHash.AppendData(p.Plaintext);
            }
            // Single transaction for all catalog writes.
            _repo._catalog.RecordChunks(toRecord);

            _pending.Clear();
            batch?.CheckFlush(); // throws OperationCanceledException on abort
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_blobBuf);
        }
    }

    /// <summary>
    /// Hashes, encrypts and stores one plaintext chunk, recording it on the
    /// file entry. Shared by both chunking schemes: chunk IDs are SHA-256 of
    /// plaintext either way, so deduplication is scheme-agnostic.
    /// </summary>
    private void StoreChunk(ReadOnlySpan<byte> chunkPlain, FileEntry entry, IncrementalHash fileHash,
        byte[] blobBuf, IBlobStore blobs)
    {
        string chunkId = Hashing.Sha256Hex(chunkPlain);
        if (!blobs.Exists(chunkId))
        {
            var blobSpan = blobBuf.AsSpan(0, chunkPlain.Length + ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes);
            ChunkCrypto.EncryptInto(chunkPlain, _key, Hashing.HexToBytes(chunkId), blobSpan);
            blobs.Put(chunkId, blobSpan);
        }
        _catalog.RecordChunk(chunkId, chunkPlain.Length);
        entry.Chunks.Add(chunkId);
        fileHash.AppendData(chunkPlain);
    }

    /// <summary>Fixed-size chunking path (v1 behavior).</summary>
    private long ChunkStreamFixed(Stream stream, FileEntry entry, IncrementalHash fileHash, byte[] buffer,
        IBlobStore blobs, IBatchedBackupContext? batch)
    {
        // Batched dedup: accumulate chunks, single HasChunks query per batch.
        using var stager = new BatchedChunkStager(this);
        try
        {
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, _config.ChunkSize)) > 0)
            {
                stager.AddChunk(buffer.AsSpan(0, read), entry, fileHash, blobs, batch);
                total += read;
                // Note: CheckFlush is called by stager.Flush (every 64 chunks
                // or at end), not per chunk. Throws on abort.
            }
            stager.Flush(batch);
            return total;
        }
        finally
        {
            // stager.Dispose returns the blob buffer
        }
    }

    /// <summary>
    /// Content-defined chunking path (Gear rolling hash). Bytes stream into
    /// an accumulation buffer; the chunker reports boundary lengths and each
    /// completed chunk is stored from the front of the buffer, with the
    /// remainder shifted down. The chunker's max-size force-cut guarantees
    /// the buffer never overflows.
    /// </summary>
    private long ChunkStreamCdc(Stream stream, FileEntry entry, IncrementalHash fileHash,
        IBlobStore blobs, IBatchedBackupContext? batch)
    {
        int maxChunk = EffectiveMaxChunkSize;
        int avgChunk = _config.ChunkSize; // CDC target average; min = avg/4, max = avg*4
        var chunker = new CdcChunker(avgChunk / 4, avgChunk, avgChunk * 4);
        byte[] accum = ArrayPool<byte>.Shared.Rent(maxChunk);
        var completed = new List<int>();
        long total = 0;
        // Batched dedup: accumulate chunks, single HasChunks query per batch.
        using var stager = new BatchedChunkStager(this);
        try
        {
            int accumLen = 0;
            void Drain(int chunkLen)
            {
                stager.AddChunk(accum.AsSpan(0, chunkLen), entry, fileHash, blobs, batch);
                total += chunkLen;
                int rest = accumLen - chunkLen;
                Buffer.BlockCopy(accum, chunkLen, accum, 0, rest);
                accumLen = rest;
                // Note: CheckFlush is called by stager.Flush, not per chunk.
            }

            int read;
            while ((read = stream.Read(accum, accumLen, accum.Length - accumLen)) > 0)
            {
                chunker.Feed(accum.AsSpan(accumLen, read), completed);
                accumLen += read;
                foreach (int chunkLen in completed)
                    Drain(chunkLen);
                completed.Clear();
                if (accumLen == accum.Length)
                {
                    // Unreachable: the chunker force-cuts at maxChunk, so a
                    // full buffer always yields a completed chunk above.
                    // Cut manually rather than spin on a full buffer.
                    Drain(accum.Length);
                    chunker.Reset();
                }
            }
            chunker.Finish(completed);
            foreach (int chunkLen in completed)
                Drain(chunkLen);
            stager.Flush(batch);
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(accum);
        }
    }

    // ---------- disk images ----------

    /// <summary>
    /// Backs up a raw block device (e.g. <c>\\.\C:</c> on Windows,
    /// <c>/dev/sda</c> on Linux) as a single image entry. The device is read
    /// sequentially through the normal chunk/encrypt pipeline, so the image
    /// gets the same per-chunk authentication and deduplication as files.
    /// The device is opened with <see cref="FileShare.ReadWrite"/> so a live
    /// volume can be imaged. Note the image is of the live volume, not a
    /// point-in-time copy.
    /// </summary>
    public BackupManifest BackupDiskImage(string devicePath, string imageName = "disk.img")
    {
        if (string.IsNullOrWhiteSpace(imageName) || imageName.Contains('/') || imageName.Contains('\\'))
            throw new ArgumentException("Image name must be a plain file name.", nameof(imageName));
        StorageGuard.EnsureRepoNotOnDrive(_root, devicePath);

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
                Chunking = _config.ChunkingScheme,
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
    /// Streams a raw block device backup directly to a remote blob store
    /// (SMB share or Usenet), without staging chunks locally. Each chunk is
    /// read, encrypted, and <c>Put</c> to <paramref name="destination"/> as
    /// it comes off the device — only a single chunk buffer is held in memory
    /// at a time, so a multi-terabyte drive can be imaged without needing
    /// that much free local disk. The manifest and catalog (tiny) still go to
    /// the local repo; the bulk payload lives only on the destination.
    /// </summary>
    public BackupManifest BackupDiskImageStreaming(string devicePath, IBlobStore destination, string imageName = "disk.img")
    {
        return BackupDiskImageStreaming(devicePath, destination, imageName, sparse: false, volumeLetter: null);
    }

    /// <summary>
    /// Streaming disk-image backup with used-space-only optimization.
    /// When <paramref name="sparse"/> is true, only allocated/non-zero chunks
    /// are stored; skipped chunk positions are marked with an empty string
    /// in the manifest and restored as zeros. If <paramref name="volumeLetter"/>
    /// is given (e.g. "C:"), the NTFS volume bitmap is used to identify
    /// allocated clusters; otherwise (or if the bitmap is unavailable),
    /// chunks that are all zeros are skipped.
    /// </summary>
    public BackupManifest BackupDiskImageStreaming(string devicePath, IBlobStore destination, string imageName,
        bool sparse, string? volumeLetter)
    {
        if (string.IsNullOrWhiteSpace(imageName) || imageName.Contains('/') || imageName.Contains('\\'))
            throw new ArgumentException("Image name must be a plain file name.", nameof(imageName));
        ArgumentNullException.ThrowIfNull(destination);
        if (destination is SmbBlobStore smb)
            StorageGuard.EnsureSmbShareNotOnDrive(smb.SharePath, devicePath);

        using var stream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.ChunkSize);
        try
        {
            FileEntry entry;
            if (sparse)
                entry = BackupStreamSparse(stream, imageName, DateTime.UtcNow, buffer, destination, volumeLetter);
            else
                entry = BackupStream(stream, imageName, DateTime.UtcNow, buffer, destination);
            var manifest = new BackupManifest
            {
                BackupId = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                Source = devicePath,
                ChunkSize = _config.ChunkSize,
                Chunking = _config.ChunkingScheme,
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
    /// Sparse variant of <see cref="BackupStream"/>: only backs up chunks that
    /// are allocated (per NTFS bitmap) or non-zero. Skipped positions are
    /// recorded as empty strings in <c>entry.Chunks</c>; the SHA-256 is computed
    /// over the full image with zeros for skipped chunks.
    /// </summary>
    private FileEntry BackupStreamSparse(Stream stream, string relPath, DateTime mtimeUtc, byte[] buffer,
        IBlobStore destination, string? volumeLetter, IBatchedBackupContext? batch = null)
    {
        var entry = new FileEntry
        {
            Path = relPath,
            MtimeUtc = mtimeUtc,
        };

        // Try the NTFS volume bitmap for precise allocation info.
        List<(long StartCluster, long ClusterCount)>? ranges = null;
        int bytesPerCluster = 4096; // NTFS default; refined below if we get the bitmap
        if (!string.IsNullOrWhiteSpace(volumeLetter) && OperatingSystem.IsWindows())
        {
            try
            {
                ranges = VolumeBitmapReader.GetAllocatedRanges(volumeLetter);
                bytesPerCluster = GetBytesPerCluster(volumeLetter);
            }
            catch
            {
                // Fall back to zero-detection below.
                ranges = null;
            }
        }

        long size;
        try { size = stream.Length; }
        catch (NotSupportedException) { size = -1; }
        catch (IOException) { size = -1; }

        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] blobBuf = ArrayPool<byte>.Shared.Rent(
            _config.ChunkSize + ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes);
        byte[] zeroBuf = new byte[_config.ChunkSize]; // for hash of skipped chunks
        try
        {
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, _config.ChunkSize)) > 0)
            {
                var span = buffer.AsSpan(0, read);
                bool shouldBackup;

                if (ranges != null)
                {
                    // Bitmap: back up if any part of this chunk is allocated.
                    shouldBackup = VolumeBitmapReader.IsRangeAllocated(ranges, total, read, bytesPerCluster);
                }
                else
                {
                    // Fallback: back up if the chunk is not all zeros.
                    shouldBackup = !IsAllZeros(span);
                }

                if (shouldBackup)
                {
                    string chunkId = Hashing.Sha256Hex(span);
                    if (!destination.Exists(chunkId))
                    {
                        var blobSpan = blobBuf.AsSpan(0, read + ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes);
                        ChunkCrypto.EncryptInto(span, _key, Hashing.HexToBytes(chunkId), blobSpan);
                        destination.Put(chunkId, blobSpan);
                    }
                    _catalog.RecordChunk(chunkId, read);
                    entry.Chunks.Add(chunkId);
                    fileHash.AppendData(span);
                    if (batch is not null)
                        batch.CheckFlush(); // throws OperationCanceledException on abort
                }
                else
                {
                    // Sparse: mark position, hash zeros.
                    entry.Chunks.Add("");
                    fileHash.AppendData(zeroBuf.AsSpan(0, read));
                }
                total += read;
            }
            entry.Size = size >= 0 ? size : total;
            if (size >= 0 && size != total)
                throw new IOException(
                    $"Stream length changed during backup of '{relPath}': expected {size} bytes, read {total}.");
            entry.Sha256 = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blobBuf);
        }
        return entry;
    }

    private static bool IsAllZeros(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
            if (b != 0) return false;
        return true;
    }

    private static int GetBytesPerCluster(string volumeLetter)
    {
        // Use GetDiskFreeSpace to get bytes per cluster. Default to 4096 on failure.
        try
        {
            string root = volumeLetter.Trim().TrimEnd('\\');
            if (!root.EndsWith(":")) root += ":";
            root += "\\";
            if (GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector,
                out _, out _))
            {
                return (int)(sectorsPerCluster * bytesPerSector);
            }
        }
        catch { /* use default */ }
        return 4096;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetDiskFreeSpace(string lpRootPathName,
        out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

    /// <summary>
    /// Restores a disk-image backup (see <see cref="BackupDiskImage"/>) by
    /// writing the decrypted image back to a block device. Fails closed:
    /// every chunk is AES-GCM authenticated on decrypt, its SHA-256 is
    /// checked against the chunk ID, and the whole-image SHA-256 must match
    /// the manifest — any mismatch aborts the restore with an exception.
    /// </summary>
    public void RestoreDiskImage(string backupId, string devicePath)
    {
        RestoreDiskImage(backupId, devicePath, _blobs);
    }

    /// <summary>
    /// Restores a disk-image backup from the given blob source (local repo by
    /// default, or a remote store for streaming backups). Sparse images
    /// (empty-string chunk IDs) are restored with zeros for skipped positions.
    /// </summary>
    public void RestoreDiskImage(string backupId, string devicePath, IBlobStore source)
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
        // One pooled buffer reused for every decrypted chunk: zero per-chunk
        // allocation on the plaintext side. (The encrypted blob still comes
        // from source.Get; a pooled read path would be a separate change.)
        // Sized for the largest chunk this repo can produce (CDC chunks can
        // be up to 4x the target average); oversized blobs are rejected below.
        byte[] plainBuf = ArrayPool<byte>.Shared.Rent(EffectiveMaxChunkSize);
        byte[] zeroBuf = new byte[_config.ChunkSize];
        try
        {
            long position = 0;
            foreach (string chunkId in entry.Chunks)
            {
                int chunkLen;
                if (string.IsNullOrEmpty(chunkId))
                {
                    // Sparse: skipped chunk position — write zeros.
                    chunkLen = (int)Math.Min(_config.ChunkSize, entry.Size - position);
                    device.Write(zeroBuf, 0, chunkLen);
                    imageHash.AppendData(zeroBuf.AsSpan(0, chunkLen));
                }
                else
                {
                    byte[] blob = source.Get(chunkId); // throws if missing
                    int ctLen = blob.Length - ChunkCrypto.NonceSizeBytes - ChunkCrypto.TagSizeBytes;
                    if (ctLen < 0 || ctLen > plainBuf.Length)
                        throw new InvalidDataException(
                            $"Chunk {chunkId} has an invalid blob size ({blob.Length} bytes); restore aborted.");
                    var plainSpan = plainBuf.AsSpan(0, ctLen);
                    ChunkCrypto.DecryptInto(blob, _key, Hashing.HexToBytes(chunkId), plainSpan); // AES-GCM authenticates
                    if (!string.Equals(Hashing.Sha256Hex(plainSpan), chunkId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Chunk {chunkId} failed hash verification; restore aborted.");
                    device.Write(plainBuf, 0, ctLen);
                    imageHash.AppendData(plainSpan);
                    CryptographicOperations.ZeroMemory(plainSpan);
                    chunkLen = ctLen;
                }
                position += chunkLen;
            }
            device.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plainBuf);
        }

        string actual = Convert.ToHexString(imageHash.GetHashAndReset());
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Restored image hash mismatch for backup {backupId}: manifest says {entry.Sha256}, wrote {actual}.");

        OperationLog.Append(_root, "restore-disk",
            $"id={backupId} device={devicePath} bytes={entry.Size}");
    }

    // ---------- restore & verify ----------

    public BackupManifest LoadManifest(string backupId)
    {
        // Backup IDs are 32-hex GUIDs; reject path traversal early.
        if (string.IsNullOrEmpty(backupId) || backupId.Length != 32 ||
            !backupId.All(c => Uri.IsHexDigit(c)))
            throw new ArgumentException($"Invalid backup ID: {backupId}", nameof(backupId));
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
    /// Encrypted blob size without reading the blob (for volume planning).
    /// </summary>
    public long GetChunkBlobSize(string chunkIdHex)
    {
        if (_blobs is LocalBlobStore local)
        {
            try
            {
                return new FileInfo(local.PathFor(chunkIdHex)).Length;
            }
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new InvalidDataException($"Chunk {chunkIdHex} is missing from the repository.", ex);
            }
        }
        return GetChunkBlob(chunkIdHex).Length;
    }

    /// <summary>Target packed-volume size for Usenet uploads (repo.json).</summary>
    public long VolumeSizeBytes => _config.VolumeSizeBytes;

    /// <summary>Whether uploads pack chunks into volumes (repo.json).</summary>
    public bool UseVolumes => _config.UseVolumes;

    /// <summary>
    /// Decides whether an upload uses volume packing. Packing mode and
    /// volume size are sticky per backup: the manifest records the packing,
    /// and the server-side manifest article is immutable, so flipping modes
    /// or sizes for the same backup would silently break the upload/download
    /// contract. Flips are refused with a reason instead.
    /// </summary>
    /// <param name="manifest">The backup's manifest.</param>
    /// <param name="chunkIds">Canonical ordered chunk IDs.</param>
    /// <param name="forceChunkMode">Caller override (--no-volumes / config).</param>
    /// <param name="refusal">When returning false with a refusal, the reason.</param>
    public bool ResolveVolumeMode(
        BackupManifest manifest,
        string[] chunkIds,
        bool forceChunkMode,
        out string? refusal)
    {
        refusal = null;
        bool manifestHasVolumes = manifest.Volumes is { Count: > 0 };
        bool wantVolumes = UseVolumes && !forceChunkMode && chunkIds.Length > 0;
        if (manifestHasVolumes)
        {
            if (manifest.VolumeSize != VolumeSizeBytes)
            {
                refusal = $"backup {manifest.BackupId} was packed with volume size " +
                    $"{manifest.VolumeSize}, but repo.json now says {VolumeSizeBytes}. " +
                    "Volume size is sticky per backup; restore repo.json or re-backup.";
                return false;
            }
            return true;
        }
        if (wantVolumes && ChunkUploadRecorded(chunkIds))
        {
            refusal = $"backup {manifest.BackupId} was already uploaded per-chunk; volume " +
                "packing cannot be enabled retroactively. Re-backup or keep chunk mode.";
            return false;
        }
        return wantVolumes;
    }

    private bool ChunkUploadRecorded(string[] chunkIds)
    {
        using var catalog = new Catalog(CatalogPath);
        return chunkIds.Any(id => catalog.GetUploadTimeUtc(id) is not null);
    }

    /// <summary>
    /// Greedy volume packing plan for a manifest's chunks: ordered groups of
    /// chunk IDs sized to the repo's configured volume target. Reads no blob
    /// bytes — sizes come from the local store.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> PlanVolumes(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string[] chunkIds = ChunkOrdering.GetOrderedChunkIds(manifest);
        return VolumePacker.Plan(chunkIds, GetChunkBlobSize, VolumeSizeBytes);
    }

    /// <summary>
    /// For batched incremental backups: the child's own batches only packed
    /// newly-staged chunks, so parent-referenced chunks need their volume
    /// entries inherited from the parent manifest to keep the child's
    /// manifest self-contained for download and NZB generation. Each
    /// inherited entry keeps its original post-set name, so per-batch parity
    /// groups stay aligned on the download side.
    /// Returns false when some referenced chunk is covered by neither the
    /// child's new volumes nor the parent's volumes (e.g. the parent was
    /// uploaded chunk-mode) — the caller should fall back to the legacy
    /// full-staging path rather than produce an unrestorable manifest.
    /// </summary>
    public static bool TryInheritParentVolumes(
        BackupManifest child, BackupManifest parent, out List<VolumeEntry> inherited)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        inherited = new List<VolumeEntry>();

        var childCovered = new HashSet<string>(StringComparer.Ordinal);
        if (child.Volumes is not null)
            foreach (var v in child.Volumes)
                foreach (string id in v.ChunkIds)
                    childCovered.Add(id);

        // Parent volume entries indexed by chunk ID.
        var parentByChunk = new Dictionary<string, VolumeEntry>(StringComparer.Ordinal);
        if (parent.Volumes is not null)
            foreach (var v in parent.Volumes)
                foreach (string id in v.ChunkIds)
                    parentByChunk.TryAdd(id, v);

        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in child.Files)
            foreach (string id in f.Chunks)
                if (!string.IsNullOrEmpty(id) && !childCovered.Contains(id))
                    needed.Add(id);

        var seenVolumes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in needed)
        {
            if (!parentByChunk.TryGetValue(id, out VolumeEntry? entry))
                return false; // uncovered chunk — cannot inherit
            if (seenVolumes.Add(entry.Id))
                inherited.Add(entry);
        }
        return true;
    }

    /// <summary>
    /// Recomputes the root hash and writes the manifest back to manifests/.
    /// Used after upload-time mutations (volume packing) so the stored
    /// manifest matches what was posted to Usenet.
    /// </summary>
    public void SaveManifest(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.RootSha256 = manifest.ComputeRootHash();
        WriteManifest(manifest);
    }

    /// <summary>
    /// Decrypts and hash-verifies one stored chunk. Throws
    /// <see cref="CryptographicException"/> on wrong passphrase and
    /// <see cref="InvalidDataException"/> on corruption. Used by the recovery
    /// wizard for early passphrase validation.
    /// </summary>
    public void VerifyChunk(string chunkIdHex)
    {
        byte[] blob = _blobs.Get(chunkIdHex);
        byte[] chunkId = Hashing.HexToBytes(chunkIdHex);
        byte[] plaintext = ChunkCrypto.Decrypt(blob, _key, chunkId); // AES-GCM authenticates
        byte[] actual = Hashing.Sha256Bytes(plaintext);
        if (!CryptographicOperations.FixedTimeEquals(actual, chunkId))
            throw new InvalidDataException($"Chunk {chunkIdHex} failed hash verification.");
    }

    /// <summary>
    /// Short repository identity used in NNTP message-IDs, derived from
    /// the repository salt (stable; no migration for existing repos).
    /// </summary>
    public string RepoId => ArticleCodec.DeriveRepoId(Convert.FromBase64String(_config.KdfSaltB64));

    /// <summary>Path to catalog.db, which also holds the NNTP upload journal.</summary>
    public string CatalogPath => Path.Combine(_root, "catalog.db");

    /// <summary>
    /// The local chunk store, for batched staging (track + prune). Null when
    /// the repo was opened with a custom blob store (remote chunks).
    /// </summary>
    internal LocalBlobStore? LocalChunkStore => _blobs as LocalBlobStore;

    /// <summary>Catalog chunk-known query, for batched cross-batch dedup.</summary>
    internal bool CatalogHasChunk(string chunkIdHex) => _catalog.HasChunk(chunkIdHex);

    private ChunkMessageIndex? _messageIndex;

    /// <summary>
    /// Maps chunk IDs to their live NNTP message IDs, tracking republications
    /// performed by retention refresh. Every NntpBlobStore for this repo must
    /// receive this index — otherwise refreshed identities are posted but
    /// forgotten, and later reads resolve to the expired original IDs.
    /// </summary>
    public ChunkMessageIndex MessageIndex => _messageIndex ??= new ChunkMessageIndex(_root);

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
        IBlobStore remote,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(nzb);
        ArgumentNullException.ThrowIfNull(remote);

        var files = nzb.Files;
        // Collect all chunk IDs for parity group lookup
        var allChunkIds = files.Select(f => f.ChunkId)
            .Where(id => id is not null)
            .Cast<string>()
            .ToList();
        for (int i = 0; i < files.Count; i++)
            if (files[i].ChunkId is null)
                throw new InvalidDataException(
                    $"NZB file #{i + 1} ('{files[i].Subject}') does not reference a usenet-backup article.");

        return DownloadChunkIds(allChunkIds, remote, progress);
    }

    /// <summary>
    /// Downloads an explicit list of chunks (fetch, authenticate, hash-verify,
    /// store; parity reconstruction on failure). Used by
    /// <see cref="DownloadChunks"/> and by LAN recovery of volume-packed
    /// backups (the LAN server hosts chunks, not volume articles).
    /// </summary>
    public DownloadResult DownloadChunkIds(
        IReadOnlyList<string> chunkIds,
        IBlobStore remote,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        ArgumentNullException.ThrowIfNull(remote);

        int downloaded = 0, alreadyPresent = 0, reconstructed = 0;
        var allChunkIds = chunkIds.ToList();

        for (int i = 0; i < allChunkIds.Count; i++)
        {
            string chunkId = allChunkIds[i];
            string messageId = ArticleCodec.MakeMessageId(chunkId, RepoId);

            if (_blobs.Exists(chunkId))
            {
                _catalog.RecordDownload(messageId, chunkId); // adopt into journal
                alreadyPresent++;
            }
            else
            {
                byte[]? blob = null;
                try
                {
                    // ARTICLE + yEnc CRC-32 + header chunk-ID check inside.
                    blob = remote.Get(chunkId);
                    VerifyDownloadedBlob(chunkId, blob);
                }
                catch (Exception)
                {
                    // Chunk missing or corrupted — try parity reconstruction
                    blob = TryReconstructChunk(chunkId, allChunkIds, remote);
                    if (blob is null)
                        throw; // Reconstruction failed, rethrow original
                    reconstructed++;
                }
                _blobs.Put(chunkId, blob);
                _catalog.RecordChunk(chunkId, blob.Length);
                _catalog.RecordDownload(messageId, chunkId);
                downloaded++;
            }
            progress?.Invoke(i + 1, allChunkIds.Count);
        }
        return new DownloadResult(downloaded, alreadyPresent, allChunkIds.Count, reconstructed);
    }

    /// <summary>
    /// Volume-mode download: every volume referenced by a volume NZB is
    /// fetched as one article, unpacked, and each chunk is authenticated
    /// (AES-GCM) and hash-verified before storage — same fail-closed
    /// guarantees as <see cref="DownloadChunks"/>, at volume granularity.
    ///
    /// Interruption-safe: a volume whose chunks are all present locally is
    /// skipped (adopted into the download journal). The chunk->volume plan
    /// comes from the local manifest when available; otherwise volumes are
    /// fetched and unpacked to discover their chunks.
    /// </summary>
    /// <param name="nzb">Parsed volume NZB index (<see cref="NzbDocument.IsVolumeNzb"/>).</param>
    /// <param name="remote">NNTP-backed blob store to fetch from.</param>
    /// <param name="progress">Called as (done, total) after each volume.</param>
    public DownloadResult DownloadVolumes(
        NzbDocument nzb,
        NntpBlobStore remote,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(nzb);
        ArgumentNullException.ThrowIfNull(remote);
        if (!nzb.IsVolumeNzb)
            throw new ArgumentException("NZB is not a volume index.", nameof(nzb));

        // Chunk->volume plan for skip planning and parity reconstruction.
        // Best-effort: a missing manifest only costs extra fetches.
        Dictionary<string, IReadOnlyList<string>>? plan = null;
        // Post-set per volume, for batched backups: parity groups are
        // per-set, so reconstruction must scope groups to the volume's set.
        // Null (legacy backups) means the whole backup was one set.
        Dictionary<string, string?>? volumeSets = null;
        if (nzb.BackupId is not null)
        {
            try
            {
                var manifest = LoadManifest(nzb.BackupId);
                if (manifest.Volumes is { Count: > 0 })
                {
                    plan = manifest.Volumes.ToDictionary(
                        v => v.Id, v => (IReadOnlyList<string>)v.ChunkIds);
                    if (manifest.Volumes.Any(v => v.SetName is not null))
                        volumeSets = manifest.Volumes.ToDictionary(v => v.Id, v => v.SetName);
                }
            }
            catch { /* fall back to fetch-and-unpack */ }
        }

        var volumeIds = new List<string>(nzb.Files.Count);
        for (int i = 0; i < nzb.Files.Count; i++)
        {
            string? volumeId = nzb.Files[i].VolumeId;
            if (volumeId is null)
                throw new InvalidDataException(
                    $"NZB file #{i + 1} ('{nzb.Files[i].Subject}') does not reference a usenet-backup article.");
            volumeIds.Add(volumeId);
        }
        // Canonical (sorted) order for parity group alignment.
        var allVolumeIds = volumeIds.OrderBy(id => id, StringComparer.Ordinal).ToList();

        // Parity scope for a volume: its post set's volumes when the
        // manifest records per-batch sets, else the whole backup (legacy).
        IReadOnlyList<string> ParityScope(string vid)
        {
            if (volumeSets is not null &&
                volumeSets.TryGetValue(vid, out string? set) && set is not null)
            {
                return volumeIds
                    .Where(id => volumeSets.TryGetValue(id, out string? s) && s == set)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
            }
            return allVolumeIds;
        }

        int downloaded = 0, alreadyPresent = 0, reconstructed = 0;
        for (int i = 0; i < volumeIds.Count; i++)
        {
            string volumeId = volumeIds[i];
            string messageId = ArticleCodec.MakeVolumeMessageId(volumeId, RepoId);
            IReadOnlyList<string>? wanted = null;
            plan?.TryGetValue(volumeId, out wanted);

            if (wanted is not null && wanted.All(c => _blobs.Exists(c)))
            {
                _catalog.RecordDownload(messageId, volumeId); // adopt into journal
                alreadyPresent += wanted.Count;
            }
            else
            {
                byte[]? volumeBytes = null;
                try
                {
                    volumeBytes = remote.GetVolume(volumeId);
                    VolumePacker.Unpack(volumeBytes); // fail closed on bad framing
                }
                catch (Exception)
                {
                    // Volume missing or corrupted — try parity reconstruction
                    volumeBytes = TryReconstructVolume(volumeId, ParityScope(volumeId), plan, remote);
                    if (volumeBytes is null)
                        throw; // Reconstruction failed, rethrow original
                    reconstructed++;
                }
                foreach (var (chunkId, blob) in VolumePacker.Unpack(volumeBytes))
                {
                    if (_blobs.Exists(chunkId))
                    {
                        alreadyPresent++;
                        continue;
                    }
                    VerifyDownloadedBlob(chunkId, blob);
                    _blobs.Put(chunkId, blob);
                    _catalog.RecordChunk(chunkId, blob.Length);
                    downloaded++;
                }
                _catalog.RecordDownload(messageId, volumeId);
            }
            progress?.Invoke(i + 1, volumeIds.Count);
        }
        return new DownloadResult(downloaded, alreadyPresent, downloaded + alreadyPresent, reconstructed);
    }

    /// <summary>
    /// Attempts to reconstruct a missing/corrupt volume using XOR or PAR2
    /// parity over volumes. Sibling volume bytes come from a local rebuild
    /// (when all their chunks are present) or a remote fetch. Every chunk
    /// of a reconstructed volume is re-verified before it is accepted.
    /// Returns null if reconstruction is not possible.
    /// </summary>
    private byte[]? TryReconstructVolume(
        string volumeId,
        IReadOnlyList<string> allVolumeIds,
        Dictionary<string, IReadOnlyList<string>>? plan,
        NntpBlobStore remote)
    {
        byte[]? GetVolumeBytesOrNull(string vid)
        {
            try
            {
                if (plan is not null && plan.TryGetValue(vid, out var chunks) &&
                    chunks.All(c => _blobs.Exists(c)))
                    return VolumePacker.BuildVolume(chunks, _blobs.Get).Bytes;
                return remote.GetVolume(vid);
            }
            catch { return null; }
        }

        bool VerifyVolume(byte[] volumeBytes)
        {
            try
            {
                foreach (var (chunkId, blob) in VolumePacker.Unpack(volumeBytes))
                    VerifyDownloadedBlob(chunkId, blob);
                return true;
            }
            catch { return false; }
        }

        // Try XOR parity first (simpler, single parity block)
        var xorGroup = Redundancy.XorParity.GetGroupFor(volumeId, allVolumeIds);
        if (xorGroup.Count >= 2)
        {
            string parityId = Redundancy.XorParity.MakeParityId(xorGroup);
            try
            {
                byte[] parityBytes = remote.GetVolume(parityId);
                byte[]? reconstructed = Redundancy.XorParity.Reconstruct(
                    volumeId, xorGroup, GetVolumeBytesOrNull, parityBytes);
                if (reconstructed is not null && VerifyVolume(reconstructed))
                    return reconstructed;
            }
            catch { /* parity not available */ }
        }

        // Try PAR2 (Reed-Solomon, up to 3 missing)
        var par2Group = Redundancy.Par2Redundancy.GetGroupFor(volumeId, allVolumeIds);
        if (par2Group.Count >= 2)
        {
            try
            {
                var parityDatas = new List<byte[]>();
                for (int p = 0; p < Redundancy.Par2Redundancy.ParityShards; p++)
                {
                    string parityId = Redundancy.Par2Redundancy.MakeParityId(par2Group, p);
                    try { parityDatas.Add(remote.GetVolume(parityId)); }
                    catch { /* parity block not available */ }
                }

                if (parityDatas.Count > 0)
                {
                    var reconstructed = Redundancy.Par2Redundancy.Reconstruct(
                        par2Group, GetVolumeBytesOrNull, parityDatas);
                    if (reconstructed is not null &&
                        reconstructed.TryGetValue(volumeId, out byte[]? candidate) &&
                        VerifyVolume(candidate))
                        return candidate;
                }
            }
            catch { /* reconstruction failed */ }
        }

        return null;
    }

    /// <summary>
    /// Downloads a backup from an NZB index, dispatching on index kind:
    /// volume NZBs fetch packed volume articles (<see cref="DownloadVolumes"/>),
    /// chunk NZBs fetch one article per chunk (<see cref="DownloadChunks"/>).
    /// </summary>
    public DownloadResult DownloadNzb(
        NzbDocument nzb,
        NntpBlobStore remote,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(nzb);
        ArgumentNullException.ThrowIfNull(remote);
        return nzb.IsVolumeNzb
            ? DownloadVolumes(nzb, remote, progress)
            : DownloadChunks(nzb, remote, progress);
    }

    /// <summary>
    /// Attempts to reconstruct a missing chunk using XOR or PAR2 parity.
    /// Returns null if reconstruction is not possible.
    /// </summary>
    private byte[]? TryReconstructChunk(
        string chunkId,
        IReadOnlyList<string> allChunkIds,
        IBlobStore remote)
    {
        // Try XOR parity first (simpler, single parity block)
        var xorGroup = Redundancy.XorParity.GetGroupFor(chunkId, allChunkIds);
        if (xorGroup.Count >= 2)
        {
            string parityId = Redundancy.XorParity.MakeParityId(xorGroup);
            try
            {
                byte[] parityBytes = remote.Get(parityId);
                byte[]? reconstructed = Redundancy.XorParity.Reconstruct(
                    chunkId, xorGroup,
                    id => {
                        try {
                            if (_blobs.Exists(id)) return _blobs.Get(id);
                            return remote.Get(id);
                        } catch { return null; }
                    },
                    parityBytes);
                if (reconstructed is not null)
                {
                    // Verify the reconstructed chunk
                    try {
                        VerifyDownloadedBlob(chunkId, reconstructed);
                        return reconstructed;
                    } catch { /* verification failed */ }
                }
            }
            catch { /* parity not available */ }
        }

        // Try PAR2 (Reed-Solomon, up to 3 missing)
        // Uses Par2Redundancy.Reconstruct which handles the length header
        // and trims reconstructed shards to original sizes.
        var par2Group = Redundancy.Par2Redundancy.GetGroupFor(chunkId, allChunkIds);
        if (par2Group.Count >= 2)
        {
            try
            {
                // Fetch available parity blocks
                var parityDatas = new List<byte[]>();
                for (int p = 0; p < Redundancy.Par2Redundancy.ParityShards; p++)
                {
                    string parityId = Redundancy.Par2Redundancy.MakeParityId(par2Group, p);
                    try {
                        parityDatas.Add(remote.Get(parityId));
                    } catch { /* parity block not available */ }
                }

                if (parityDatas.Count > 0)
                {
                    var reconstructed = Redundancy.Par2Redundancy.Reconstruct(
                        par2Group,
                        id => {
                            try {
                                if (_blobs.Exists(id)) return _blobs.Get(id);
                                return remote.Get(id);
                            } catch { return null; }
                        },
                        parityDatas);

                    if (reconstructed is not null && reconstructed.TryGetValue(chunkId, out byte[]? candidate))
                    {
                        try {
                            VerifyDownloadedBlob(chunkId, candidate);
                            return candidate;
                        } catch { /* verification failed */ }
                    }
                }
            }
            catch { /* reconstruction failed */ }
        }

        return null;
    }

    /// <summary>
    /// Uploads an encrypted copy of a backup's manifest to Usenet so the
    /// recovery wizard can discover backups newer than the USB stick.
    /// The manifest is AES-256-GCM encrypted with the repo key (AAD binds
    /// it to the backup ID); without the passphrase it is opaque bytes.
    /// Idempotent: skips when the article already exists.
    /// </summary>
    public int UploadManifest(string backupId, NntpBlobStore remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        string manifestPath = Path.Combine(_root, "manifests", backupId + ".json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"No local manifest for backup {backupId}.");
        byte[] plaintext = File.ReadAllBytes(manifestPath);
        byte[] aad = Hashing.Sha256Bytes(System.Text.Encoding.UTF8.GetBytes(backupId));
        byte[] encrypted = ChunkCrypto.Encrypt(plaintext, _key, aad);
        try
        {
            remote.PostManifest(backupId, encrypted);
            OperationLog.Append(_root, "manifest-upload", $"id={backupId}");
            // Update the monthly manifest index (versioned; Usenet articles are immutable).
            bool indexPosted = UpdateManifestIndex(remote);
            return indexPosted ? 2 : 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    /// <summary>
    /// Posts/updates the versioned monthly manifest index listing all local
    /// backup IDs for the current year-month. Each upload creates a new
    /// version; discovery fetches the latest via STAT probing.
    /// </summary>
    private bool UpdateManifestIndex(NntpBlobStore remote)
    {
        string yearMonth = DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        string manifestsDir = Path.Combine(_root, "manifests");
        if (!Directory.Exists(manifestsDir))
            return false;
        var backupIds = Directory.GetFiles(manifestsDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(id => id.Length == 32 && id.All(Uri.IsHexDigit))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (backupIds.Count == 0)
            return false;
        int version = remote.PostManifestIndex(yearMonth, backupIds);
        OperationLog.Append(_root, "manifest-index-upload", $"ym={yearMonth} v={version} count={backupIds.Count}");
        return true;
    }

    /// <summary>
    /// A manifest discovered on Usenet that is not on the local USB.
    /// </summary>
    public sealed record RemoteManifest(string BackupId, BackupManifest Manifest);

    /// <summary>
    /// Scans Usenet for encrypted manifests from this repo, downloads and
    /// decrypts any not present locally, and returns them newest-first.
    /// Fails closed: wrong passphrase or tampering throws; bad manifests
    /// are never stored locally.
    /// </summary>
    public IReadOnlyList<RemoteManifest> DiscoverRemoteManifests(NntpBlobStore remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var result = new List<RemoteManifest>();
        foreach (string backupId in remote.ListManifestIds())
        {
            string localPath = Path.Combine(_root, "manifests", backupId + ".json");
            if (File.Exists(localPath))
                continue; // already on USB
            byte[]? encrypted = remote.GetManifest(backupId);
            if (encrypted is null)
                continue;
            byte[] aad = Hashing.Sha256Bytes(System.Text.Encoding.UTF8.GetBytes(backupId));
            byte[] plaintext;
            try
            {
                plaintext = ChunkCrypto.Decrypt(encrypted, _key, aad);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    $"Remote manifest {backupId} failed authentication (wrong passphrase or tampered).", ex);
            }
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(plaintext);
                var manifest = JsonSerializer.Deserialize(json, FileKeepJsonContext.Default.BackupManifest)
                    ?? throw new InvalidDataException($"Remote manifest {backupId} is corrupt.");
                if (!manifest.BackupId.Equals(backupId, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Remote manifest backup ID mismatch (expected {backupId}).");
                result.Add(new RemoteManifest(backupId, manifest));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        return result.OrderByDescending(m => m.Manifest.CreatedUtc).ToList();
    }

    /// <summary>
    /// Discovers remote manifests via versioned monthly index articles,
    /// without scanning the newsgroup. Walks backward from the current
    /// month, STAT-probing for index versions. Each STAT is a single
    /// round-trip; a 60-day gap costs ~4 STATs, not a 16.9B-article LISTGROUP.
    /// </summary>
    /// <param name="monthsBack">How many months to search backward (default 12).</param>
    public IReadOnlyList<RemoteManifest> DiscoverRemoteManifestsViaIndex(
        NntpBlobStore remote, int monthsBack = 12)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RemoteManifest>();
        DateTime now = DateTime.UtcNow;
        for (int m = 0; m < monthsBack; m++)
        {
            DateTime dt = now.AddMonths(-m);
            string yearMonth = dt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            IReadOnlyList<string>? backupIds = remote.GetLatestManifestIndex(yearMonth);
            if (backupIds is null)
                continue;
            foreach (string backupId in backupIds)
            {
                if (!seen.Add(backupId))
                    continue; // already processed from a newer month
                // Validate network-supplied IDs: must be 32-hex GUIDs.
                if (backupId.Length != 32 || !backupId.All(c => Uri.IsHexDigit(c)))
                    continue; // forged index line; skip
                string localPath = Path.Combine(_root, "manifests", backupId + ".json");
                if (File.Exists(localPath))
                    continue; // already on USB
                byte[]? encrypted = remote.GetManifest(backupId);
                if (encrypted is null)
                    continue;
                byte[] aad = Hashing.Sha256Bytes(System.Text.Encoding.UTF8.GetBytes(backupId));
                byte[] plaintext;
                try
                {
                    plaintext = ChunkCrypto.Decrypt(encrypted, _key, aad);
                }
                catch (CryptographicException ex)
                {
                    throw new InvalidDataException(
                        $"Remote manifest {backupId} failed authentication (wrong passphrase or tampered).", ex);
                }
                try
                {
                    string json = System.Text.Encoding.UTF8.GetString(plaintext);
                    var manifest = JsonSerializer.Deserialize(json, FileKeepJsonContext.Default.BackupManifest)
                        ?? throw new InvalidDataException($"Remote manifest {backupId} is corrupt.");
                    if (!manifest.BackupId.Equals(backupId, StringComparison.Ordinal))
                        throw new InvalidDataException(
                            $"Remote manifest backup ID mismatch (expected {backupId}).");
                    result.Add(new RemoteManifest(backupId, manifest));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        return result.OrderByDescending(m => m.Manifest.CreatedUtc).ToList();
    }

    /// <summary>
    /// Fails closed: decrypts (AES-GCM authentication) and checks
    /// SHA-256(plaintext) against the content-addressed chunk ID.
    /// </summary>
    private void VerifyDownloadedBlob(string chunkIdHex, byte[] blob)
    {
        // Fail closed on size before decryption allocates: a malicious
        // oversized blob must never reach ChunkCrypto.Decrypt's allocation.
        if (blob.Length > MaxChunkBlobBytes)
            throw new InvalidDataException(
                $"Downloaded chunk {chunkIdHex} is {blob.Length:N0} bytes, exceeding the " +
                $"maximum chunk blob size of {MaxChunkBlobBytes:N0} bytes; " +
                "rejecting as malicious or corrupt.");
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

    public void Restore(string backupId, string destDir) =>
        Restore(backupId, destDir, new RestoreOptions());

    /// <summary>
    /// Restores a backup with selective/merge options. The
    /// <see cref="RestoreOptions.MergePolicy"/> decides what happens when a
    /// file already exists in the destination: overwrite it, skip it when it
    /// differs, or keep whichever copy is newer.
    /// </summary>
    public RestoreResult Restore(string backupId, string destDir, RestoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        StorageGuard.EnsureRestoreDestNotInRepo(_root, destDir);
        var manifest = LoadManifest(backupId);
        if (manifest.IsDiskImage)
            throw new InvalidOperationException(
                $"Backup {backupId} is a disk image; use RestoreDiskImage to write it to a device, not file restore.");
        var errors = new List<string>();
        var result = new RestoreResult();
        RestoreOrVerify(manifest, destDir, verifyOnly: false, errors, options, result);
        OperationLog.Append(_root, "restore",
            $"id={backupId} dest={destDir} files={manifest.Files.Count} errors={errors.Count} " +
            $"policy={options.MergePolicy} restored={result.RestoredFiles} " +
            $"skippedIdentical={result.SkippedIdentical} skippedDiffering={result.SkippedDiffering.Count}");
        if (errors.Count > 0)
            throw new InvalidDataException("Restore failed:\n" + string.Join("\n", errors));
        return result;
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
        if (manifest.IsDiskImage)
        {
            // Disk images verify by checking chunk presence (skipping sparse
            // positions) and manifest integrity. Full content verification
            // requires a restore target.
            VerifyDiskImage(manifest, errors);
        }
        else
        {
            RestoreOrVerify(manifest, destDir: null, verifyOnly: true, errors, options: null, result: null);
        }
        OperationLog.Append(_root, "verify",
            $"id={backupId} files={manifest.Files.Count} errors={errors.Count} ok={errors.Count == 0}");
        return errors;
    }

    /// <summary>
    /// Verifies a disk-image backup: manifest integrity plus chunk presence.
    /// Sparse positions (empty chunk IDs) are skipped — they restore as zeros.
    /// </summary>
    private void VerifyDiskImage(BackupManifest manifest, List<string> errors)
    {
        if (manifest.Files.Count != 1)
        {
            errors.Add($"Disk image manifest has {manifest.Files.Count} files, expected 1.");
            return;
        }
        var entry = manifest.Files[0];
        foreach (string chunkId in entry.Chunks)
        {
            if (string.IsNullOrEmpty(chunkId))
                continue; // Sparse: skipped position, restores as zeros.
            try
            {
                VerifyChunk(chunkId);
            }
            catch (Exception ex)
            {
                errors.Add($"Chunk {chunkId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resolves a manifest-relative path against the restore destination and
    /// anchors it inside <paramref name="destDir"/>. Manifests may arrive from
    /// untrusted sources (imported from Usenet, tampered repos), so a bare
    /// <c>Path.Combine</c> is unsafe: traversal sequences ("../../x"),
    /// absolute paths, drive-relative paths ("C:foo") and UNC paths would all
    /// write outside the destination. The combined path is normalized with
    /// <see cref="Path.GetFullPath"/> and rejected with
    /// <see cref="InvalidDataException"/> unless it stays under the
    /// normalized destination root.
    /// <para>
    /// Lexical only: <see cref="Path.GetFullPath"/> does not resolve reparse
    /// points, so callers must also pass the result through
    /// <see cref="AssertNoReparsePoints"/> before writing.
    /// </para>
    /// </summary>
    private static string ResolveRestorePath(string destDir, string manifestPath)
    {
        string root = Path.GetFullPath(destDir);
        string combined = Path.Combine(
            destDir, manifestPath.Replace('/', Path.DirectorySeparatorChar));
        string resolved = Path.GetFullPath(combined);
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !resolved.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Manifest path '{manifestPath}' escapes the restore directory '{destDir}'.");
        return resolved;
    }

    /// <summary>
    /// Rejects a resolved restore path when any existing path component
    /// strictly below <paramref name="destDir"/> is a reparse point
    /// (symlink, junction, mount point). <see cref="ResolveRestorePath"/>
    /// is lexical, so a symlink already present in the destination tree —
    /// planted by an earlier restore of a hostile manifest, left behind by
    /// a previous run, or a pre-existing junction — would otherwise
    /// redirect directory creation, the temp-file write and the final
    /// rename outside the destination. The destination root itself is the
    /// caller's explicit choice and is not checked. Pass
    /// <paramref name="includeLeaf"/> for paths created as regular files
    /// or directories; symlink entries manage their own leaf
    /// (delete-then-recreate), so only their parent chain is checked.
    /// </summary>
    private static void AssertNoReparsePoints(string destDir, string resolvedPath, bool includeLeaf)
    {
        string root = Path.GetFullPath(destDir);
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        string end = includeLeaf
            ? resolvedPath
            : Path.GetDirectoryName(resolvedPath) ?? root;
        // resolvedPath passed through ResolveRestorePath, so it is at or
        // beneath root; the walk covers only components strictly below root.
        string relative = end.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? end.Substring(rootPrefix.Length)
            : string.Empty;
        string current = root;
        foreach (string part in relative.Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (IsReparsePoint(current))
                throw new InvalidDataException(
                    $"Refusing to restore through reparse point '{current}' under destination '{destDir}'.");
        }
    }

    /// <summary>
    /// True when a path exists as anything, including a dangling symlink
    /// (<see cref="File.Exists"/> reports those as missing, and
    /// <see cref="File.GetAttributes"/> follows the link and throws).
    /// </summary>
    private static bool LinkPathExists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        // Dangling symlink: attribute read fails, but the link itself exists.
        return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false; // Nonexistent components cannot be reparse points.
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when an existing file's SHA-256 matches the manifest entry.
    /// Used by merge mode to detect identical files without restoring.
    /// </summary>
    private static bool FileContentMatches(string path, string expectedSha256)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buf = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int n;
            while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                hash.AppendData(buf, 0, n);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return Convert.ToHexString(hash.GetHashAndReset())
            .Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreOrVerify(BackupManifest manifest, string? destDir, bool verifyOnly,
        List<string> errors, RestoreOptions? options, RestoreResult? result)
    {
        // Selective restore: narrow the manifest down to the entries the
        // include/exclude globs select. Verification always covers the
        // whole backup (options only apply to real restores).
        List<FileEntry> filesToRestore = manifest.Files;
        HashSet<string>? neededDirs = null;
        if (!verifyOnly && options is not null &&
            (options.Include.Count > 0 || options.Exclude.Count > 0))
        {
            filesToRestore = manifest.Files.Where(f => MatchesRestoreFilter(f.Path, options)).ToList();
            neededDirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in filesToRestore)
            {
                string parent = f.Path;
                int slash;
                while ((slash = parent.LastIndexOf('/')) > 0)
                {
                    parent = parent.Substring(0, slash);
                    neededDirs.Add(parent);
                }
            }
        }

        if (!verifyOnly && destDir is not null && manifest.Directories is not null)
        {
            foreach (string dir in manifest.Directories)
            {
                // In selective mode, create a directory only if it is
                // selected itself or an ancestor of a selected file.
                if (neededDirs is not null && options is not null &&
                    !MatchesRestoreFilter(dir, options) && !neededDirs.Contains(dir))
                    continue;
                try
                {
                    string dirPath = ResolveRestorePath(destDir, dir);
                    AssertNoReparsePoints(destDir, dirPath, includeLeaf: true);
                    Directory.CreateDirectory(dirPath);
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
            // Two-pass restore: regular files first, symlinks last. A symlink
            // created early could redirect a later file write outside destDir
            // (lexical path validation doesn't resolve symlinks).
            foreach (var entry in filesToRestore)
            {
                if (entry.SymlinkTarget is not null)
                    continue;
                try
                {
                    RestoreOneFile(entry, manifest, destDir, verifyOnly, buffer, options, result);
                }
                catch (Exception ex)
                {
                    errors.Add($"{entry.Path}: {ex.Message}");
                }
            }
            foreach (var entry in filesToRestore)
            {
                if (entry.SymlinkTarget is null)
                    continue;
                try
                {
                    RestoreOneFile(entry, manifest, destDir, verifyOnly, buffer, options, result);
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

    /// <summary>
    /// True when a manifest-relative path ('/' separators) passes the
    /// include/exclude globs. Empty includes select everything; excludes
    /// win over includes. Case-insensitive on Windows, case-sensitive
    /// elsewhere. Gitignore-style: a pattern without a '/' matches the
    /// file name at any depth ('*.log'), and a bare directory name
    /// implies its whole subtree ('docs' == 'docs/**').
    /// </summary>
    private static bool MatchesRestoreFilter(string manifestPath, RestoreOptions options)
    {
        bool ignoreCase = OperatingSystem.IsWindows();
        bool included = options.Include.Count == 0 ||
            options.Include.Any(p => GlobMatch(manifestPath, p, ignoreCase));
        if (!included)
            return false;
        return !options.Exclude.Any(p => GlobMatch(manifestPath, p, ignoreCase));
    }

    private static bool GlobMatch(string manifestPath, string pattern, bool ignoreCase)
    {
        if (!pattern.Contains('/'))
        {
            string fileName = manifestPath.Substring(manifestPath.LastIndexOf('/') + 1);
            if (GlobMatcher.IsMatch(fileName, pattern, ignoreCase))
                return true;
            // Bare directory name implies its subtree ('docs' == 'docs/**').
            if (!pattern.Contains('*') && !pattern.Contains('?'))
                return GlobMatcher.IsMatch(manifestPath, pattern + "/**", ignoreCase);
            return false;
        }
        return GlobMatcher.IsMatch(manifestPath, pattern, ignoreCase);
    }

    private void RestoreOneFile(FileEntry entry, BackupManifest manifest, string? destDir,
        bool verifyOnly, byte[] buffer, RestoreOptions? options, RestoreResult? result)
    {
        if (entry.SymlinkTarget is not null)
        {
            if (!verifyOnly && destDir is not null)
            {
                string linkPath = ResolveRestorePath(destDir, entry.Path);
                AssertNoReparsePoints(destDir, linkPath, includeLeaf: false);
                var policy = options?.MergePolicy ?? MergePolicy.Overwrite;
                if (result is not null && policy != MergePolicy.Overwrite && LinkPathExists(linkPath))
                {
                    // SkipDiffering and KeepNewer never touch an existing
                    // path (links have no meaningful "newer", so KeepNewer
                    // treats them like SkipDiffering). An identical link is
                    // skipped silently; anything else is reported.
                    string? currentTarget =
                        File.ResolveLinkTarget(linkPath, returnFinalTarget: false)?.ToString();
                    if (string.Equals(currentTarget, entry.SymlinkTarget, StringComparison.Ordinal))
                        result.SkippedIdentical++;
                    else
                        result.SkippedDiffering.Add(entry.Path);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                if (File.Exists(linkPath) || Directory.Exists(linkPath))
                    File.Delete(linkPath);
                File.CreateSymbolicLink(linkPath, entry.SymlinkTarget);
                if (result is not null)
                    result.RestoredFiles++;
            }
            return;
        }

        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Stream? outStream = null;
        string? outPath = null;
        string? tempPath = null;
        try
        {
            if (!verifyOnly && destDir is not null)
            {
                outPath = ResolveRestorePath(destDir, entry.Path);
                AssertNoReparsePoints(destDir, outPath, includeLeaf: true);
                var policy = options?.MergePolicy ?? MergePolicy.Overwrite;
                if (result is not null && policy != MergePolicy.Overwrite && File.Exists(outPath))
                {
                    // The reparse-point check above already rejected links,
                    // so this is a real file.
                    if (policy == MergePolicy.SkipDiffering)
                    {
                        if (FileContentMatches(outPath, entry.Sha256))
                            result.SkippedIdentical++;
                        else
                            result.SkippedDiffering.Add(entry.Path);
                        return;
                    }
                    // KeepNewer: newer copy wins; ties keep the destination.
                    // A kept destination file differs from the backup, so it
                    // is reported alongside skipped-differing files.
                    if (File.GetLastWriteTimeUtc(outPath) >= entry.MtimeUtc)
                    {
                        result.SkippedDiffering.Add(entry.Path);
                        return;
                    }
                    // Backup is newer: fall through and overwrite.
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                // Write to temp file first; rename to final path only after
                // hash verification succeeds. This is atomic on the same
                // filesystem and eliminates any window where a partial file
                // is visible at the destination path.
                tempPath = outPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
                outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
            }

            foreach (string chunkId in entry.Chunks)
            {
                byte[] plaintext = ReadChunkPlaintext(chunkId);
                fileHash.AppendData(plaintext);
                outStream?.Write(plaintext, 0, plaintext.Length);
            }

            string actual = Convert.ToHexString(fileHash.GetHashAndReset());
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"File hash mismatch (manifest {entry.Sha256}, actual {actual}).");

            // Hash verified — atomically move temp file to final path
            if (tempPath is not null && outPath is not null)
            {
                outStream?.Dispose();
                outStream = null;
                File.Move(tempPath, outPath, overwrite: true);
                tempPath = null; // Moved successfully, don't delete
                if (result is not null)
                    result.RestoredFiles++;
            }
        }
        catch
        {
            // Clean up temp file on failure; final path is never touched
            // unless the hash verified successfully.
            outStream?.Dispose();
            outStream = null;
            if (tempPath is not null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best effort */ }
            }
            throw;
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
