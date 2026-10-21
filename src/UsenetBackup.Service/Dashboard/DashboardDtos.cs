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
    [property: JsonPropertyName("yubiKeyOnly")] bool YubiKeyOnly);

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

/// <summary>Job summary for /api/config response.</summary>
public sealed record ConfigJobDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("repo")] string? Repo,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("schedule")] string? Schedule,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("jobType")] string? JobType,
    [property: JsonPropertyName("backupPrivilege")] bool BackupPrivilege,
    [property: JsonPropertyName("vss")] bool Vss,
    [property: JsonPropertyName("autoUpload")] bool AutoUpload,
    [property: JsonPropertyName("targets")] List<string>? Targets,
    [property: JsonPropertyName("smbShare")] string? SmbShare,
    [property: JsonPropertyName("smbUser")] string? SmbUser,
    [property: JsonPropertyName("hasSmbPassword")] bool HasSmbPassword,
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
    [property: JsonPropertyName("usedBy")] List<string> UsedBy);

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
    [property: JsonPropertyName("error")] string? Error);

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
    // JobRunState name: Active | UserPaused | NeedsLocation | WaitingForDrive.
    [property: JsonPropertyName("state")] string State);

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

/// <summary>Response for POST /api/protection/phone/setup: enrollment data.</summary>
public sealed record PhoneSetupDto(
    [property: JsonPropertyName("otpauthUri")] string OtpauthUri,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("qrSvg")] string QrSvg);
