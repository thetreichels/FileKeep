using System.Collections.Generic;
using System.Text.Json.Serialization;
using FileKeep.Core.Recovery;

namespace FileKeep.Service;

/// <summary>
/// Named DTO records for dashboard API responses. These replace anonymous
/// types so System.Text.Json source generation works under trimming.
/// All property names use [JsonPropertyName] to preserve the camelCase
/// JSON contract the dashboard frontend expects.
/// </summary>

public sealed record ErrorDto(
    [property: JsonPropertyName("error")] string Error);

public sealed record OkDto(
    [property: JsonPropertyName("ok")] bool Ok);

public sealed record OkErrorDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error);

public sealed record OkMessageDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string? Message);

public sealed record OkDetailDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("detail")] string? Detail);

public sealed record OkYubiKeyDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("yubikey")] bool Yubikey);

/// <summary>
/// Response for <c>POST /api/yubikey/setup-generated</c>: the one and
/// only time the generated repository secret is ever returned, in its
/// grouped display form for the recovery sheet.
/// </summary>
public sealed record YubiKeySetupGeneratedDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("yubikey")] bool Yubikey,
    [property: JsonPropertyName("recoverySecret")] string RecoverySecret);

public sealed record AcceptedDto(
    [property: JsonPropertyName("accepted")] bool Accepted);

public sealed record AcceptedReposDto(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("repos")] int Repos);

public sealed record ActiveDto(
    [property: JsonPropertyName("active")] bool Active);

/// <summary>The running backup's live progress (GET /api/run-progress):
/// phase, counts, per-destination bars, and the worst-case total
/// estimate that the job cards and the Overview card render.</summary>
public sealed record RunProgressDto(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("progress")] FileKeep.Core.Service.RunProgressSnapshot? Progress);

public sealed record YubiKeyStatusDto(
    [property: JsonPropertyName("detected")] bool Detected,
    [property: JsonPropertyName("devices")] string[] Devices);

public sealed record YubiKeyUnlockDto(
    [property: JsonPropertyName("yubikey")] bool Yubikey,
    [property: JsonPropertyName("unlocked")] bool Unlocked,
    [property: JsonPropertyName("yubiKeyOnly")] bool YubiKeyOnly,
    // MU-15: a typed-secret unlock is unverified until a restore
    // decrypts with it; the badge must not claim more than that.
    [property: JsonPropertyName("verified")] bool Verified,
    // MU-15: the phone is a real unlock path (it releases the
    // machine's DPAPI-held passphrase after a TOTP presence proof);
    // the restore view shows its unlock row only when enabled.
    [property: JsonPropertyName("phoneEnabled")] bool PhoneEnabled,
    // Restore gating (2026-10-25): restores are gated behind the
    // session whenever any unlock method is enabled, and the view
    // renders the session row on this flag — not on YubiKey mode.
    [property: JsonPropertyName("restoresGated")] bool RestoresGated,
    // Typed-passphrase unlock exists in passphrase mode without
    // YubiKey, and in YubiKey-only mode with an added passphrase
    // (the wrap); the view renders its row on this flag combined
    // with PassphraseWrap below. In passphrase+YubiKey mode the
    // endpoint refuses, so the row stays hidden there.
    [property: JsonPropertyName("passphraseEnabled")] bool PassphraseEnabled,
    // True when a passphrase has been added to a YubiKey-only
    // profile (it wraps the generated secret). The restore view
    // needs this separately from PassphraseEnabled: in
    // passphrase+YubiKey mode PassphraseEnabled is true but the
    // typed-passphrase row must NOT render.
    [property: JsonPropertyName("passphraseWrap")] bool PassphraseWrap);

/// <summary>Result of POST /api/offline-folders/rekey: totals for the
/// catalog, how many chunks were rewritten vs already new-key, and
/// the failures. The catalog is saved under the new key ONLY when
/// <see cref="Failures"/> is empty (<see cref="CatalogSaved"/>); a
/// partial run leaves the old-key catalog in place, so the user
/// fixes the failures and re-runs with the same secrets.</summary>
public sealed record OfflineRekeySummaryDto(
    [property: JsonPropertyName("folders")] int Folders,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("rewritten")] int Rewritten,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("failures")] IReadOnlyList<string> Failures,
    [property: JsonPropertyName("catalogSaved")] bool CatalogSaved);

/// <summary>One repo re-pointed by vault migration.</summary>
public sealed record VaultRepoMappingDto(
    [property: JsonPropertyName("oldRepo")] string OldRepo,
    [property: JsonPropertyName("newRepo")] string NewRepo,
    [property: JsonPropertyName("jobs")] IReadOnlyList<string> Jobs);

