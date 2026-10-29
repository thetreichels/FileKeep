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

    // ---- Candidate-apply (Config lock-site migration) ----
    //
    // Pattern, generalized from ServiceConfigBuilder (Stage 5): the
    // handler builds and validates its candidate change outside any
    // lock, then one of these Apply methods swaps it into the live
    // config under Lock, in microseconds. The handler then Saves and
    // reloads exactly as before, outside the lock. That matches the
    // Config sites today (all of them Save outside the lock except
    // UpdateNntp — see ApplyNntp below).
    //
    // The hard constraint is today's atomicity, which limits pure
    // candidate-apply: wherever today's lock block reads live state
    // to decide what to write, that read + write move INTO the Apply
    // method under lock, not out to a snapshot check. The documented
    // exceptions are ApplyLocation (blob lookup + DPAPI + disguise
    // check), ApplyAttachLocation (both existence checks),
    // ApplyDeleteLocation (strip + remove), and ApplyNntp (mutate +
    // validate + Usenet sync). Those are typed store operations by
    // necessity, not candidate swaps — the alternative (snapshot
    // checks outside the lock) would introduce races that did not
    // exist today. DPAPI Protect inside ApplyLocation/ApplyNntp is
    // short CPU work, not a hardware wait, so it does not break the
    // no-lock-around-hardware-waits rule. No Apply method Saves,
    // reloads, or does file IO (ApplyNntp validates, as today).

    /// <summary>
    /// Swaps a job into the live list by name under the lock: same
    /// name replaces the object (the scheduler must see a consistent
    /// snapshot, not a half-mutated object), new name appends. This
    /// is the UpsertJob apply path; validation (Builder) and the
    /// create-target rule run in the handler before this, as before.
    /// The job object passed in is the handler's request object, never
    /// a live object, so no clone is needed.
    /// </summary>
    public void ApplyJob(BackupJobConfig job)
    {
        lock (Lock)
        {
            var existing = _config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                _config.Jobs.Add(job);
            else
                _config.Jobs[_config.Jobs.IndexOf(existing)] = job;
        }
    }

    /// <summary>
    /// Removes a job by name under the lock. Returns false when the
    /// job does not exist, so the handler can return 400 with no Save,
    /// exactly as the old lock block did.
    /// </summary>
    public bool ApplyDeleteJob(string name)
    {
        lock (Lock)
        {
            var existing = _config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return false;
            _config.Jobs.Remove(existing);
            return true;
        }
    }

    /// <summary>
    /// Deep-copies a job's list fields. CloneWithTargets copies
    /// Targets but shares Sources (MemberwiseClone), and it would
    /// normalize Targets via EffectiveTargets if used carelessly —
    /// SetJobPaused must preserve the raw Targets list exactly, so
    /// both lists are copied explicitly here. List elements are
    /// strings (immutable), so a list copy is a deep copy.
    /// </summary>
    private static BackupJobConfig CloneJob(BackupJobConfig job)
    {
        var clone = job.CloneWithTargets(job.Targets?.ToList() ?? new List<string>());
        if (job.Targets is null)
            clone.Targets = null;
        clone.Sources = job.Sources?.ToList() ?? new List<string>();
        return clone;
    }

    /// <summary>
    /// Sets a job's stored user pause under the lock, by swapping in
    /// a clone with the new value — never by mutating the live job
    /// object before commit. Returns false when the job does not
    /// exist (no Save, 400 as before). A no-op (value already equal)
    /// swaps nothing, preserving object identity as before.
    /// </summary>
    public bool ApplySetJobPaused(string name, bool paused)
    {
        lock (Lock)
        {
            var job = _config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (job is null)
                return false;
            if (job.Paused == paused)
                return true;
            var clone = CloneJob(job);
            clone.Paused = paused;
            _config.Jobs[_config.Jobs.IndexOf(job)] = clone;
            return true;
        }
    }

    /// <summary>Result of <see cref="ApplyAttachLocation"/>.</summary>
    public enum AttachLocationOutcome
    {
        Attached,
        UnknownLocation,
        UnknownJob,
    }

    /// <summary>
    /// Attaches a location to a job's targets under the lock. Both
    /// existence checks run inside the lock, in today's order
    /// (location first, then job), so a location deleted between a
    /// snapshot check and the apply cannot leave a job targeting a
    /// non-existent location. The job is swapped for a clone with
    /// the new target list (targets normalized exactly as today,
    /// via EffectiveTargets); an already-present target swaps
    /// nothing. Returns which check failed, so the handler's 400
    /// messages are unchanged.
    /// </summary>
    public AttachLocationOutcome ApplyAttachLocation(string jobName, string locationName)
    {
        lock (Lock)
        {
            if (!_config.Locations.Any(l =>
                    l.Name.Equals(locationName, StringComparison.OrdinalIgnoreCase)))
                return AttachLocationOutcome.UnknownLocation;
            var job = _config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job is null)
                return AttachLocationOutcome.UnknownJob;
            var targets = job.EffectiveTargets.ToList();
            if (!targets.Any(t => t.Equals(locationName, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Add(locationName);
                var clone = CloneJob(job);
                clone.Targets = targets;
                _config.Jobs[_config.Jobs.IndexOf(job)] = clone;
            }
            return AttachLocationOutcome.Attached;
        }
    }

    /// <summary>
    /// Deletes a location under the lock, stripping it from every
    /// job's targets in the same lock hold, exactly as today. Whole
    /// Jobs/Locations lists are NOT replaced from a snapshot here —
    /// a job added between a snapshot and the apply would be lost.
    /// Returns whether the location existed and, in job-list order,
    /// the names of the jobs it was stripped from (the handler's log
    /// text depends on that list).
    /// </summary>
    public (bool Found, List<string> StrippedFrom) ApplyDeleteLocation(string name)
    {
        lock (Lock)
        {
            var existing = _config.Locations.FirstOrDefault(l =>
                l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return (false, new List<string>());
            var strippedFrom = new List<string>();
            foreach (var job in _config.Jobs)
            {
                var targets = job.EffectiveTargets
                    .Where(t => !t.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (targets.Count != job.EffectiveTargets.Count)
                {
                    job.Targets = targets;
                    strippedFrom.Add(job.Name);
                }
            }
            _config.Locations.Remove(existing);
            return (true, strippedFrom);
        }
    }

    /// <summary>Result of <see cref="ApplyLocation"/>.</summary>
    public sealed record ApplyLocationOutcome(bool IsCreate, string? DisguiseError);

    /// <summary>
    /// Adds or updates a location under the lock, exactly as the old
    /// UpsertLocation lock block did: existing-blob lookup, blank
    /// keeps the blob, non-blank plaintext is DPAPI-protected, and
    /// plaintext fields are cleared — all inside the lock, so a
    /// concurrent upsert of the same location cannot interleave and
    /// lose the new blob. For an existing location the disguise
    /// check runs before any live field is copied (a disguise error
    /// leaves the live location untouched, as before), and the field
    /// copy is field-by-field into the existing object, in today's
    /// order and with today's field list (RedundancyMode is not
    /// copied by UpsertLocation today, and is not copied here).
    /// Returns whether this was a create (the handler's auto-scan
    /// condition) and any disguise error (no Save on error).
    /// </summary>
    public ApplyLocationOutcome ApplyLocation(BackupLocation loc)
    {
        lock (Lock)
        {
            var existing = _config.Locations.FirstOrDefault(l =>
                l.Name.Equals(loc.Name, StringComparison.OrdinalIgnoreCase));
            string? pwBlob = existing?.PasswordProtected;
            if (!string.IsNullOrEmpty(loc.PasswordPlaintext))
                pwBlob = Dpapi.Protect(loc.PasswordPlaintext);
            string? smbPwBlob = existing?.SmbPasswordProtected;
            if (!string.IsNullOrEmpty(loc.SmbPasswordPlaintext))
                smbPwBlob = Dpapi.Protect(loc.SmbPasswordPlaintext);
            loc.PasswordProtected = pwBlob;
            loc.SmbPasswordProtected = smbPwBlob;
            loc.PasswordPlaintext = null;
            loc.SmbPasswordPlaintext = null;

            if (existing is null)
            {
                _config.Locations.Add(loc);
                return new ApplyLocationOutcome(true, null);
            }

            if (existing.DisguiseEnabled != loc.DisguiseEnabled ||
                !string.Equals(existing.CoverName, loc.CoverName, StringComparison.Ordinal) ||
                existing.DisguiseDecoys != loc.DisguiseDecoys)
                return new ApplyLocationOutcome(false,
                    "Disguise settings cannot be changed on an existing location. " +
                    "Delete it and create a new disguised location instead.");

            existing.Type = loc.Type;
            existing.Host = loc.Host; existing.Port = loc.Port;
            existing.Ssl = loc.Ssl; existing.Username = loc.Username;
            existing.Connections = loc.Connections;
            existing.Newsgroup = loc.Newsgroup;
            existing.RetentionDays = loc.RetentionDays;
            existing.PasswordProtected = pwBlob;
            existing.Share = loc.Share; existing.SmbUser = loc.SmbUser;
            existing.SmbPasswordProtected = smbPwBlob;
            existing.VolumeSerial = loc.VolumeSerial;
            existing.VolumeLabel = loc.VolumeLabel;
            existing.UsbFolder = loc.UsbFolder;
            existing.DisguiseEnabled = loc.DisguiseEnabled;
            existing.CoverName = loc.CoverName;
            existing.DisguiseDecoys = loc.DisguiseDecoys;
            return new ApplyLocationOutcome(false, null);
        }
    }

    /// <summary>Result of <see cref="ApplyNntp"/>, captured under the lock.</summary>
    public sealed record ApplyNntpOutcome(
        bool Cleared, string Host, int Port, string Username, bool Ssl,
        int Connections, bool HasPassword);

    /// <summary>
    /// Applies an NNTP update under the lock, exactly as the old
    /// UpdateNntp lock block did up to the Save: empty host clears
    /// the configuration; otherwise the existing blob is kept unless
    /// a plaintext password is supplied (then DPAPI-protected, inside
    /// the lock, as with ApplyLocation), plaintext is cleared, and
    /// the Nntp object is assigned. Then the whole config is
    /// validated under the lock (a validation failure leaves the
    /// live Nntp assigned and skips the sync, as before), and the
    /// "Usenet" location is synced from the new settings. The
    /// return data is captured under the lock, so the handler's log
    /// and response cannot report a concurrent UpdateNntp call's
    /// host.
    /// DELIBERATE CHANGE, disclosed: today Save + ReloadJobs also
    /// ran inside this lock; they now run in the handler outside it,
    /// like every other Config site. That narrows the atomic window
    /// (Validate + sync are still atomic; sync + Save are not), but
    /// Save re-validates the whole config before writing, so an
    /// invalid state still cannot be persisted, and moving Reload
    /// outside removes a lock-order inversion that existed today:
    /// UpdateNntp acquired Store.Lock then _stateLock (via Reload),
    /// while every other Reload path acquires _stateLock then
    /// Store.Lock (via EffectiveProviders). No other site's
    /// atomicity changes.
    /// </summary>
    public ApplyNntpOutcome ApplyNntp(NntpConfig nntp)
    {
        lock (Lock)
        {
            if (string.IsNullOrWhiteSpace(nntp.Host))
            {
                _config.Nntp = null;
            }
            else
            {
                string? blobToKeep = _config.Nntp?.PasswordProtected;
                if (!string.IsNullOrEmpty(nntp.PasswordPlaintext))
                    blobToKeep = Dpapi.Protect(nntp.PasswordPlaintext);
                nntp.PasswordProtected = blobToKeep;
                nntp.PasswordPlaintext = null;
                _config.Nntp = nntp;
            }

            _config.Validate();

            var usenetLoc = _config.Locations.FirstOrDefault(l =>
                l.ParsedKind == LocationKind.Nntp && l.Name.Equals("Usenet", StringComparison.OrdinalIgnoreCase));
            if (usenetLoc is not null && _config.Nntp is not null)
            {
                usenetLoc.Host = _config.Nntp.Host;
                usenetLoc.Port = _config.Nntp.Port;
                usenetLoc.Ssl = _config.Nntp.Ssl;
                usenetLoc.Username = _config.Nntp.Username;
                usenetLoc.Connections = _config.Nntp.Connections;
                usenetLoc.Newsgroup = _config.Nntp.Newsgroup;
                usenetLoc.RetentionDays = _config.Nntp.RetentionDays;
                usenetLoc.PasswordProtected = _config.Nntp.PasswordProtected;
                usenetLoc.RedundancyMode = _config.Nntp.RedundancyMode;
            }

            return _config.Nntp is null
                ? new ApplyNntpOutcome(true, "", 0, "", false, 0, false)
                : new ApplyNntpOutcome(false, _config.Nntp.Host, _config.Nntp.Port,
                    _config.Nntp.Username, _config.Nntp.Ssl, _config.Nntp.Connections,
                    _config.Nntp.HasPassword);
        }
    }
}
