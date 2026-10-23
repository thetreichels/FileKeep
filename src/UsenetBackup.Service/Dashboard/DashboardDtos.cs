using System.Collections.Generic;
using System.Text.Json.Serialization;
using UsenetBackup.Core.Recovery;

namespace UsenetBackup.Service;

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

public sealed record ActiveProgressDto(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("progress")] UsenetBackup.Core.Service.UploadProgressSnapshot? Progress);

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
    [property: JsonPropertyName("phoneEnabled")] bool PhoneEnabled);

public sealed record ExistsDto(
    [property: JsonPropertyName("exists")] bool Exists);

/// <summary>Passphrase status for /api/passphrase/status.</summary>
public sealed record PassphraseStatusDto(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("yubikey")] bool Yubikey);

public sealed record CancelledDto(
    [property: JsonPropertyName("cancelled")] bool Cancelled);

public sealed record CountDto(
    [property: JsonPropertyName("count")] int Count);

public sealed record HasPasswordDto(
    [property: JsonPropertyName("hasPassword")] bool HasPassword);

/// <summary>Empty response (replaces new { }).</summary>
public sealed record EmptyDto;

/// <summary>Import result for /api/import-remote.</summary>
public sealed record ImportedDto(
    [property: JsonPropertyName("imported")] int Imported);

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

/// <summary>Snapshot of upload progress for /api/upload-progress.</summary>
/// <summary>Single NNTP probe result for /api/config/nntp/probe.</summary>
public sealed record NntpProbeResultDto(
    [property: JsonPropertyName("port")] string Port,
    [property: JsonPropertyName("result")] string Result);

/// <summary>Response for /api/config/nntp/probe.</summary>
public sealed record NntpProbeResponseDto(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("probes")] List<NntpProbeResultDto> Probes,
    [property: JsonPropertyName("fallback")] NntpProbeResultDto? Fallback,
    [property: JsonPropertyName("diagnosis")] string Diagnosis);

/// <summary>Job summary for /api/config response. Jobs carry no
/// credentials or connection details (Phase 6): targets are location
/// names, and everything else lives on the locations.</summary>
public sealed record ConfigJobDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("repo")] string? Repo,
    [property: JsonPropertyName("source")] string? Source,
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
    [property: JsonPropertyName("usbFolder")] string? UsbFolder);

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
    [property: JsonPropertyName("source")] string? Source,
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

/// <summary>Expiring backup entry for /api/expiration.</summary>
public sealed record ExpiringBackupDto(
    [property: JsonPropertyName("backupId")] string BackupId,
    [property: JsonPropertyName("providerHost")] string ProviderHost,
    [property: JsonPropertyName("uploadedUtc")] DateTime UploadedUtc,
    [property: JsonPropertyName("expiresUtc")] DateTime ExpiresUtc,
    [property: JsonPropertyName("daysLeft")] int DaysLeft,
    [property: JsonPropertyName("retentionDays")] int RetentionDays);

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
