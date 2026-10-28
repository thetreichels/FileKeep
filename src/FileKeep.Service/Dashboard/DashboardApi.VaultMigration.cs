using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// DashboardApi — vault migration (the migrate half of rekey).
///
/// A passphrase change orphans every banked vault: repos open by
/// deriving their key from the passphrase plus a stored salt, so a
/// new passphrase cannot open old repos. Migration is the path that
/// preserves backups when the SOURCES still exist: fresh repos are
/// created under the new secret, jobs are re-pointed at them, and
/// the next runs repopulate them from source. The old repos and
/// their destination pools are left untouched and recorded as
/// previous vaults.
///
/// The destination handling is the part that must be right, because
/// SMB/USB chunk pools are shared by chunk ID with no key in the
/// address: reusing the same pool for a new key would either
/// overwrite old-key blobs (SMB compares content) or leave old-key
/// blobs in place for the new repo to trip over (USB skips on size
/// match). So migration creates FRESH pools for shared-pool
/// destinations — a sibling share folder for SMB, a new USB folder
/// (or cover name, when disguised) for USB — and re-points the
/// jobs' targets at them. Usenet is append-only (each post is a new
/// article), so its locations are shared safely and left alone.
///
/// Migration runs either AFTER the passphrase change (no new secret
/// supplied: the currently configured secret is used) or BEFORE it
/// (new secret typed: the caller must then change the passphrase to
/// match before the next run, or runs will fail to open the new
/// vaults — that case is called out in the warnings).
/// </summary>
public static partial class DashboardApi
{
    /// <summary>
    /// Migrates every job with a repo to fresh vaults under
    /// <paramref name="newSecret"/> (or the currently configured
    /// secret when null). Requires fresh proof of an existing
    /// method (the shared gate) and refuses in YubiKey-only mode
    /// (the root secret never changes there). The whole
    /// validate → Init → mutate → save sequence holds the config
    /// lock, matching the profile save. A failed repo Init aborts
    /// before any mutation; repos already Init'd stay as unlinked
    /// empty dirs, which is harmless.
    /// </summary>
    public static (int Status, object Payload) MigrateVaults(
        ServiceConfig config, string configPath, string? newSecret,
        IYubiKeyProvider? yubiKeyProvider, Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false, string? proofTotp = null)
    {
        var proof = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proof is not null)
            return proof.Value;
        if (config.GeneratedSecretMode)
            return (400, new ErrorDto(
                "Vault migration is a passphrase-mode operation: in YubiKey-only mode the root secret " +
                "never changes, so vaults never need migrating."));
        string? secret = newSecret;
        string? current = null;
        BackupScheduler.TryGetPassphrase(config, out current, out _);
        if (string.IsNullOrEmpty(secret))
        {
            if (string.IsNullOrEmpty(current))
                return (400, new ErrorDto(
                    "No new secret supplied and the current secret could not be resolved on this PC — " +
                    "type the new secret."));
            secret = current;
        }

        var warnings = new List<string>();
        if (!string.IsNullOrEmpty(current) && !string.Equals(secret, current, StringComparison.Ordinal))
            warnings.Add(
                "The vaults were created under the typed secret, which is not the currently configured " +
                "secret — change the passphrase to match before the next run, or runs will fail to open " +
                "the new vaults.");

