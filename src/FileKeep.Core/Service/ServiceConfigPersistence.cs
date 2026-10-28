using System.Text.Json;
using FileKeep.Core.Json;

namespace FileKeep.Core.Service;

/// <summary>
/// Persistence for <see cref="ServiceConfig"/> (config-lifecycle split,
/// Stage 2): the load/save code formerly in <see cref="ServiceConfig"/>
/// lives here so it can be tested for atomicity without the whole
/// object. Same semantics, same messages, same timing —
/// <see cref="ServiceConfig.Load"/> and <see cref="ServiceConfig.Save"/>
/// only delegate here. No lock, DTO, or validation-rule changes: the
/// lock lives in ServiceConfigStore (Stage 3) and
/// ServiceConfig.JobsLock delegates it, validation stays in
/// ServiceConfigValidator (Stage 1).
/// One deliberate deviation from a byte-identical move, disclosed:
/// the original <c>Save</c> wrote the temp file outside the try, so a
/// write failure left a partial temp behind and only a move failure
/// cleaned up. Here the write and the move share one try, so any
/// failure cleans up — the plan's "temp cleanup on failure" read
/// literally, and no caller can depend on a leftover partial temp
/// (temps are GUID-named and never read back).
/// Second deviation, hardening (Joel, 2026-10-28): the original held
/// the lock only around serialization, so concurrent same-path saves
/// raced the move and lost on Windows. The temp-write+move now run
/// under a static process-wide gate (SaveLock). Save itself takes
/// the two locks sequentially — store lock for serialization,
/// released before SaveLock for write+move — and never acquires the
/// store lock while holding SaveLock, so the order is always store
/// lock before SaveLock and no cycle exists. Callers may already
/// hold the store lock across a Save call (dashboard handlers do);
/// that re-locks legally and preserves the order. An instance lock
/// alone was tried first and
/// does NOT fix the race: the failing test saves through distinct
/// config objects, which have distinct locks.
/// </summary>
public static class ServiceConfigPersistence
{
    public static ServiceConfig Load(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { throw new InvalidOperationException($"Cannot read service config '{path}': {ex.Message}", ex); }
        ServiceConfig? config;
        try
        {
            // Fail closed on typos: a misspelled key (e.g. "autoVeriy")
            // must not silently fall back to a default.
            config = FileKeepJsonOptions.Deserialize(json,
                FileKeepJsonOptions.ServiceConfigLoad, FileKeepJsonContext.Default.ServiceConfig);
        }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid service config '{path}': {ex.Message}", ex); }
        if (config is null)
            throw new InvalidOperationException($"Invalid service config '{path}': empty document.");
        // No backwards compatibility: the pre-locations config shapes
        // are no longer migrated (MigrateToLocations is gone) and the
        // retired per-job fields are gone too, so a legacy file fails
        // to load under the fail-closed loader instead of loading
        // inertly. Bare "nntp"/"smb" target tokens are NOT rewritten:
        // they name no location, resolve to nothing at run time, and
        // the job lands in the derived NeedsLocation state.
        config.Validate();
        return config;
    }

    // Process-wide save gate (Joel, 2026-10-28): the instance lock
    // serializes saves on ONE config object, but distinct objects
    // saving to one path still raced the move — Windows refuses
    // concurrent overwriting moves (UnauthorizedAccessException for
    // the losers) while Linux rename always wins. This static gate
    // serializes the temp-write+move across all instances, so every
    // save succeeds and the final file is one complete write. Save
    // itself takes the locks sequentially: the store lock guards
    // serialization and is released before SaveLock guards write+move,
    // and Save never acquires the store lock while holding SaveLock —
    // so the order is always store lock before SaveLock and no cycle
    // exists. (Callers may already hold the store lock across a Save
    // call; that preserves the order.) Stage 3 considered folding
    // this gate into
    // the store lock and deliberately kept it: the store lock guards
    // ONE config's data, while this gate guards the filesystem path
    // across instances — different resources, and deleting the gate
    // would break the ConcurrentSamePath guarantee on Windows (the
    // store lock held through the whole save still would not
    // serialize distinct config objects). The gate stays, explained,
    // as the single cross-instance file-write lock.
    private static readonly object SaveLock = new();

    public static void Save(ServiceConfig config, string path)
    {
        config.Validate();
        // Serialize under the config's lock: dashboard handlers mutate Jobs on
        // other threads, and serializing a concurrently-modified list throws
        // or writes torn JSON. This is the store's lock object, exposed
        // through the facade (Stage 3 moved it there); SaveLock below
        // is a second lock object but guards a different resource (the
        // filesystem path), not the data, so a handler already holding
        // the store lock re-locks legally.
        string json;
        lock (config.JobsLock)
        {
            json = FileKeepJsonOptions.Serialize(config,
                FileKeepJsonOptions.ServiceConfigSave, FileKeepJsonContext.Default.ServiceConfig);
        }
        // Unique temp name: concurrent saves must not share a temp file.
        // The write+move run under the static gate (see SaveLock): the
        // instance lock alone cannot serialize distinct objects saving
        // to one path, and Windows refuses their racing moves.
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        lock (SaveLock)
        {
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