/// <summary>Result of POST /api/vaults/migrate: how many jobs moved,
/// which repos were created, which fresh locations were added, and
/// the warnings (typed-secret mismatch).</summary>
public sealed record VaultMigrationSummaryDto(
    [property: JsonPropertyName("jobsMigrated")] int JobsMigrated,
    [property: JsonPropertyName("repos")] IReadOnlyList<VaultRepoMappingDto> Repos,
    [property: JsonPropertyName("locationsCreated")] IReadOnlyList<string> LocationsCreated,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

/// <summary>Encrypted offline export envelope (file format,
/// snake_case like repo.json). Returned inside
/// <see cref="OfflineExportResultDto"/>; the same shape is the
/// import input. No filenames or credentials appear outside the
/// encrypted <c>data</c> blob.</summary>
public sealed record OfflineExportPackageDto(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("kdf_salt_b64")] string KdfSaltB64,
    [property: JsonPropertyName("kdf_iterations")] int KdfIterations,
    [property: JsonPropertyName("nonce_b64")] string NonceB64,
    [property: JsonPropertyName("data_b64")] string DataB64);

/// <summary>Result of POST /api/offline-folders/export: the download
/// filename and the envelope (the page stringifies it to a file).</summary>
public sealed record OfflineExportResultDto(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("package")] OfflineExportPackageDto Package);

/// <summary>Result of POST /api/offline-folders/import.</summary>
public sealed record OfflineImportSummaryDto(
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

/// <summary>Result of POST /api/offline-folders/forget: the entry is
/// gone, the destination chunks are untouched and orphaned, and
/// <see cref="JobsUsingLocation"/> names the live jobs that still
/// target the folder's location (the location itself is never
/// touched by a forget).</summary>
public sealed record OfflineForgetSummaryDto(
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("jobsUsingLocation")] IReadOnlyList<string> JobsUsingLocation);

/// <summary>Result of POST /api/offline-folders/open-token: the
/// token and the DAV URL carrying it (the page hands the URL to
/// the desktop host, which ShellExecutes it).</summary>
public sealed record OfflineOpenTokenDto(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("readOnly")] bool ReadOnly);

/// <summary>One currently open file, for the lock-while-open
/// warning (GET /api/offline-folders/opens).</summary>
public sealed record OfflineOpenFileDto(
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("readOnly")] bool ReadOnly);

/// <summary>Result of POST /api/offline-folders/save-as: the written
/// file's name, the destination kind ("local", "usb", or "smb"),
/// and the bytes written.</summary>
public sealed record OfflineSaveAsDto(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("destination")] string Destination,
    [property: JsonPropertyName("bytes")] long Bytes);

public sealed record ExistsDto(
    [property: JsonPropertyName("exists")] bool Exists);

/// <summary>Passphrase status for /api/passphrase/status.</summary>
public sealed record PassphraseStatusDto(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("yubikey")] bool Yubikey);

public sealed record CancelledDto(
    [property: JsonPropertyName("cancelled")] bool Cancelled);

public sealed record HasPasswordDto(
    [property: JsonPropertyName("hasPassword")] bool HasPassword);

/// <summary>Empty response (replaces new { }).</summary>
public sealed record EmptyDto;

/// <summary>Folder entry for /api/user-folders.</summary>
public sealed record FolderDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path);

/// <summary>
/// Response for GET /api/browse-folders (server-backed folder picker).
/// Path is the listed folder's full path, or "" for the drives view;
/// Parent is null at a drive root (and in the drives view), where the
/// picker's "up" target is the drives view. Folders holds the listed
/// folder's subdirectories; Drives is populated only in the drives view.
/// Home is the user's profile folder, the picker's default start.
/// </summary>
public sealed record BrowseFoldersDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("parent")] string? Parent,
    [property: JsonPropertyName("home")] string Home,
    [property: JsonPropertyName("folders")] List<FolderDto> Folders,
    [property: JsonPropertyName("drives")] List<FolderDto> Drives);

/// <summary>Job summary for /api/config response. Jobs carry no
/// credentials or connection details (Phase 6): targets are location
/// names, and everything else lives on the locations.</summary>
public sealed record ConfigJobDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("repo")] string? Repo,
    [property: JsonPropertyName("schedule")] string? Schedule,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("jobType")] string? JobType,
    [property: JsonPropertyName("backupPrivilege")] bool BackupPrivilege,
    [property: JsonPropertyName("vss")] bool Vss,
    [property: JsonPropertyName("targets")] List<string>? Targets,
    [property: JsonPropertyName("autoVerify")] bool AutoVerify,
    [property: JsonPropertyName("verificationMode")] string? VerificationMode,
    [property: JsonPropertyName("redundancyMode")] string? RedundancyMode,
    [property: JsonPropertyName("sources")] List<string>? Sources,
    [property: JsonPropertyName("paused")] bool Paused);

