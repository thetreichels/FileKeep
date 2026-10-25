using FileKeep.Core.Redundancy;
using FileKeep.Core.Service;

namespace FileKeep.Core.Nntp;

/// <summary>
/// Uploads one staging batch at a time to a single NNTP provider.
/// Owns its connection pool and blob store for the whole backup so batches
/// reuse connections. Each batch is an independent post set (own NFO,
/// volumes, and parity), so a failed batch can be retried without redoing
/// earlier batches, and each batch's parity groups stay aligned for
/// reconstruction (grouped per post set on the download side).
/// </summary>
public sealed class NntpBatchUploader : IDisposable
{
    private const int MaxBatchAttempts = 3;

    private readonly BackupRepository _repo;
    private readonly string _backupId;
    private readonly NntpConfig _nntp;
    private readonly string? _password;
    private readonly string _redundancy;
    private readonly int _parityGroupSize;
    private readonly Action<string> _log;
    private readonly NntpConnectionPool _pool;
    private readonly NntpBlobStore _store;
    private readonly ProviderLimits _limits;
    private bool _disposed;
    // P2-V2a/V2b: the parity groups generated and the per-batch NFO
    // dates used across all batches, for the caller to stamp into the
    // manifest (retention cannot reconstruct either after the fact).
    private readonly List<List<string>> _parityGroups = new();
    private readonly List<string?> _nfoDates = new();

    public NntpBatchUploader(
        BackupRepository repo,
        string backupId,
        NntpConfig nntp,
        string? password,
        string redundancy,
        int parityGroupSize,
        Action<string> log)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _backupId = backupId ?? throw new ArgumentNullException(nameof(backupId));
        _nntp = nntp ?? throw new ArgumentNullException(nameof(nntp));
        _password = password;
        _redundancy = redundancy ?? "none";
        _parityGroupSize = parityGroupSize;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _limits = new ProviderLimits(repo.RepoRoot);

