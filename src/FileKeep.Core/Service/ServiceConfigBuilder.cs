namespace FileKeep.Core.Service;

/// <summary>
/// Validated construction for <see cref="ServiceConfig"/> (config-lifecycle
/// split, Stage 5): replaces the dashboard's temp-config validation trick.
/// A builder composes a candidate config from the live config plus one
/// changed job, validates it, and returns it — so no caller can hold an
/// unvalidated config. The returned candidate is validation-only: the
/// caller still applies the change to the live config under JobsLock and
/// saves through the usual path (the apply path is unchanged).
/// </summary>
public sealed class ServiceConfigBuilder
{
    private readonly ServiceConfig _live;
    private BackupJobConfig? _job;

    public ServiceConfigBuilder(ServiceConfig live)
    {
        _live = live;
    }

    /// <summary>
    /// The job to validate: swapped into the candidate's job list by name
    /// (same name replaces, new name appends — the create/edit rule).
    /// Must be called before <see cref="Build"/>.
    /// </summary>
    public ServiceConfigBuilder WithJob(BackupJobConfig job)
    {
        _job = job;
        return this;
    }

    /// <summary>
    /// Builds the candidate and validates it. Throws the same
    /// <see cref="InvalidOperationException"/> the temp-config path threw
    /// for an invalid candidate. The candidate mirrors the temp config
    /// the dashboard used to build inline: live port/bind/Nntp, snapshot
    /// locations, and snapshot jobs with the candidate swapped in.
    /// </summary>
    public ServiceConfig Build()
    {
        if (_job is null)
            throw new InvalidOperationException("WithJob must be called before Build.");
        var job = _job;
        var candidate = new ServiceConfig
        {
            DashboardPort = _live.DashboardPort,
            DashboardBind = _live.DashboardBind,
            Nntp = _live.Nntp,
            Locations = _live.SnapshotLocations(),
            Jobs = _live.SnapshotJobs()
                .Where(j => !j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { job }).ToList(),
        };
        candidate.Validate();
        return candidate;
    }
}
