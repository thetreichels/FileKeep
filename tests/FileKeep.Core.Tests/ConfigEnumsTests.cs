using System.Text.Json;
using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Enum cleanup (security review rec 2 remainder): redundancyMode,
/// protectionLinkMode, location Type, job Mode, JobType, and
/// RepositoryConfig.ChunkingScheme are typed enums parsed at load,
/// following the VerificationModeParser pattern. JSON transport stays
/// strings. Unknown values are rejected fail-closed at load with the
/// same messages as before (asserted below, not just the exception
/// type); valid configs are unaffected.
/// </summary>
public sealed class ConfigEnumsTests
{
    private static ServiceConfig ValidConfig() => new()
    {
        Jobs = new List<BackupJobConfig>
        {
            new() { Name = "docs", Repo = "r", Sources = new List<string> { "s" }, Schedule = "interval 60" },
        },
    };

    // ---- Parser units ----

    [Fact]
    public void RedundancyParser_MapsKnownValues_EmptyDefaultsToNone()
    {
        Assert.Equal(RedundancyKind.Xor, RedundancyKindParser.Parse("xor"));
        Assert.Equal(RedundancyKind.Par2, RedundancyKindParser.Parse("par2"));
        Assert.Equal(RedundancyKind.None, RedundancyKindParser.Parse("none"));
        Assert.Equal(RedundancyKind.None, RedundancyKindParser.Parse(""));
        Assert.Equal(RedundancyKind.None, RedundancyKindParser.Parse(null));
        Assert.Equal(RedundancyKind.Xor, RedundancyKindParser.Parse(" XOR "));
        Assert.Throws<ArgumentException>(() => RedundancyKindParser.Parse("raid6"));
    }

    [Fact]
    public void LinkParser_MapsKnownValues_EmptyDefaultsToOr()
    {
        Assert.Equal(LinkKind.And, LinkKindParser.Parse("and"));
        Assert.Equal(LinkKind.Or, LinkKindParser.Parse("or"));
        Assert.Equal(LinkKind.Or, LinkKindParser.Parse(""));
        Assert.Equal(LinkKind.Or, LinkKindParser.Parse(null));
        Assert.Equal(LinkKind.And, LinkKindParser.Parse("AND"));
        Assert.Throws<ArgumentException>(() => LinkKindParser.Parse("both"));
    }

    [Fact]
    public void LocationParser_MapsKnownValues_RejectsEmptyAndUnknown()
    {
        Assert.Equal(LocationKind.Nntp, LocationKindParser.Parse("nntp"));
        Assert.Equal(LocationKind.Smb, LocationKindParser.Parse("smb"));
        Assert.Equal(LocationKind.Usb, LocationKindParser.Parse("USB"));
        Assert.Throws<ArgumentException>(() => LocationKindParser.Parse(""));
        Assert.Throws<ArgumentException>(() => LocationKindParser.Parse(null));
        Assert.Throws<ArgumentException>(() => LocationKindParser.Parse("nfs"));
    }

    [Fact]
    public void BackupModeParser_MapsKnownValues_RejectsEmptyAndUnknown()
    {
        Assert.Equal(BackupMode.Incremental, BackupModeParser.Parse("incremental"));
        Assert.Equal(BackupMode.Full, BackupModeParser.Parse("Full"));
        Assert.Throws<ArgumentException>(() => BackupModeParser.Parse(""));
        Assert.Throws<ArgumentException>(() => BackupModeParser.Parse("differential"));
    }

    [Fact]
    public void JobKindParser_MapsKnownValues_RejectsEmptyAndUnknown()
    {
        Assert.Equal(JobKind.Files, JobKindParser.Parse("files"));
        Assert.Equal(JobKind.Disk, JobKindParser.Parse("Disk"));
        Assert.Throws<ArgumentException>(() => JobKindParser.Parse(""));
        Assert.Throws<ArgumentException>(() => JobKindParser.Parse("image"));
    }

    [Fact]
    public void ChunkingParser_MapsKnownValues_RejectsEmptyAndUnknown()
    {
        Assert.Equal(ChunkingKind.Fixed, ChunkingKindParser.Parse("fixed"));
        Assert.Equal(ChunkingKind.Cdc, ChunkingKindParser.Parse("CDC"));
        Assert.Throws<ArgumentException>(() => ChunkingKindParser.Parse(""));
        Assert.Throws<ArgumentException>(() => ChunkingKindParser.Parse("zstd"));
    }