        _pool = new NntpConnectionPool(
            nntp.Host, nntp.Port, nntp.Ssl,
            nntp.Username, password, nntp.Connections);
        _store = new NntpBlobStore(_pool, nntp.Newsgroup, repo.RepoId, repo.CatalogPath,
            messageIndex: repo.MessageIndex,
            providerKey: ChunkMessageIndex.MakeProviderKey(nntp.Host, nntp.Newsgroup),
            from: repo.GetOrCreatePoster(), indexAuth: repo.IndexAuth);
        // Client-side article guard must match the provider's real limit,
        // not the 64 MB default: probed ceiling when known, else enough
        // headroom for the repo's configured volume size. (Previously the
        // scheduler never set this, so 100 MB volumes were rejected
        // client-side even when the provider accepted them.)
        long? probed = _limits.GetMaxArticleBytes(nntp.Host, nntp.Port);
        _store.MaxArticleBytes = probed.HasValue && probed.Value > 0
            ? probed.Value
            : Math.Max(repo.VolumeSizeBytes * 2, NntpBlobStore.DefaultMaxArticleBytes);
    }

    /// <summary>Provider key ("host/newsgroup") for manifest records.</summary>
    public string ProviderKey =>
        ChunkMessageIndex.MakeProviderKey(_nntp.Host, _nntp.Newsgroup);

    /// <summary>
    /// P2-V2a: the parity record for this uploader's uploads — the
    /// redundancy mode and group size used, plus every parity group
    /// generated across all batches — for the caller to stamp into the
    /// manifest before the manifest article is posted.
    /// </summary>
    public ManifestParityRecord ParityRecord => new()
    {
        Mode = _redundancy,
        GroupSize = _parityGroupSize,
        Groups = _parityGroups,
    };

    /// <summary>
    /// P2-V2b: the posting date each batch's NFO was built with, by
    /// batch index; null when no volume batch posted an NFO.
    /// </summary>
    public List<string?>? NfoDates =>
        _nfoDates.Any(d => d is not null) ? _nfoDates : null;

    private void RecordParityGroups(IReadOnlyList<string> slice)
    {
        if (_redundancy == "xor")
            _parityGroups.AddRange(XorParity.GroupsFor(slice, _parityGroupSize));
        else if (_redundancy == "par2")
            _parityGroups.AddRange(Par2Redundancy.GroupsFor(slice));
    }

    private void RecordNfoDate(int batchIndex, string date)
    {
        while (_nfoDates.Count <= batchIndex)
            _nfoDates.Add(null);
        _nfoDates[batchIndex] = date;
    }

    /// <summary>
    /// Provider host (for batch-size resolution and logging).
    /// </summary>
    public string Host => _nntp.Host;

    /// <summary>
    /// Provider port (for batch-size resolution).
    /// </summary>
    public int Port => _nntp.Port;

    /// <summary>
    /// Batch size for this provider: 90% of the probed article ceiling when
    /// known, else the repo's default volume size (100 MB).
    /// </summary>
    public long BatchSizeBytes =>
        BatchStaging.ResolveBatchSizeBytes(_limits, _nntp.Host, _nntp.Port,
            BackupRepository.DefaultVolumeSizeBytes);

    /// <summary>
    /// Uploads one batch of newly-staged chunks. In volume mode the batch's
    /// chunks are packed into volumes (repo's configured volume size) and
    /// posted as one set with per-batch parity; in chunk mode chunks are
    /// posted individually with per-batch parity. Returns the volume entries
    /// for the manifest (with per-batch set names). Retries the batch up to
    /// <see cref="MaxBatchAttempts"/> times; earlier batches are untouched.
    /// </summary>
    public List<VolumeEntry> UploadBatch(
        IReadOnlyList<string> newChunkIds, int batchIndex, bool useVolumes)
    {
        ArgumentNullException.ThrowIfNull(newChunkIds);
        if (batchIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(batchIndex));
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Canonical order (ChunkOrdering) so parity groups are deterministic.
        string[] ordered = ChunkOrdering.GetOrderedChunkIds(newChunkIds);
        if (ordered.Length == 0)
            return new List<VolumeEntry>();

        Exception? lastError = null;
        for (int attempt = 1; attempt <= MaxBatchAttempts; attempt++)
        {
            try
            {
                return useVolumes
                    ? UploadVolumeBatch(ordered, batchIndex)
                    : UploadChunkBatch(ordered, batchIndex);
            }
            catch (Exception ex) when (attempt < MaxBatchAttempts)
            {
                lastError = ex;
                _log($"batch {batchIndex}: attempt {attempt} failed ({ex.Message}); retrying...");
                Thread.Sleep(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }
        throw new InvalidOperationException(
            $"Batch {batchIndex} failed after {MaxBatchAttempts} attempts: {lastError?.Message}",
            lastError);
    }

    /// <summary>
    /// Computes a batch's manifest volume entries WITHOUT posting
    /// (MU-18): the packing is deterministic (canonical chunk order,
    /// the repo's volume size), so the entries describe the batch
    /// identically whether or not an NNTP upload produced them. The
    /// batched scheduler uses this when a batch was delivered only to
    /// file destinations (SMB/USB) — the chunks are pruned all the
    /// same, and a manifest whose Volumes list omits them misleads
    /// every consumer that trusts the packing.
    /// </summary>
    public static List<VolumeEntry> PlanVolumeEntries(
        BackupRepository repo, string backupId,
        IReadOnlyList<string> chunkIds, int batchIndex)
    {
        string[] ordered = ChunkOrdering.GetOrderedChunkIds(chunkIds);
        if (ordered.Length == 0)
            return new List<VolumeEntry>();
        var groups = VolumePacker.Plan(ordered, repo.GetChunkBlobSize, repo.VolumeSizeBytes)
            .ToList();
        string setName = ArticleCodec.SetName(backupId, batchIndex);
        var entries = new List<VolumeEntry>(groups.Count);
        foreach (var group in groups)
        {
            var volume = VolumePacker.BuildVolume(group, repo.GetChunkBlob);
            entries.Add(new VolumeEntry
            {
                Id = volume.Id,
                ChunkIds = volume.ChunkIds.ToList(),
                SizeBytes = volume.Bytes.Length,
                SetName = setName,
            });
        }
        return entries;
    }

    private List<VolumeEntry> UploadVolumeBatch(string[] orderedChunkIds, int batchIndex)
    {
        // Plan volumes for this batch's chunks only (all staged locally).
        var groups = VolumePacker.Plan(orderedChunkIds, _repo.GetChunkBlobSize, _repo.VolumeSizeBytes)
            .ToList();
        if (groups.Count == 0)
            return new List<VolumeEntry>();

        // Preflight: prove the provider accepts this batch's largest article.
        // Uses the cached probe when known; otherwise posts one probe article.
        PreflightBatch(groups);

        string setName = ArticleCodec.SetName(_backupId, batchIndex);
        int totalParts = groups.Count + 1;
        string nfoDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string nfoText = ArticleCodec.BuildNfoText(
            _backupId,
            nfoDate,
            groups.Count,
            _repo.GetOrCreatePoster());
        _store.PostNfo(_backupId, batchIndex, nfoText, setName, totalParts);
        RecordNfoDate(batchIndex, nfoDate);
        _log($"batch {batchIndex}: posting {groups.Count} volumes to {_nntp.Host}...");

        var volumeEntries = new List<(int Index, VolumeEntry Entry)>();
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _nntp.Connections };
        int uploaded = 0, skipped = 0;
        Parallel.For(0, groups.Count, parallelOptions, i =>
        {
            var group = groups[i];
            var volume = VolumePacker.BuildVolume(group, _repo.GetChunkBlob);
            lock (volumeEntries)
                volumeEntries.Add((i, new VolumeEntry
                {
                    Id = volume.Id,
                    ChunkIds = volume.ChunkIds.ToList(),
                    SizeBytes = volume.Bytes.Length,
                    SetName = setName,
                }));
            if (_store.Exists(volume.Id))
            {
                Interlocked.Increment(ref skipped);
            }
            else
            {
                _store.PutVolume(volume.Id, volume.Bytes, setName, i + 2, totalParts);
                Interlocked.Increment(ref uploaded);
            }
        });
        _log($"batch {batchIndex}: {uploaded} volumes posted" +
            (skipped > 0 ? $" ({skipped} already present)" : ""));

        // Per-batch parity over this batch's sorted volume IDs. The download
        // side groups parity by post set, so these stay aligned.
        UploadVolumeParity(volumeEntries, batchIndex);

        return volumeEntries.OrderBy(x => x.Index).Select(x => x.Entry).ToList();
    }

    private void UploadVolumeParity(
        List<(int Index, VolumeEntry Entry)> volumeEntries, int batchIndex)
    {
        int dataUnits = volumeEntries.Count;
        int parityGroups = dataUnits / 10 + (dataUnits % 10 >= 2 ? 1 : 0);
        int parityUnits = _redundancy == "xor" ? parityGroups
            : _redundancy == "par2" ? parityGroups * 3 : 0;
        if (parityUnits == 0)
            return;

        var chunkMap = volumeEntries.ToDictionary(
            e => e.Entry.Id, e => (IReadOnlyList<string>)e.Entry.ChunkIds);
        string[] sortedVolIds = chunkMap.Keys
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        byte[] GetVolumeBytes(string volId) =>
            VolumePacker.BuildVolume(chunkMap[volId], _repo.GetChunkBlob).Bytes;

        string parityLabel = _redundancy == "xor" ? "XOR" : "PAR2";
        int parityUploaded = 0, paritySkipped = 0;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _nntp.Connections };
        for (int g = 0; g < sortedVolIds.Length; g += 10)
        {
            string[] group = sortedVolIds.Skip(g).Take(10).ToArray();
            if (group.Length < 2)
                continue;
            RecordParityGroups(group);
            IReadOnlyDictionary<string, byte[]> blocks =
                _redundancy == "xor"
                    ? XorParity.GenerateParity(group, GetVolumeBytes, null, _parityGroupSize)
                    : Par2Redundancy.GenerateParity(group, GetVolumeBytes);
            Parallel.ForEach(blocks, parallelOptions, kvp =>
            {
                if (!_store.Exists(kvp.Key))
                {
                    _store.PutVolume(kvp.Key, kvp.Value);
                    Interlocked.Increment(ref parityUploaded);
                }
                else
                {
                    Interlocked.Increment(ref paritySkipped);
                }
            });
        }
        _log($"batch {batchIndex}: {parityUploaded} {parityLabel} parity blocks posted" +
            (paritySkipped > 0 ? $" ({paritySkipped} already present)" : ""));
    }

    private List<VolumeEntry> UploadChunkBatch(string[] orderedChunkIds, int batchIndex)
    {
        _log($"batch {batchIndex}: posting {orderedChunkIds.Length} chunks to {_nntp.Host}...");
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _nntp.Connections };
        int uploaded = 0, skipped = 0;
        Parallel.ForEach(orderedChunkIds, parallelOptions, chunkId =>
        {
            if (_store.Exists(chunkId))
            {
                Interlocked.Increment(ref skipped);
            }
            else
            {
                byte[] blob = _repo.GetChunkBlob(chunkId);
                _store.Put(chunkId, blob);
                Interlocked.Increment(ref uploaded);
            }
        });

        // Per-batch parity over the batch's chunks.
        int parityGroups = orderedChunkIds.Length / 10 + (orderedChunkIds.Length % 10 >= 2 ? 1 : 0);
        if ((_redundancy == "xor" || _redundancy == "par2") && parityGroups > 0)
        {
            string parityLabel = _redundancy == "xor" ? "XOR" : "PAR2";
            int parityUploaded = 0, paritySkipped = 0;
            for (int g = 0; g < orderedChunkIds.Length; g += 10)
            {
                string[] group = orderedChunkIds.Skip(g).Take(10).ToArray();
                if (group.Length < 2)
                    continue;
                RecordParityGroups(group);
                IReadOnlyDictionary<string, byte[]> blocks =
                    _redundancy == "xor"
                        ? XorParity.GenerateParity(group, id => _repo.GetChunkBlob(id), null, _parityGroupSize)
                        : Par2Redundancy.GenerateParity(group, id => _repo.GetChunkBlob(id));
                Parallel.ForEach(blocks, parallelOptions, kvp =>
                {
                    if (!_store.Exists(kvp.Key))
                    {
                        _store.Put(kvp.Key, kvp.Value);
                        Interlocked.Increment(ref parityUploaded);
                    }
                    else
                    {
                        Interlocked.Increment(ref paritySkipped);
                    }
                });
            }
            _log($"batch {batchIndex}: {parityUploaded} {parityLabel} parity blocks posted" +
                (paritySkipped > 0 ? $" ({paritySkipped} already present)" : ""));
        }
        _log($"batch {batchIndex}: {uploaded} chunks posted" +
            (skipped > 0 ? $" ({skipped} already present)" : ""));
        // Chunk mode: no volume entries for the manifest.
        return new List<VolumeEntry>();
    }

    /// <summary>
    /// Preflight for one batch: builds the batch's largest volume, measures
    /// the exact posted article size, and proves the provider accepts it.
    /// Uses the cached per-provider limit when known; otherwise posts a
    /// single probe article. Throws with a clear message when rejected.
    /// </summary>
    private void PreflightBatch(List<IReadOnlyList<string>> groups)
    {
        long maxBlobBytes = 0;
        IReadOnlyList<string>? largest = null;
        foreach (var group in groups)
        {
            long total = 0;
            foreach (string id in group)
                total += _repo.GetChunkBlobSize(id);
            if (total > maxBlobBytes)
            {
                maxBlobBytes = total;
                largest = group;
            }
        }
        if (largest is null)
            return;
        var volume = VolumePacker.BuildVolume(largest, _repo.GetChunkBlob);
        string poster = _repo.GetOrCreatePoster();
        string article = ArticleCodec.BuildVolumeArticle(
            volume.Id, _repo.RepoId, volume.Bytes, _nntp.Newsgroup, poster, "00000000", 2, 2);
        long articleBytes = article.Length; // yEnc body is ASCII: chars == bytes

        long? cached = _limits.GetMaxArticleBytes(_nntp.Host, _nntp.Port);
        if (cached.HasValue && cached.Value >= articleBytes)
        {
            _log($"batch preflight: largest article {articleBytes} bytes within " +
                $"known {_nntp.Host}:{_nntp.Port} limit ({cached.Value} bytes).");
            return;
        }
        _log($"batch preflight: probing {_nntp.Host}:{_nntp.Port} with a {articleBytes}-byte article...");
        _limits.EnsureCapacity(_nntp.Host, _nntp.Port, articleBytes, size =>
        {
            byte[] payload = new byte[size];
            Random.Shared.NextBytes(payload);
            string probeArticle = ArticleCodec.BuildProbeArticle(
                payload, _nntp.Newsgroup, poster, out _);
            try
            {
                using var client = new NntpClient(_nntp.Host, _nntp.Port, _nntp.Ssl);
                client.Connect();
                if (!string.IsNullOrEmpty(_nntp.Username))
                    client.Authenticate(_nntp.Username, _password ?? "");
                client.Post(probeArticle);
                client.Quit();
                return true;
            }
            catch
            {
                return false;
            }
        });
        _log("batch preflight OK — provider accepts the volume size.");
        // Refresh the client-side guard from the fresh probe.
        long? fresh = _limits.GetMaxArticleBytes(_nntp.Host, _nntp.Port);
        if (fresh.HasValue && fresh.Value > 0)
            _store.MaxArticleBytes = fresh.Value;
    }

    /// <summary>
    /// Posts the backup manifest + manifest index after all batches, and
    /// records the upload for expiration monitoring.
    /// </summary>
    public int UploadManifest()
    {
        int articles = _repo.UploadManifest(_backupId, _store);
        var tracker = new UsenetUploadTracker(_repo.RepoRoot);
        tracker.RecordUpload(_backupId, _nntp.Host, _nntp.Newsgroup);
        return articles;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _store.Dispose();
        _pool.Dispose();
    }
}