        lock (config.JobsLock)
        {
            // A job without a repo path fails config validation on
            // save, so migration refuses upfront instead of Init'ing
            // repos and then failing the save (which would leave
            // unlinked empty dirs and a confusing error).
            var repoLess = config.Jobs
                .Where(j => string.IsNullOrWhiteSpace(j.Repo)).Select(j => j.Name).ToList();
            if (repoLess.Count > 0)
                return (400, new ErrorDto(
                    $"Job{(repoLess.Count > 1 ? "s" : "")} {string.Join(", ", repoLess)} {(repoLess.Count > 1 ? "have" : "has")} no repo path — " +
                    "delete or repair them before migrating."));
            var jobsToMigrate = config.Jobs.ToList();

            // Fresh pools for shared-pool destinations, mapped once
            // per old location so jobs sharing a location share the
            // new pool too.
            var referencedNames = jobsToMigrate
                .SelectMany(j => j.EffectiveTargets)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // A shared-pool location without its pool address cannot
            // be given a fresh pool — and it cannot keep the old one
            // either (its jobs would keep the colliding pool). Refuse
            // upfront, before any Init or mutation, naming every
            // offender. (Such locations also fail config validation
            // on save, so this names the real fix instead of letting
            // the save fail with a generic validation error.)
            var unmappable = new List<string>();
            foreach (string name in referencedNames)
            {
                var loc = config.Locations.FirstOrDefault(l =>
                    string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (loc is null)
                    continue;
                if (loc.ParsedKind == LocationKind.Smb &&
                    string.IsNullOrWhiteSpace(loc.Share))
                    unmappable.Add($"SMB location '{loc.Name}' has no share path");
                else if (loc.ParsedKind == LocationKind.Usb &&
                    string.IsNullOrWhiteSpace(loc.VolumeSerial))
                    unmappable.Add($"USB location '{loc.Name}' has no volume serial");
            }
            if (unmappable.Count > 0)
                return (400, new ErrorDto(
                    "These destinations cannot be given fresh pools — fix them before migrating: " +
                    string.Join("; ", unmappable) + "."));

            var locationMap = new Dictionary<string, BackupLocation>(StringComparer.OrdinalIgnoreCase);
            var locationsCreated = new List<string>();
            foreach (string name in referencedNames)
            {
                var loc = config.Locations.FirstOrDefault(l =>
                    string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (loc is null)
                    continue; // Unknown target already resolves to nothing; leave it alone.
                if (loc.ParsedKind == LocationKind.Nntp)
                    continue; // Append-only: safe to share across keys.
                BackupLocation fresh;
                if (loc.ParsedKind == LocationKind.Smb)
                {
                    fresh = loc.Clone();
                    fresh.Share = loc.Share.TrimEnd('\\', '/') + "-migrated";
                }
                else if (loc.ParsedKind == LocationKind.Usb)
                {
                    fresh = loc.Clone();
                    if (loc.DisguiseEnabled && !string.IsNullOrWhiteSpace(loc.CoverName))
                        fresh.CoverName = loc.CoverName + "-migrated";
                    else
                        fresh.UsbFolder = (string.IsNullOrWhiteSpace(loc.UsbFolder) ? "FileKeep" : loc.UsbFolder) + "-migrated";
                }
                else
                {
                    continue; // Unknown type: leave alone.
                }
                fresh.Name = UniqueLocationName(config, loc.Name + " (migrated)");
                config.Locations.Add(fresh);
                locationMap[loc.Name] = fresh;
                locationsCreated.Add(fresh.Name);
            }

            // One fresh repo per distinct old repo path; jobs sharing
            // a repo share the new one too.
            var repoMappings = new List<VaultRepoMappingDto>();
            foreach (var group in jobsToMigrate.GroupBy(j => j.Repo.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                string oldRepo = group.Key;
                string newRepo = UniqueRepoPath(config, oldRepo);
                try { BackupRepository.Init(newRepo, secret); }
                catch (Exception ex)
                {
                    return (400, new ErrorDto($"Could not create the new vault for '{oldRepo}': {ex.Message}"));
                }
                var jobNames = group.Select(j => j.Name).ToList();
                var oldTargets = group
                    .SelectMany(j => j.EffectiveTargets)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var job in group)
                {
                    job.Repo = newRepo;
                    if (job.Targets is { Count: > 0 })
                        job.Targets = job.Targets
                            .Select(t => locationMap.TryGetValue(t.Trim(), out var fresh) ? fresh.Name : t)
                            .ToList();
                }
                config.PreviousVaults.Add(new MigratedVaultRecord
                {
                    Jobs = jobNames,
                    OldRepo = oldRepo,
                    NewRepo = newRepo,
                    MigratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                    OldTargets = oldTargets,
                });
                repoMappings.Add(new VaultRepoMappingDto(oldRepo, newRepo, jobNames));
            }

            try { config.Save(configPath); }
            catch (Exception ex) { return (400, new ErrorDto(ex.Message)); }
            log?.Invoke($"dashboard: vaults migrated — {repoMappings.Count} repos, {locationsCreated.Count} fresh locations.");
            return (200, new VaultMigrationSummaryDto(
                jobsToMigrate.Count, repoMappings, locationsCreated, warnings));
        }
    }

    private static string UniqueRepoPath(ServiceConfig config, string oldRepo)
    {
        string basePath = oldRepo.TrimEnd('\\', '/') + "-migrated";
        string candidate = basePath;
        int n = 2;
        while (Directory.Exists(candidate) ||
               config.Jobs.Any(j => string.Equals(j.Repo, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = basePath + "-" + n++;
        }
        return candidate;
    }

    private static string UniqueLocationName(ServiceConfig config, string baseName)
    {
        string candidate = baseName;
        int n = 2;
        while (config.Locations.Any(l => string.Equals(l.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = baseName + " " + n++;
        }
        return candidate;
    }
}