    // ---- Accessors + JSON transport stays strings ----

    [Fact]
    public void Accessors_ExposeParsedValues_CaseInsensitive()
    {
        Assert.Equal(LocationKind.Usb, new BackupLocation { Type = "USB" }.ParsedKind);
        Assert.Equal(BackupMode.Full, new BackupJobConfig { Mode = "Full" }.ParsedMode);
        Assert.Equal(JobKind.Disk, new BackupJobConfig { JobType = "DISK" }.ParsedJobKind);
        Assert.Equal(RedundancyKind.Xor, new NntpConfig { RedundancyMode = "xor" }.ParsedRedundancy);
        Assert.Equal(RedundancyKind.None, new BackupLocation { RedundancyMode = "" }.ParsedRedundancy);
        Assert.Equal(LinkKind.And, new ServiceConfig { ProtectionLinkMode = "and" }.ParsedLink);
        Assert.Equal(ChunkingKind.Cdc, new RepositoryConfig { ChunkingScheme = "cdc" }.ParsedScheme);
    }

    [Fact]
    public void JsonTransport_StaysStrings_ParsedAccessorsIgnored()
    {
        var job = new BackupJobConfig { Name = "docs", Mode = "full", JobType = "disk", RedundancyMode = "xor" };
        string json = JsonSerializer.Serialize(job);
        Assert.Contains("\"mode\":\"full\"", json);
        Assert.Contains("\"jobType\":\"disk\"", json);
        Assert.Contains("\"redundancyMode\":\"xor\"", json);
        Assert.DoesNotContain("ParsedMode", json);
        Assert.DoesNotContain("ParsedJobKind", json);
        var roundTrip = JsonSerializer.Deserialize<BackupJobConfig>(json)!;
        Assert.Equal("full", roundTrip.Mode);
        Assert.Equal(BackupMode.Full, roundTrip.ParsedMode);
    }

