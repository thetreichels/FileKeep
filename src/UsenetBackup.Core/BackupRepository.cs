using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace UsenetBackup.Core;

/// <summary>
/// Milestone 1: local repository engine.
/// Init → BackupDirectory → manifest; Restore / Verify from manifest.
/// </summary>
public sealed class BackupRepository : IDisposable
{
    public const string FormatVersion = "v1";
    public const int DefaultChunkSize = 4 * 1024 * 1024;
    public const int DefaultKdfIterations = 600_000;

    private readonly string _root;
    private readonly RepositoryConfig _config;
    private readonly byte[] _key;
    private readonly ChunkStore _chunks;
    private readonly Catalog _catalog;
    private bool _disposed;

    private BackupRepository(string root, RepositoryConfig config, byte[] key)
    {
        _root = root;
        _config = config;
        _key = key;
        _chunks = new ChunkStore(root, key);
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
        return new BackupRepository(Path.GetFullPath(repoPath), config, key);
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

    public BackupManifest BackupDirectory(string sourceDir)
    {
        string fullSource = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(fullSource))
            throw new DirectoryNotFoundException($"Source directory not found: {fullSource}");

        var manifest = new BackupManifest
        {
            BackupId = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            Source = fullSource,
            ChunkSize = _config.ChunkSize,
        };

        string[] files = Directory
            .EnumerateFiles(fullSource, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(fullSource, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.ChunkSize);
        try
        {
            foreach (string rel in files)
            {
                string full = Path.Combine(fullSource, rel.Replace('/', Path.DirectorySeparatorChar));
                manifest.Files.Add(BackupOneFile(full, rel, buffer));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        manifest.RootSha256 = manifest.ComputeRootHash();
        if (!manifest.VerifyRootHash())
            throw new InvalidOperationException("Internal error: manifest root hash did not verify.");

        string manifestPath = Path.Combine(_root, "manifests", manifest.BackupId + ".json");
        File.WriteAllText(manifestPath, manifest.ToJson());
        _catalog.RecordBackup(manifest.BackupId, manifest.Type, manifest.Source, manifest.CreatedUtc);
        return manifest;
    }

    private FileEntry BackupOneFile(string fullPath, string relPath, byte[] buffer)
    {
        var entry = new FileEntry
        {
            Path = relPath,
            MtimeUtc = File.GetLastWriteTimeUtc(fullPath),
        };

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            entry.SymlinkTarget = File.ResolveLinkTarget(fullPath, returnFinalTarget: false)?.ToString();
            entry.Size = 0;
            entry.Sha256 = Hashing.Sha256Hex(ReadOnlySpan<byte>.Empty);
            return entry;
        }

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        entry.Size = stream.Length;
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        int read;
        while ((read = stream.Read(buffer, 0, _config.ChunkSize)) > 0)
        {
            var span = buffer.AsSpan(0, read);
            string chunkId = _chunks.Write(span);
            _catalog.RecordChunk(chunkId, read);
            entry.Chunks.Add(chunkId);
            fileHash.AppendData(span);
        }
        entry.Sha256 = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
        return entry;
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

    public long StoredChunkCount() => _chunks.StoredChunkCount();

    public void Restore(string backupId, string destDir)
    {
        var manifest = LoadManifest(backupId);
        var errors = new List<string>();
        RestoreOrVerify(manifest, destDir, verifyOnly: false, errors);
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
            return new List<string> { ex.Message };
        }
        RestoreOrVerify(manifest, destDir: null, verifyOnly: true, errors);
        return errors;
    }

    private void RestoreOrVerify(BackupManifest manifest, string? destDir, bool verifyOnly, List<string> errors)
    {
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
                byte[] plaintext = _chunks.Read(chunkId); // decrypts, authenticates, re-hashes
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
