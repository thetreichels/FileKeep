namespace UsenetBackup.Core.Service;

/// <summary>
/// Whether a job can run right now, and if not, why not. Only
/// <see cref="UserPaused"/> is stored (on <see cref="BackupJobConfig"/>);
/// the rest are derived fresh from the job's targets and the machine's
/// attached drives every time the question is asked, so a derived pause
/// lifts by itself the moment its cause clears — a location is attached,
/// a USB drive comes back — with nothing to reset and no catch-up run.
/// </summary>
public enum JobRunState
{
    /// <summary>Has at least one usable target; runs on schedule.</summary>
    Active,

    /// <summary>The user paused this job. Only the user resumes it.</summary>
    UserPaused,

    /// <summary>
    /// No security is set up the scheduler can use: no passphrase (or
    /// YubiKey-only generated secret) is stored for unattended runs —
    /// none was ever set up, or the stored copy no longer decrypts on
    /// this machine/account. The job is HELD, not failed: scheduled
    /// attempts are recorded as skips, never as failures, and the state
    /// lifts by itself the moment a usable passphrase exists
    /// (Settings → Security). Before this state existed, every attempt
    /// threw "Cannot run scheduled backup without a passphrase…" and
    /// piled up as a failure (Joel, 2026-10-22).
    /// </summary>
    HeldNoSecurity,

    /// <summary>
    /// The job has no usable backup location at all: its target list is
    /// empty (the state a job lands in when its locations are deleted
    /// out from under it) or none of its target names resolves to a
    /// location (a pre-locations config's bare "nntp"/"smb" tokens).
    /// Repaired by attaching a location.
    /// </summary>
    NeedsLocation,

    /// <summary>
    /// Every target is a USB location whose drive is not currently
    /// attached. Repaired by plugging the drive back in.
    /// </summary>
    WaitingForDrive,

    /// <summary>
    /// Every target is a network location (Usenet or SMB) that was
    /// unreachable the last time it was tried — at a due time or a
    /// manual run. Laptops roam; an unreachable network at the
    /// scheduled time is an absence, not a failure. Repaired by the
    /// network coming back; the next due time tries again.
    /// </summary>
    WaitingForNetwork,
}

/// <summary>
/// Computes a job's <see cref="JobRunState"/> from its config and a
/// location lookup. Pure apart from the lookup delegate, so the rules
/// are unit-testable without drives or a running service.
/// </summary>
public static class JobStateEvaluator
{
    /// <summary>
    /// Location facts for one target name: whether a location with that
    /// name exists, whether it is a USB location, and — USB only —
    /// whether its drive is attached right now; — network only —
    /// whether the location is currently considered away (unreachable
    /// when last tried).
    /// </summary>
    public readonly record struct LocationFacts(
        bool Exists, bool IsUsb, bool UsbPresent, bool NetworkAway);

    /// <summary>
    /// Location-only evaluation (assumes security is set up). Kept for
    /// focused location-rule tests — production paths (the scheduler's
    /// gate and status feed) use the three-argument form so a missing
    /// passphrase holds the job instead of failing it.
    /// </summary>
    public static JobRunState Evaluate(
        BackupJobConfig job, Func<string, LocationFacts> locationLookup)
        => Evaluate(job, locationLookup, securityReady: true);

    public static JobRunState Evaluate(
        BackupJobConfig job, Func<string, LocationFacts> locationLookup,
        bool securityReady)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(locationLookup);
        if (job.Paused)
            return JobRunState.UserPaused;
        // Security is the precondition for ANY unattended run — the
        // executor resolves the passphrase before it touches a target —
        // so a missing one holds the job ahead of the location checks.
        if (!securityReady)
            return JobRunState.HeldNoSecurity;
        var targets = job.EffectiveTargets;
        if (targets.Count == 0)
            return JobRunState.NeedsLocation;
        // A target is usable when its location exists, is not an absent
        // USB drive, and is not a network location currently considered
        // away. A target naming no location at all — a legacy bare
        // "nntp"/"smb" token from a pre-locations config, or a name left
        // dangling by a hand edit — resolves to NOTHING (Phase 6 dropped
        // the bare-token fallback): it can never make the job Active.
        // When every target is unresolvable the job is NeedsLocation,
        // exactly like a job with no targets at all. (Location deletion
        // strips names from target lists in the same operation, so in
        // product flows a dangling name only ever comes from an old or
        // hand-edited config.)
        bool anyUsbAway = false;
        bool anyNetworkAway = false;
        foreach (string target in targets)
        {
            LocationFacts facts = locationLookup(target);
            if (!facts.Exists)
                continue;
            if (facts.IsUsb)
            {
                if (facts.UsbPresent)
                    return JobRunState.Active;
                anyUsbAway = true;
            }
            else if (!facts.NetworkAway)
            {
                return JobRunState.Active;
            }
            else
            {
                anyNetworkAway = true;
            }
        }
        if (anyUsbAway)
            return JobRunState.WaitingForDrive;
        if (anyNetworkAway)
            return JobRunState.WaitingForNetwork;
        return JobRunState.NeedsLocation;
    }

    /// <summary>Short human reason for a non-Active state, for logs and errors.</summary>
    public static string Describe(JobRunState state) => state switch
    {
        JobRunState.Active => "active",
        JobRunState.UserPaused => "paused",
        JobRunState.HeldNoSecurity => "set up security to run backups",
        JobRunState.NeedsLocation => "no backup location",
        JobRunState.WaitingForDrive => "USB drive not connected",
        JobRunState.WaitingForNetwork => "network location unavailable",
        _ => state.ToString(),
    };

    /// <summary>
    /// Builds a lookup over the config's locations. USB presence is
    /// resolved through <see cref="UsbDriveResolver"/> by volume serial
    /// (drive letters are irrelevant), once per location per lookup
    /// instance — build one lookup per evaluation pass, not per job.
    /// Network away-ness is NOT probed here: it comes from the
    /// <paramref name="networkAway"/> delegate (a live probe at a due
    /// time, or the recorded result of the last try for status reads),
    /// so rendering a status page never touches the network.
    /// </summary>
    public static Func<string, LocationFacts> BuildLookup(
        ServiceConfig config, Func<BackupLocation, bool>? networkAway = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var cache = new Dictionary<string, LocationFacts>(StringComparer.OrdinalIgnoreCase);
        return name =>
        {
            if (cache.TryGetValue(name, out var facts))
                return facts;
            var loc = config.Locations.FirstOrDefault(l =>
                l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (loc is null)
            {
                facts = new LocationFacts(false, false, false, false);
            }
            else if (loc.Type.Equals("usb", StringComparison.OrdinalIgnoreCase))
            {
                bool present = UsbDriveResolver.ResolveBackupPath(
                    loc.VolumeSerial, loc.UsbFolder) is not null;
                facts = new LocationFacts(true, true, present, false);
            }
            else
            {
                facts = new LocationFacts(true, false, false,
                    networkAway?.Invoke(loc) ?? false);
            }
            cache[name] = facts;
            return facts;
        };
    }
}
