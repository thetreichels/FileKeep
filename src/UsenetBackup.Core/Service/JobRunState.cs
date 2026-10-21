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
    /// The job has no backup location at all (its target list is empty —
    /// the state a job lands in when its locations are deleted out from
    /// under it). Repaired by attaching a location.
    /// </summary>
    NeedsLocation,

    /// <summary>
    /// Every target is a USB location whose drive is not currently
    /// attached. Repaired by plugging the drive back in.
    /// </summary>
    WaitingForDrive,
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
    /// whether its drive is attached right now.
    /// </summary>
    public readonly record struct LocationFacts(bool Exists, bool IsUsb, bool UsbPresent);

    public static JobRunState Evaluate(
        BackupJobConfig job, Func<string, LocationFacts> locationLookup)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(locationLookup);
        if (job.Paused)
            return JobRunState.UserPaused;
        var targets = job.EffectiveTargets;
        if (targets.Count == 0)
            return JobRunState.NeedsLocation;
        // A target is usable when its location exists and is not an
        // absent USB drive. A target naming no location at all (a legacy
        // bare "nntp" token the migration left alone) defers to the
        // executor's own resolution, exactly as before this state
        // machine existed — location deletion strips names from target
        // lists in the same operation, so a dangling name is never a
        // product state, only a hand-edited one.
        foreach (string target in targets)
        {
            LocationFacts facts = locationLookup(target);
            if (!facts.Exists || !facts.IsUsb || facts.UsbPresent)
                return JobRunState.Active;
        }
        return JobRunState.WaitingForDrive;
    }

    /// <summary>Short human reason for a non-Active state, for logs and errors.</summary>
    public static string Describe(JobRunState state) => state switch
    {
        JobRunState.Active => "active",
        JobRunState.UserPaused => "paused",
        JobRunState.NeedsLocation => "no backup location",
        JobRunState.WaitingForDrive => "USB drive not connected",
        _ => state.ToString(),
    };

    /// <summary>
    /// Builds a lookup over the config's locations. USB presence is
    /// resolved through <see cref="UsbDriveResolver"/> by volume serial
    /// (drive letters are irrelevant), once per location per lookup
    /// instance — build one lookup per evaluation pass, not per job.
    /// </summary>
    public static Func<string, LocationFacts> BuildLookup(ServiceConfig config)
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
                facts = new LocationFacts(false, false, false);
            }
            else if (loc.Type.Equals("usb", StringComparison.OrdinalIgnoreCase))
            {
                bool present = UsbDriveResolver.ResolveBackupPath(
                    loc.VolumeSerial, loc.UsbFolder) is not null;
                facts = new LocationFacts(true, true, present);
            }
            else
            {
                facts = new LocationFacts(true, false, true);
            }
            cache[name] = facts;
            return facts;
        };
    }
}