/// <summary>NNTP config summary for /api/config response.</summary>
public sealed record ConfigNntpDto(
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("ssl")] bool Ssl,
    [property: JsonPropertyName("connections")] int Connections,
    [property: JsonPropertyName("hasPassword")] bool HasPassword);

/// <summary>NNTP provider summary for /api/config response.</summary>
public sealed record ConfigNntpProviderDto(
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("ssl")] bool Ssl,
    [property: JsonPropertyName("connections")] int Connections,
    [property: JsonPropertyName("retentionDays")] int RetentionDays,
    [property: JsonPropertyName("redundancyMode")] string? RedundancyMode,
    [property: JsonPropertyName("hasPassword")] bool HasPassword);

/// <summary>Backup location summary for /api/config response.</summary>
public sealed record ConfigLocationDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("ssl")] bool Ssl,
    [property: JsonPropertyName("connections")] int Connections,
    [property: JsonPropertyName("newsgroup")] string? Newsgroup,
    [property: JsonPropertyName("retentionDays")] int RetentionDays,
    [property: JsonPropertyName("share")] string? Share,
    [property: JsonPropertyName("smbUser")] string? SmbUser,
    [property: JsonPropertyName("hasPassword")] bool HasPassword,
    [property: JsonPropertyName("usedBy")] List<string> UsedBy,
    [property: JsonPropertyName("volumeLabel")] string? VolumeLabel,
    [property: JsonPropertyName("usbFolder")] string? UsbFolder,
    [property: JsonPropertyName("disguiseEnabled")] bool DisguiseEnabled = false,
    [property: JsonPropertyName("coverName")] string? CoverName = null,
    [property: JsonPropertyName("disguiseDecoys")] bool DisguiseDecoys = false);

/// <summary>Full response for GET /api/config.</summary>
public sealed record ConfigSnapshotDto(
    [property: JsonPropertyName("dashboardPort")] int DashboardPort,
    [property: JsonPropertyName("dashboardBind")] string? DashboardBind,
    [property: JsonPropertyName("jobs")] List<ConfigJobDto> Jobs,
    [property: JsonPropertyName("nntp")] ConfigNntpDto? Nntp,
    [property: JsonPropertyName("nntpProviders")] List<ConfigNntpProviderDto> NntpProviders,
    [property: JsonPropertyName("locations")] List<ConfigLocationDto> Locations);

/// <summary>Last backup result for /api/status.</summary>
public sealed record StatusLastResultDto(
    [property: JsonPropertyName("startedLocal")] DateTime? StartedLocal,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("backupId")] string? BackupId,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("error")] string? Error,
    // Why one of several locations missed the last backup, when the job
    // is degraded (partial delivery). Null otherwise.
    [property: JsonPropertyName("degradedNote")] string? DegradedNote,
    // MU-19: the run-result side flags, exposed so a successful run
    // whose upload partially failed (or whose verify flagged trouble
    // on an older build's semantics) renders as a warning instead of
    // an unqualified green. Total upload failure and verify-detected
    // corruption now also flip Success itself.
    [property: JsonPropertyName("autoUploadFailed")] bool AutoUploadFailed,
    [property: JsonPropertyName("autoUploadError")] string? AutoUploadError,
    [property: JsonPropertyName("verifyFailed")] bool VerifyFailed,
    [property: JsonPropertyName("verifyError")] string? VerifyError,
    // MU-16: this result records a skipped due run, not an attempted
    // one (reason in Error). Rendered neutrally, never as a failure.
    [property: JsonPropertyName("skipped")] bool Skipped);

/// <summary>Job status for /api/status.</summary>
public sealed record StatusJobDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("repo")] string? Repo,
    [property: JsonPropertyName("sources")] List<string>? Sources,
    [property: JsonPropertyName("schedule")] string Schedule,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("backupPrivilege")] bool BackupPrivilege,
    [property: JsonPropertyName("vss")] bool Vss,
    [property: JsonPropertyName("nextRunLocal")] DateTime? NextRunLocal,
    [property: JsonPropertyName("consecutiveFailures")] int ConsecutiveFailures,
    [property: JsonPropertyName("lastResult")] StatusLastResultDto? LastResult,
    // JobRunState name: Active | UserPaused | HeldNoSecurity | NeedsLocation | WaitingForDrive | WaitingForNetwork.
    [property: JsonPropertyName("state")] string State);

/// <summary>
/// One in-flight restore for GET /api/operations/restores — the
/// Banked Vault's active-restores strip renders these with a
/// per-restore Cancel button (item 7, 2026-10-22). Kind is "file"
/// or "disk"; Target is the destination folder or device path the
/// cancel call keys on.
/// </summary>
public sealed record ActiveRestoreDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("backupId")] string BackupId,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("startedLocal")] DateTime StartedLocal);