    [Fact]
    public void HasPassword_MixedCaseType_UsesParsedKind()
    {
        // The old string comparison fell through to the usb branch
        // (true) for a mixed-case "SMB" with no password configured.
        // The parsed kind reads it as SMB, so no password is reported.
        string? savedSmb = Environment.GetEnvironmentVariable("FILEKEEP_SMB_PASSWORD");
        Environment.SetEnvironmentVariable("FILEKEEP_SMB_PASSWORD", null);
        try
        {
            var loc = new BackupLocation { Type = "SMB", Share = @"\\server\share" };
            Assert.False(loc.HasPassword);
            Assert.True(new BackupLocation { Type = "USB", VolumeSerial = "ABC" }.HasPassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FILEKEEP_SMB_PASSWORD", savedSmb);
        }
    }

    // ---- Load-time rejection (messages preserved) ----

    [Fact]
    public void Validate_LocationUnknownType_Rejected()
    {
        var config = ValidConfig();
        config.Locations.Add(new BackupLocation { Name = "x", Type = "nfs" });
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("has unknown type 'nfs'", ex.Message);
    }

    [Fact]
    public void Validate_LocationNullType_RejectedAsLoadError_NotNullReference()
    {
        var config = ValidConfig();
        config.Locations.Add(new BackupLocation { Name = "x", Type = null! });
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("has unknown type", ex.Message);
    }

    [Fact]
    public void Validate_LocationMixedCaseType_Loads()
    {
        var config = ValidConfig();
        config.Locations.Add(new BackupLocation { Name = "u", Type = "USB", VolumeSerial = "ABC" });
        config.Validate();
        Assert.Equal(LocationKind.Usb, config.Locations[0].ParsedKind);
    }

    [Fact]
    public void Validate_LocationBadRedundancy_Rejected()
    {
        var config = ValidConfig();
        config.Locations.Add(new BackupLocation
            { Name = "s", Type = "smb", Share = @"\\server\share", RedundancyMode = "raid6" });
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("has unknown redundancyMode 'raid6'", ex.Message);
    }

    [Fact]
    public void Validate_JobBadMode_Rejected()
    {
        var config = ValidConfig();
        config.Jobs[0].Mode = "differential";
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("has unknown mode 'differential'", ex.Message);
    }

    [Fact]
    public void Validate_JobBadJobType_Rejected()
    {
        var config = ValidConfig();
        config.Jobs[0].JobType = "image";
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("has unknown jobType 'image'", ex.Message);
    }

    [Fact]
    public void Validate_JobEmptyRedundancy_Rejected()
    {
        var config = ValidConfig();
        config.Jobs[0].RedundancyMode = "";
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("has unknown redundancyMode ''", ex.Message);
    }

    [Fact]
    public void Validate_JobBadRedundancy_Rejected()
    {
        var config = ValidConfig();
        config.Jobs[0].RedundancyMode = "raid6";
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("has unknown redundancyMode 'raid6'", ex.Message);
    }

    [Fact]
    public void Validate_JobMixedCaseValues_Load()
    {
        // Case-insensitive parsing (the VerificationModeParser pattern)
        // loosens two previously case-sensitive rejections: "Full" and
        // "XOR" failed to load before and now load and behave as their
        // canonical modes. Unknown values are still rejected (above).
        var config = ValidConfig();
        config.Jobs[0].Mode = "Full";
        config.Jobs[0].RedundancyMode = "XOR";
        config.Validate();
        Assert.Equal(BackupMode.Full, config.Jobs[0].ParsedMode);
        Assert.Equal(RedundancyKind.Xor, config.Jobs[0].ParsedRedundancy);
    }

    [Fact]
    public void Validate_BadLinkMode_Rejected_EmptyLoads()
    {
        var bad = ValidConfig();
        bad.ProtectionLinkMode = "both";
        var ex = Assert.Throws<InvalidOperationException>(() => bad.Validate());
        Assert.Contains("protectionLinkMode 'both' is invalid", ex.Message);

        var empty = ValidConfig();
        empty.ProtectionLinkMode = "";
        empty.Validate();
        Assert.Equal(LinkKind.Or, empty.ParsedLink);
    }

    [Fact]
    public void Validate_ProviderBadRedundancy_Rejected()
    {
        var config = ValidConfig();
        config.NntpProviders.Add(new NntpConfig { Host = "news.example", RedundancyMode = "raid6" });
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("has unknown redundancyMode 'raid6'", ex.Message);
    }

    [Fact]
    public void Validate_GlobalNntpBadRedundancy_Rejected()
    {
        // New rejection (previously the global entry's redundancy was
        // never validated and garbage silently behaved as "none" in
        // the uploader — the SF-22 silent-none bug, un-fixed for the
        // global entry). Blank-host placeholders are still tolerated.
        var config = ValidConfig();
        config.Nntp = new NntpConfig { Host = "news.example", RedundancyMode = "raid6" };
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("nntp.redundancyMode 'raid6' is invalid", ex.Message);

        var placeholder = ValidConfig();
        placeholder.Nntp = new NntpConfig { Host = "", RedundancyMode = "raid6" };
        placeholder.Validate();
    }

    // ---- Repository open ----

    [Fact]
    public void Open_MixedCaseChunking_Loads_BadChunkingRejectedWithOriginalMessage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fk-enum-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var _ = BackupRepository.Init(dir, "test-pass-123", kdfIterations: 1000);
            string repoJson = Path.Combine(dir, "repo.json");
            var loaded = JsonSerializer.Deserialize(
                File.ReadAllText(repoJson), FileKeep.Core.Json.FileKeepJsonContext.Default.RepositoryConfig)!;
            loaded.ChunkingScheme = "CDC";
            File.WriteAllText(repoJson, JsonSerializer.Serialize(
                loaded, FileKeep.Core.Json.FileKeepJsonContext.Default.RepositoryConfig));
            using var opened = BackupRepository.Open(dir, "test-pass-123");
            // CDC parses (not Fixed): the max chunk is 4x the target average.
            Assert.Equal(loaded.ChunkSize * 4, opened.EffectiveMaxChunkSize);

            loaded.ChunkingScheme = "zstd";
            File.WriteAllText(repoJson, JsonSerializer.Serialize(
                loaded, FileKeep.Core.Json.FileKeepJsonContext.Default.RepositoryConfig));
            var ex = Assert.Throws<NotSupportedException>(() => BackupRepository.Open(dir, "test-pass-123"));
            Assert.Contains("Repository chunking scheme 'zstd' is not supported", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
