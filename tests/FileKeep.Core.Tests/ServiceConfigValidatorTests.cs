using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Config-lifecycle split, Stage 1: validation lives in
/// <see cref="ServiceConfigValidator"/> and <see cref="ServiceConfig.Validate"/>
/// only delegates. These tests construct DTOs directly (no file IO) and
/// pin every rejection the moved code makes, with the exact messages —
/// so a future move that changes a rule or a message fails here, not at
/// load time. Cases mirror the pre-move behavior; the mode-matrix cells
/// come from the gaps work (phone secondary-only, "and" needs both
/// primaries). The VSS-on-Linux rule is deliberately untested: it is
/// platform-dependent and would fail the Windows suite.
/// </summary>
public sealed class ServiceConfigValidatorTests
{
    private static BackupJobConfig ValidJob() => new()
    {
        Name = "docs",
        Repo = "r",
        Sources = new List<string> { "s" },
        Schedule = "interval 60",
    };

    private static ServiceConfig ConfigWithJob(BackupJobConfig job) => new()
    {
        Jobs = new List<BackupJobConfig> { job },
    };

    private static ServiceConfig ValidConfig() => ConfigWithJob(ValidJob());

    private static InvalidOperationException Rejects(ServiceConfig config)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfigValidator.Validate(config));
        return ex;
    }

    private static void RejectsWith(ServiceConfig config, string fragment) =>
        Assert.Contains(fragment, Rejects(config).Message);

    // ---- Facade delegation ----

    [Fact]
    public void Facade_Validate_Delegates_ValidPassesAndRejectsSameMessage()
    {
        ValidConfig().Validate();
        var bad = ValidConfig();
        bad.DashboardPort = 0;
        var viaValidator = Assert.Throws<InvalidOperationException>(() => ServiceConfigValidator.Validate(bad));
        var viaFacade = Assert.Throws<InvalidOperationException>(() => bad.Validate());
        Assert.Equal(viaValidator.Message, viaFacade.Message);
    }

    [Fact]
    public void Facade_ValidateNoNestedSources_Delegates()
    {
        var job = ValidJob();
        job.Sources = new List<string> { "/tmp/fk-val-a", "/tmp/fk-val-a/sub" };
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfig.ValidateNoNestedSources(job));
        Assert.Contains("inside", ex.Message);
    }

    // ---- Top-level fields ----

    [Fact]
    public void TopLevel_BadDashboardPort_Rejected()
    {
        var zero = ValidConfig();
        zero.DashboardPort = 0;
        RejectsWith(zero, "dashboardPort 0 is out of range.");
        var big = ValidConfig();
        big.DashboardPort = 70000;
        RejectsWith(big, "dashboardPort 70000 is out of range.");
    }

    [Fact]
    public void TopLevel_EmptyDashboardBind_Rejected()
    {
        var config = ValidConfig();
        config.DashboardBind = "   ";
        RejectsWith(config, "dashboardBind must not be empty.");
    }

    [Fact]
    public void TopLevel_NegativeRetentionInterval_Rejected()
    {
        var config = ValidConfig();
        config.RetentionCheckIntervalHours = -1;
        RejectsWith(config, "retentionCheckIntervalHours -1 is invalid");
    }

    [Fact]
    public void TopLevel_EmptyConfig_Passes()
    {
        ServiceConfigValidator.Validate(new ServiceConfig());
    }

    // ---- Locations ----

    [Fact]
    public void Location_NoName_Rejected()
    {
        var config = ValidConfig();
        config.Locations = new List<BackupLocation> { new() { Type = "usb", VolumeSerial = "ABC" } };
        RejectsWith(config, "A backup location has no name.");
    }

    [Fact]
    public void Location_DuplicateName_Rejected_CaseInsensitive()
    {
        var config = ValidConfig();
        config.Locations = new List<BackupLocation>
        {
            new() { Name = "Stick", Type = "usb", VolumeSerial = "ABC" },
            new() { Name = "stick", Type = "usb", VolumeSerial = "DEF" },
        };
        RejectsWith(config, "Duplicate location name 'stick'.");
    }

    [Fact]
    public void Location_UnknownType_Rejected()
    {
        var config = ValidConfig();
        config.Locations = new List<BackupLocation> { new() { Name = "x", Type = "nfs" } };
        RejectsWith(config, "Location 'x' has unknown type 'nfs'");
    }

    [Fact]
    public void Location_Nntp_FieldsChecked()
    {
        var noHost = ValidConfig();
        noHost.Locations = new List<BackupLocation> { new() { Name = "p", Type = "nntp", Port = 563 } };
        RejectsWith(noHost, "Location 'p' (nntp) has no host.");

        var badPort = ValidConfig();
        badPort.Locations = new List<BackupLocation> { new() { Name = "p", Type = "nntp", Host = "h", Port = 0 } };
        RejectsWith(badPort, "Location 'p' port 0 is out of range.");

        var badConn = ValidConfig();
        badConn.Locations = new List<BackupLocation> { new() { Name = "p", Type = "nntp", Host = "h", Port = 563, Connections = 101 } };
        RejectsWith(badConn, "Location 'p' connections must be between 1 and 100.");
    }

    [Fact]
    public void Location_Smb_NoShare_Rejected()
    {
        var config = ValidConfig();
        config.Locations = new List<BackupLocation> { new() { Name = "s", Type = "smb" } };
        RejectsWith(config, "Location 's' (smb) has no share path.");
    }

    [Fact]
    public void Location_Usb_NoSerial_Rejected()
    {
        var config = ValidConfig();
        config.Locations = new List<BackupLocation> { new() { Name = "u", Type = "usb" } };
        RejectsWith(config, "Location 'u' (usb) has no volume serial.");
    }

    [Fact]
    public void Location_UnknownRedundancyMode_Rejected()
    {
        var config = ValidConfig();
        config.Locations = new List<BackupLocation>
            { new() { Name = "u", Type = "usb", VolumeSerial = "ABC", RedundancyMode = "raid6" } };
        RejectsWith(config, "Location 'u' has unknown redundancyMode 'raid6'");
    }

    // ---- Jobs ----

    [Fact]
    public void Job_NoName_DuplicateName_NoRepo_NoSources_Rejected()
    {
        var noName = ConfigWithJob(new BackupJobConfig { Repo = "r", Sources = new List<string> { "s" }, Schedule = "interval 60" });
        RejectsWith(noName, "A job has no name.");

        var dup = new ServiceConfig
        {
            Jobs = new List<BackupJobConfig> { ValidJob(), ValidJob() },
        };
        RejectsWith(dup, "Duplicate job name 'docs'.");

        var noRepo = ConfigWithJob(new BackupJobConfig { Name = "j", Sources = new List<string> { "s" }, Schedule = "interval 60" });
        RejectsWith(noRepo, "Job 'j' has no repo path.");

        var noSources = ConfigWithJob(new BackupJobConfig { Name = "j", Repo = "r", Schedule = "interval 60" });
        RejectsWith(noSources, "Job 'j' has no source folder.");
    }

    [Fact]
    public void Job_DiskImage_MultipleSources_Rejected()
    {
        var job = ValidJob();
        job.JobType = "disk";
        job.Sources = new List<string> { "s1", "s2" };
        RejectsWith(ConfigWithJob(job), "disk-image jobs back up a single device.");
    }

    [Fact]
    public void Job_UnknownMode_UnknownJobType_Rejected()
    {
        var badMode = ConfigWithJob(ValidJob());
        badMode.Jobs[0].Mode = "differential";
        RejectsWith(badMode, "Job 'docs' has unknown mode 'differential'");

        var badKind = ConfigWithJob(ValidJob());
        badKind.Jobs[0].JobType = "image";
        RejectsWith(badKind, "Job 'docs' has unknown jobType 'image'");
    }

    [Fact]
    public void Job_VssAndBackupPrivilege_Rejected()
    {
        var job = ValidJob();
        job.Vss = true;
        job.BackupPrivilege = true;
        RejectsWith(ConfigWithJob(job), "'vss' and 'backupPrivilege' are mutually exclusive.");
    }

    [Fact]
    public void Job_RedundancyAndVerificationModes_Checked()
    {
        var emptyRed = ConfigWithJob(ValidJob());
        emptyRed.Jobs[0].RedundancyMode = "";
        RejectsWith(emptyRed, "Job 'docs' has unknown redundancyMode ''");

        var badRed = ConfigWithJob(ValidJob());
        badRed.Jobs[0].RedundancyMode = "raid6";
        RejectsWith(badRed, "Job 'docs' has unknown redundancyMode 'raid6'");

        var badVer = ConfigWithJob(ValidJob());
        badVer.Jobs[0].VerificationMode = "bogus";
        RejectsWith(badVer, "Unknown verification mode 'bogus'");

        var xorSmall = ConfigWithJob(ValidJob());
        xorSmall.Jobs[0].RedundancyMode = "xor";
        xorSmall.Jobs[0].ParityGroupSize = 1;
        RejectsWith(xorSmall, "parityGroupSize must be at least 2");

        var xorOk = ConfigWithJob(ValidJob());
        xorOk.Jobs[0].RedundancyMode = "xor";
        xorOk.Jobs[0].ParityGroupSize = 2;
        ServiceConfigValidator.Validate(xorOk);
    }

    [Fact]
    public void Job_XorViaLocation_StillNeedsParityGroupSize()
    {
        var config = ValidConfig();
        config.Locations = new List<BackupLocation>
            { new() { Name = "u", Type = "usb", VolumeSerial = "ABC", RedundancyMode = "xor" } };
        config.Jobs[0].ParityGroupSize = 1;
        RejectsWith(config, "parityGroupSize must be at least 2");
    }

    [Fact]
    public void Job_EmptySchedule_Rejected()
    {
        var job = ValidJob();
        job.Schedule = "";
        RejectsWith(ConfigWithJob(job), "Job schedule is empty");
    }

    [Fact]
    public void Job_Targets_MustBeKnownLocations()
    {
        var unknown = ConfigWithJob(ValidJob());
        unknown.Jobs[0].Targets = new List<string> { "nope" };
        RejectsWith(unknown, "Job 'docs' targets unknown backup location 'nope'.");

        var known = ValidConfig();
        known.Locations = new List<BackupLocation> { new() { Name = "stick", Type = "usb", VolumeSerial = "ABC" } };
        known.Jobs[0].Targets = new List<string> { "stick" };
        ServiceConfigValidator.Validate(known);
    }

    // ---- Global NNTP + providers ----

    [Fact]
    public void GlobalNntp_HostBearingEntries_Checked_BlankHostTolerated()
    {
        var badPort = ValidConfig();
        badPort.Nntp = new NntpConfig { Host = "h", Port = 0 };
        RejectsWith(badPort, "nntp.port 0 is out of range.");

        var badConn = ValidConfig();
        badConn.Nntp = new NntpConfig { Host = "h", Port = 563, Connections = 0 };
        RejectsWith(badConn, "nntp.connections must be between 1 and 100.");

        var badRed = ValidConfig();
        badRed.Nntp = new NntpConfig { Host = "h", Port = 563, RedundancyMode = "raid6" };
        RejectsWith(badRed, "nntp.redundancyMode 'raid6' is invalid");

        // Blank-host globals are placeholders (uploads skip them): even
        // garbage fields on one are tolerated.
        var placeholder = ValidConfig();
        placeholder.Nntp = new NntpConfig { Host = "", Port = 0, RedundancyMode = "raid6" };
        ServiceConfigValidator.Validate(placeholder);
    }

    [Fact]
    public void Providers_BlankHostSkipped_HostBearingChecked()
    {
        var skipped = ValidConfig();
        skipped.NntpProviders = new List<NntpConfig> { new() { Host = "", Port = 0, RedundancyMode = "raid6" } };
        ServiceConfigValidator.Validate(skipped);

        var badPort = ValidConfig();
        badPort.NntpProviders = new List<NntpConfig> { new() { Host = "h", Port = 0 } };
        RejectsWith(badPort, "nntpProviders entry 'h' port 0 is out of range.");

        var badRed = ValidConfig();
        badRed.NntpProviders = new List<NntpConfig> { new() { Host = "h", Port = 563, RedundancyMode = "raid6" } };
        RejectsWith(badRed, "nntpProviders entry 'h' has unknown redundancyMode 'raid6'");
    }

    // ---- Protection link mode + method matrix (gaps work) ----

    [Fact]
    public void LinkMode_Unknown_Rejected()
    {
        var config = ValidConfig();
        config.ProtectionLinkMode = "both";
        RejectsWith(config, "protectionLinkMode 'both' is invalid");
    }

    [Fact]
    public void Matrix_PhoneOnly_Rejected()
    {
        var config = ValidConfig();
        config.PhoneAuthEnabled = true;
        RejectsWith(config, "Phone authenticator cannot be the only enabled method");
    }

    [Fact]
    public void Matrix_And_RequiresBothPrimaries()
    {
        var passOnly = ValidConfig();
        passOnly.ProtectionLinkMode = "and";
        passOnly.PassphraseProtected = "blob";
        RejectsWith(passOnly, "\"and\" link mode requires both Passphrase and YubiKey");

        var ykOnly = ValidConfig();
        ykOnly.ProtectionLinkMode = "and";
        ykOnly.PassphraseYubiKey = true;
        RejectsWith(ykOnly, "\"and\" link mode requires both Passphrase and YubiKey");

        var halfWrap = ValidConfig();
        halfWrap.ProtectionLinkMode = "and";
        halfWrap.PassphraseWrapProtected = "wrap";
        halfWrap.PassphraseYubiKey = true;
        RejectsWith(halfWrap, "\"and\" link mode requires both Passphrase and YubiKey");
    }

    [Fact]
    public void Matrix_And_WithBothPrimaries_Passes()
    {
        var both = ValidConfig();
        both.ProtectionLinkMode = "and";
        both.PassphraseProtected = "blob";
        both.PassphraseYubiKey = true;
        ServiceConfigValidator.Validate(both);

        var wrapBoth = ValidConfig();
        wrapBoth.ProtectionLinkMode = "and";
        wrapBoth.PassphraseWrapProtected = "wrap";
        wrapBoth.PassphraseWrapSaltB64 = "salt";
        wrapBoth.PassphraseYubiKey = true;
        ServiceConfigValidator.Validate(wrapBoth);
    }

    [Fact]
    public void Matrix_Or_WithPhonePlusPrimary_Passes()
    {
        var config = ValidConfig();
        config.PhoneAuthEnabled = true;
        config.PassphraseProtected = "blob";
        ServiceConfigValidator.Validate(config);
    }

    // ---- Nested sources (validator direct; no file IO — paths need not exist) ----

    [Fact]
    public void NestedSources_Duplicate_Rejected()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "fk-val-" + Guid.NewGuid().ToString("N"));
        var job = ValidJob();
        job.Sources = new List<string> { baseDir, baseDir };
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfigValidator.ValidateNoNestedSources(job));
        Assert.Contains("twice", ex.Message);
    }

    [Fact]
    public void NestedSources_Inside_Rejected()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "fk-val-" + Guid.NewGuid().ToString("N"));
        var job = ValidJob();
        job.Sources = new List<string> { baseDir, Path.Combine(baseDir, "sub") };
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfigValidator.ValidateNoNestedSources(job));
        Assert.Contains("inside", ex.Message);
    }

    [Fact]
    public void NestedSources_Siblings_Pass()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "fk-val-" + Guid.NewGuid().ToString("N"));
        var job = ValidJob();
        job.Sources = new List<string> { Path.Combine(baseDir, "a"), Path.Combine(baseDir, "b") };
        ServiceConfigValidator.ValidateNoNestedSources(job);
    }

    [Fact]
    public void NestedSources_RejectedThroughFullValidate()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "fk-val-" + Guid.NewGuid().ToString("N"));
        var job = ValidJob();
        job.Sources = new List<string> { baseDir, Path.Combine(baseDir, "sub") };
        RejectsWith(ConfigWithJob(job), "inside");
    }
}