/// <summary>Response for GET /api/status.</summary>
public sealed record StatusDto(
    [property: JsonPropertyName("startedLocal")] DateTime? StartedLocal,
    [property: JsonPropertyName("jobs")] List<StatusJobDto> Jobs);

/// <summary>
/// Backup entry for GET /api/backups. StoredBytes is the catalogued
/// plaintext size of the distinct chunks the backup's manifest references
/// (see BackupRepository.StoredBytes).
/// </summary>
public sealed record BackupDto(
    [property: JsonPropertyName("backupId")] string BackupId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("createdUtc")] DateTime CreatedUtc,
    [property: JsonPropertyName("storedBytes")] long StoredBytes);

/// <summary>A backup discovered on an SMB share or USB drive.</summary>
public sealed record DiscoveredBackupDto(
    [property: JsonPropertyName("backupId")] string BackupId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("createdUtc")] DateTime? CreatedUtc,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("locationPath")] string LocationPath);

/// <summary>Response for POST /api/locations/{name}/scan and /api/locations/scan-usb.</summary>
public sealed record ScanBackupsResultDto(
    [property: JsonPropertyName("scannedPath")] string ScannedPath,
    [property: JsonPropertyName("found")] int Found,
    [property: JsonPropertyName("unknown")] List<DiscoveredBackupDto> Unknown);

/// <summary>A removable logical drive (USB stick, SD card) for the backup-scan picker.</summary>
public sealed record RemovableDriveDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("serial")] string Serial);

/// <summary>
/// Disk usage for one backup location, GET /api/config/location-disk.
/// Only locations with a disk the service can currently see appear —
/// a plugged-in USB drive, or a network share reachable as the service
/// account. Usenet locations have no disk to measure and never appear.
/// </summary>
public sealed record LocationDiskDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("freeBytes")] long FreeBytes);

/// <summary>Response for POST /api/protection/phone/setup: enrollment data.</summary>
/// <summary>
/// Offline Folders card status. Deliberately a single boolean: when
/// locked, this is the ENTIRE response — no names, no counts, no hint
/// whether a catalog file exists at all.
/// </summary>
public sealed record OfflineFoldersStatusDto(
    [property: JsonPropertyName("locked")] bool Locked);

/// <summary>One Offline Folders catalog entry, as served while unlocked.</summary>
public sealed record OfflineFolderDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("locationId")] string LocationId,
    [property: JsonPropertyName("addedUtc")] string AddedUtc);

/// <summary>One file stored in an offline folder, as served while unlocked.</summary>
public sealed record OfflineFileDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("modifiedUtc")] string ModifiedUtc);

public sealed record PhoneSetupDto(
    [property: JsonPropertyName("otpauthUri")] string OtpauthUri,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("qrSvg")] string QrSvg);

/// <summary>One step of a WinRE build for /api/operations/winre-build-status.</summary>
public sealed record WinReBuildStepDto(
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("percent")] int Percent,
    [property: JsonPropertyName("estimatedRemainingSec")] int EstimatedRemainingSec);

/// <summary>
/// Response for GET /api/operations/winre-prerequisites. (SF-24: this
/// used to be a Dictionary&lt;string, object&gt;, which the trimmed
/// service has no JSON metadata for.)
/// </summary>
public sealed record WinRePrerequisitesDto(
    [property: JsonPropertyName("isWindows")] bool IsWindows,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("isAdmin")] bool IsAdmin,
    [property: JsonPropertyName("reagentc")] bool Reagentc,
    [property: JsonPropertyName("dism")] bool Dism,
    [property: JsonPropertyName("bcdboot")] bool Bcdboot,
    [property: JsonPropertyName("winReEnabled")] bool WinReEnabled,
    [property: JsonPropertyName("winReLocation")] string WinReLocation,
    [property: JsonPropertyName("scriptFound")] bool ScriptFound,
    [property: JsonPropertyName("missing")] List<string> Missing);

/// <summary>
/// Response for GET /api/operations/winre-build-status. (WALK-9: this
/// used to be an anonymous type, which the trimmed service cannot
/// serialize — the endpoint returned HTTP 500 and the dashboard showed
/// no build progress at all.)
/// </summary>
public sealed record WinReBuildStatusDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("drive")] string Drive,
    [property: JsonPropertyName("percent")] int Percent,
    [property: JsonPropertyName("currentStep")] string CurrentStep,
    [property: JsonPropertyName("estimatedRemainingSec")] int EstimatedRemainingSec,
    [property: JsonPropertyName("elapsedSec")] int ElapsedSec,
    [property: JsonPropertyName("steps")] List<WinReBuildStepDto> Steps);
