namespace FileKeep.Core.Service;

/// <summary>
/// Guarded store for <see cref="ServiceConfig"/> (config-lifecycle
/// split, Stage 3): owns the threading contract — the lock object,
/// the snapshot copies, and the effective-provider view.
/// <see cref="ServiceConfig"/> keeps its public surface and delegates
/// to this store, so every existing consumer and lock site keeps
/// working unchanged (the facade's <c>JobsLock</c> returns this
/// store's lock object — the same object, not a new one).
/// The store reads the config's lists through its back-reference on
/// every call, never caching a list: dashboard handlers replace whole
/// lists as well as mutating them, and a cached list would go stale.
/// </summary>
public sealed class ServiceConfigStore
{
    private readonly ServiceConfig _config;

    public ServiceConfigStore(ServiceConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Guards concurrent access to ALL mutable config state — Jobs,
    /// Locations, NntpProviders, the top-level Nntp, and the protection
    /// fields (P2-C1/C2: Jobs were guarded; everything else was mutated
    /// unlocked while handlers and the scheduler read it lock-free,
    /// which tore saves and handed running jobs half-edited locations).
    /// Dashboard HTTP handlers and the scheduler run on different
    /// threads; every mutation — and every enumeration that isn't a
    /// Snapshot* copy — must hold this lock. (Named JobsLock on the
    /// facade for historical reasons; the contract covers the whole
    /// config.)
    /// </summary>
    public object Lock { get; } = new();

    /// <summary>
    /// Returns a snapshot copy of the Jobs list taken under <see cref="Lock"/>.
    /// Use this for all read-only iterations outside the lock.
    /// </summary>
    public List<BackupJobConfig> SnapshotJobs()
    {
        lock (Lock)
            return _config.Jobs.ToList();
    }

    /// <summary>
    /// Returns a snapshot copy of the Locations list taken under
    /// <see cref="Lock"/>. Use this for all read-only iterations
    /// outside the lock.
    /// </summary>
    public List<BackupLocation> SnapshotLocations()
    {
        lock (Lock)
            return _config.Locations.ToList();
    }

    /// <summary>
    /// Returns a snapshot copy of the NntpProviders list taken under
    /// <see cref="Lock"/>.
    /// </summary>
    public List<NntpConfig> SnapshotProviders()
    {
        lock (Lock)
            return _config.NntpProviders.ToList();
    }

    /// <summary>
    /// Gets the effective list of global Usenet providers: NntpProviders
    /// if non-empty, otherwise the single Nntp config as a singleton list.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<NntpConfig> EffectiveProviders
    {
        get
        {
            // Snapshot copy under the lock (P2-C2): returning the live
            // list let the scheduler's provider list alias a list the
            // dashboard could replace or mutate mid-run.
            lock (Lock)
                return _config.NntpProviders.Count > 0 ? _config.NntpProviders.ToList() :
                    _config.Nntp is not null ? new[] { _config.Nntp } :
                    Array.Empty<NntpConfig>();
        }
    }
}
