using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using UsenetBackup.Core.Recovery;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Service;

/// <summary>
/// Source-generated JSON serialization metadata for dashboard API DTOs.
/// Separate from FileKeepJsonContext (which lives in Core) to avoid
/// circular dependencies. Combined via TypeInfoResolverChain in Dashboard.cs.
/// </summary>
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(OkDto))]
[JsonSerializable(typeof(OkErrorDto))]
[JsonSerializable(typeof(OkMessageDto))]
[JsonSerializable(typeof(OkDetailDto))]
[JsonSerializable(typeof(OkYubiKeyDto))]
[JsonSerializable(typeof(YubiKeySetupGeneratedDto))]
[JsonSerializable(typeof(AcceptedDto))]
[JsonSerializable(typeof(AcceptedReposDto))]
[JsonSerializable(typeof(ActiveDto))]
[JsonSerializable(typeof(ActiveProgressDto))]
[JsonSerializable(typeof(YubiKeyStatusDto))]
[JsonSerializable(typeof(YubiKeyUnlockDto))]
[JsonSerializable(typeof(ExistsDto))]
[JsonSerializable(typeof(PassphraseStatusDto))]
[JsonSerializable(typeof(CancelledDto))]
[JsonSerializable(typeof(CountDto))]
[JsonSerializable(typeof(HasPasswordDto))]
[JsonSerializable(typeof(EmptyDto))]
[JsonSerializable(typeof(ImportedDto))]
[JsonSerializable(typeof(FolderDto))]
[JsonSerializable(typeof(List<FolderDto>))]
[JsonSerializable(typeof(BrowseFoldersDto))]
[JsonSerializable(typeof(NntpProbeResultDto))]
[JsonSerializable(typeof(NntpProbeResponseDto))]
[JsonSerializable(typeof(List<NntpProbeResultDto>))]
[JsonSerializable(typeof(ConfigJobDto))]
[JsonSerializable(typeof(List<ConfigJobDto>))]
[JsonSerializable(typeof(ConfigNntpDto))]
[JsonSerializable(typeof(ConfigNntpProviderDto))]
[JsonSerializable(typeof(List<ConfigNntpProviderDto>))]
[JsonSerializable(typeof(ConfigLocationDto))]
[JsonSerializable(typeof(List<ConfigLocationDto>))]
[JsonSerializable(typeof(ConfigSnapshotDto))]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(StatusJobDto))]
[JsonSerializable(typeof(List<StatusJobDto>))]
[JsonSerializable(typeof(StatusLastResultDto))]
[JsonSerializable(typeof(ExpiringBackupDto))]
[JsonSerializable(typeof(List<ExpiringBackupDto>))]
[JsonSerializable(typeof(BackupDto))]
[JsonSerializable(typeof(BackupDto[]))]
[JsonSerializable(typeof(PhysicalDrives.PhysicalDrive))]
[JsonSerializable(typeof(PhysicalDrives.PhysicalDrive[]))]
[JsonSerializable(typeof(UsbDrives.UsbDrive))]
[JsonSerializable(typeof(UsbDrives.UsbDrive[]))]
[JsonSerializable(typeof(RemovableDriveDto))]
[JsonSerializable(typeof(RemovableDriveDto[]))]
[JsonSerializable(typeof(PhoneSetupDto))]
[JsonSerializable(typeof(UploadProgressSnapshot))]
[JsonSerializable(typeof(JsonElement))]
public partial class DashboardJsonContext : JsonSerializerContext
{
}