namespace FileKeep.Core.Service;

/// <summary>
/// Validation for <see cref="ServiceConfig"/> (config-lifecycle split,
/// Stage 1): the rules formerly in <c>ServiceConfig.Validate</c> live
/// here so they can be tested without file IO. Same rules, same
/// messages, same timing — <see cref="ServiceConfig.Validate"/>
/// delegates here, so Load, Save, and the dashboard temp-config check
/// validate exactly as before. No lock, persistence, or DTO changes:
/// those are Stages 2–5.
/// </summary>
public static class ServiceConfigValidator
{
    public static void Validate(ServiceConfig config)
    {
        if (config.DashboardPort is < 1 or > 65535)
            throw new InvalidOperationException($"dashboardPort {config.DashboardPort} is out of range.");
        if (string.IsNullOrWhiteSpace(config.DashboardBind))
            throw new InvalidOperationException("dashboardBind must not be empty.");
        if (config.RetentionCheckIntervalHours < 0)
            throw new InvalidOperationException(
                $"retentionCheckIntervalHours {config.RetentionCheckIntervalHours} is invalid (must be 0 or more).");

        // Locations (SF-21): these were never validated at all — a
        // duplicate name silently shadowed the first entry (resolution
        // is FirstOrDefault), unknown types and missing per-type fields
        // loaded fine and failed (or silently no-opped) at backup time.
        var locationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in config.Locations)
        {
            if (string.IsNullOrWhiteSpace(loc.Name))
                throw new InvalidOperationException("A backup location has no name.");
            if (!locationNames.Add(loc.Name))
                throw new InvalidOperationException($"Duplicate location name '{loc.Name}'.");
            LocationKind locKind;
            try { locKind = loc.ParsedKind; }
            catch (ArgumentException)
            {
                // A null type previously threw NullReferenceException out
                // of the Equals call; it now gets this same load error.
                throw new InvalidOperationException(
                    $"Location '{loc.Name}' has unknown type '{loc.Type}' (expected \"nntp\", \"smb\", or \"usb\").");
            }
            if (locKind == LocationKind.Nntp)
            {
                if (string.IsNullOrWhiteSpace(loc.Host))
                    throw new InvalidOperationException($"Location '{loc.Name}' (nntp) has no host.");
                if (loc.Port is < 1 or > 65535)
                    throw new InvalidOperationException($"Location '{loc.Name}' port {loc.Port} is out of range.");
                if (loc.Connections < 1 || loc.Connections > 100)
                    throw new InvalidOperationException($"Location '{loc.Name}' connections must be between 1 and 100.");
            }
            else if (locKind == LocationKind.Smb)
            {
                if (string.IsNullOrWhiteSpace(loc.Share))
                    throw new InvalidOperationException($"Location '{loc.Name}' (smb) has no share path.");
            }
            else // Usb: the parser only yields these three kinds.
            {
                if (string.IsNullOrWhiteSpace(loc.VolumeSerial))
                    throw new InvalidOperationException($"Location '{loc.Name}' (usb) has no volume serial.");
            }
            if (!string.IsNullOrEmpty(loc.RedundancyMode))
            {
                try { RedundancyKindParser.Parse(loc.RedundancyMode); }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException(
                        $"Location '{loc.Name}' has unknown redundancyMode '{loc.RedundancyMode}' (expected \"none\", \"xor\", or \"par2\").");
                }
            }
        }

        // Empty jobs are valid: a fresh install has no jobs yet, and the
        // dashboard is used to create the first one. The service must start
        // so the dashboard is available.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in config.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Name))
                throw new InvalidOperationException("A job has no name.");
            if (!names.Add(job.Name))
                throw new InvalidOperationException($"Duplicate job name '{job.Name}'.");
            if (string.IsNullOrWhiteSpace(job.Repo))
                throw new InvalidOperationException($"Job '{job.Name}' has no repo path.");
            if (job.EffectiveSources.Count == 0)
                throw new InvalidOperationException($"Job '{job.Name}' has no source folder.");
            if (job.IsDiskImage && job.EffectiveSources.Count > 1)
                throw new InvalidOperationException($"Job '{job.Name}': disk-image jobs back up a single device.");
            ValidateNoNestedSources(job);
            ScheduleParser.Parse(job.Schedule); // validates format
            try { BackupModeParser.Parse(job.Mode); }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Job '{job.Name}' has unknown mode '{job.Mode}' (expected \"incremental\" or \"full\").");
            }
            try { JobKindParser.Parse(job.JobType); }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Job '{job.Name}' has unknown jobType '{job.JobType}' (expected \"files\" or \"disk\").");
            }
            if (job.Vss && job.BackupPrivilege)
                throw new InvalidOperationException(
                    $"Job '{job.Name}': 'vss' and 'backupPrivilege' are mutually exclusive.");
            // Enum/numeric fields (SF-22): an unknown redundancyMode
            // silently behaved as "none", an unknown verificationMode
            // was rejected only when a backup started, and
            // parityGroupSize < 2 with xor either hung parity
            // generation (0/negative) or silently produced no parity
            // (1) — all become load-time errors with the job named.
            // Unlike locations/providers, where an empty redundancyMode
            // means "fall back to the job's", a job's own redundancyMode
            // must name a mode: empty matched none of the three modes
            // before the enum conversion and is still rejected — the
            // parser's empty-to-None default does not apply here.
            if (string.IsNullOrEmpty(job.RedundancyMode))
                throw new InvalidOperationException(
                    $"Job '{job.Name}' has unknown redundancyMode '{job.RedundancyMode}' (expected \"none\", \"xor\", or \"par2\").");
            try { RedundancyKindParser.Parse(job.RedundancyMode); }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Job '{job.Name}' has unknown redundancyMode '{job.RedundancyMode}' (expected \"none\", \"xor\", or \"par2\").");
            }
            try { VerificationModeParser.Parse(job.VerificationMode); }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Job '{job.Name}': {ex.Message}");
            }
            // Job and location redundancies are validated by this point;
            // provider redundancies are not (their check runs below), so
            // the provider leg stays a comparison — a garbage provider
            // value is not xor, and its own check rejects it with its
            // own message rather than surfacing as an ArgumentException.
            bool xorInPlay = job.ParsedRedundancy == RedundancyKind.Xor
                || config.Locations.Any(l => l.ParsedRedundancy == RedundancyKind.Xor)
                || config.EffectiveProviders.Any(p => string.Equals(
                    p.RedundancyMode, "xor", StringComparison.OrdinalIgnoreCase));
            if (xorInPlay && job.ParityGroupSize < 2)
                throw new InvalidOperationException(
                    $"Job '{job.Name}': parityGroupSize must be at least 2 when redundancyMode is \"xor\" (got {job.ParityGroupSize}).");
            // Targets (SF-21): every named target must be a defined
            // location. No backwards compatibility: the bare "nntp"/"smb"
            // tokens from pre-locations configs are unknown names and
            // fail here like any other unknown target.
            foreach (string target in job.EffectiveTargets)
            {
                if (!locationNames.Contains(target))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' targets unknown backup location '{target}'.");
            }
            if (job.Vss && !OperatingSystem.IsWindows())
                throw new InvalidOperationException(
                    $"Job '{job.Name}': 'vss' requires Windows.");
        }
        try { LinkKindParser.Parse(config.ProtectionLinkMode); }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"protectionLinkMode '{config.ProtectionLinkMode}' is invalid (expected \"or\" or \"and\").");
        }
        // Follow-up to the gaps audit (Joel, 2026-10-27): the
        // protection profile's method-combination rules are enforced
        // at load, not only at the profile endpoint. Phone is
        // secondary-only (USB recovery cannot use TOTP), and "and"
        // requires both primaries — mirroring SetProtectionProfile.
        // Stored fields only: the scheduler passphrase env var is a
        // runtime override, not a saved method, so a saved phone-only
        // profile is invalid even when the env var happens to be set.
        // Fresh configs still load: no phone, linkMode "or".
        bool passphraseMethod = (!config.GeneratedSecretMode &&
            !string.IsNullOrEmpty(config.PassphraseProtected)) ||
            (!string.IsNullOrEmpty(config.PassphraseWrapProtected) &&
             !string.IsNullOrEmpty(config.PassphraseWrapSaltB64));
        bool yubiKeyMethod = config.GeneratedSecretMode || config.PassphraseYubiKey;
        if (config.PhoneAuthEnabled && !passphraseMethod && !yubiKeyMethod)
            throw new InvalidOperationException(
                "Phone authenticator cannot be the only enabled method. Enable Passphrase or YubiKey first.");
        if (config.ParsedLink == LinkKind.And && !(passphraseMethod && yubiKeyMethod))
            throw new InvalidOperationException(
                "\"and\" link mode requires both Passphrase and YubiKey to be enabled.");
        if (config.Nntp is not null && !string.IsNullOrWhiteSpace(config.Nntp.Host))
        {
            if (config.Nntp.Port is < 1 or > 65535)
                throw new InvalidOperationException($"nntp.port {config.Nntp.Port} is out of range.");
            if (config.Nntp.Connections < 1 || config.Nntp.Connections > 100)
                throw new InvalidOperationException("nntp.connections must be between 1 and 100.");
            // The global NNTP entry's redundancy was never validated: a
            // garbage value silently behaved as "none" in the uploader
            // (the same silent-none bug SF-22 fixed for jobs). Blank-host
            // globals are placeholders (uploads skip them), so only
            // host-bearing globals are checked.
            if (!string.IsNullOrEmpty(config.Nntp.RedundancyMode))
            {
                try { RedundancyKindParser.Parse(config.Nntp.RedundancyMode); }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException(
                        $"nntp.redundancyMode '{config.Nntp.RedundancyMode}' is invalid (expected \"none\", \"xor\", or \"par2\").");
                }
            }
        }
        // Providers (SF-21): entries in the list get the same checks the
        // legacy singleton gets. A blank-host entry is a tolerated
        // placeholder (uploads skip it with a log line — SF-14), so only
        // entries with a host are field-checked.
        foreach (var provider in config.NntpProviders)
        {
            if (string.IsNullOrWhiteSpace(provider.Host))
                continue;
            if (provider.Port is < 1 or > 65535)
                throw new InvalidOperationException(
                    $"nntpProviders entry '{provider.Host}' port {provider.Port} is out of range.");
            if (provider.Connections < 1 || provider.Connections > 100)
                throw new InvalidOperationException(
                    $"nntpProviders entry '{provider.Host}' connections must be between 1 and 100.");
            if (!string.IsNullOrEmpty(provider.RedundancyMode))
            {
                try { RedundancyKindParser.Parse(provider.RedundancyMode); }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException(
                        $"nntpProviders entry '{provider.Host}' has unknown redundancyMode '{provider.RedundancyMode}' (expected \"none\", \"xor\", or \"par2\").");
                }
            }
        }
    }


    /// <summary>
    /// Rejects duplicate or nested source folders: backing up a folder
    /// already includes everything below it, so listing both would back up
    /// the nested files twice. The dashboard prevents this in the UI; this
    /// is the server-side backstop for hand-edited configs. Comparison
    /// is on canonical paths (see <see cref="PathCanonicalization"/>):
    /// symlinks, junctions, and Windows 8.3 short paths naming the same
    /// directory are caught, not just lexically identical spellings.
    /// </summary>
    public static void ValidateNoNestedSources(BackupJobConfig job)
    {
        var normalized = new List<string>();
        foreach (string s in job.EffectiveSources)
        {
            string n;
            try
            {
                n = PathCanonicalization.Canonicalize(s);
            }
            catch
            {
                throw new InvalidOperationException($"Job '{job.Name}' has an invalid source path '{s}'.");
            }
            normalized.Add(n);
        }
        for (int i = 0; i < normalized.Count; i++)
        {
            for (int j = 0; j < normalized.Count; j++)
            {
                if (i == j)
                    continue;
                if (normalized[i].Equals(normalized[j], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' lists the same source folder twice: '{job.EffectiveSources[i]}'.");
                if (normalized[i].StartsWith(normalized[j] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}': source '{job.EffectiveSources[i]}' is inside source '{job.EffectiveSources[j]}' — a folder already includes everything below it.");
            }
        }
    }
}
