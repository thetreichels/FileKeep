using UsenetBackup.Core.Recovery;
using UsenetBackup.Core.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Security audit D-2 (Phase 4): NNTP must be TLS by default everywhere a
/// default exists — a probe captured AUTHINFO PASS in cleartext because
/// the config models defaulted to plaintext. These tests pin the new
/// defaults AND the stored-config contract: a service.json that
/// explicitly records plaintext keeps it (the value is the user's
/// choice); only omitted fields fall back to the TLS defaults.
/// </summary>
public sealed class TlsByDefaultTests : IDisposable
{
    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "ub-tls-" + Guid.NewGuid().ToString("N"));

    public TlsByDefaultTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private ServiceConfig Load(string json)
    {
        string path = Path.Combine(_workDir, "service.json");
        File.WriteAllText(path, json);
        return ServiceConfig.Load(path);
    }

    [Fact]
    public void NntpConfig_FreshInstance_DefaultsToTls()
    {
        var cfg = new NntpConfig();
        Assert.True(cfg.Ssl);
        Assert.Equal(563, cfg.Port);
    }

    [Fact]
    public void BackupLocation_FreshInstance_DefaultsToTls()
    {
        var loc = new BackupLocation { Name = "p", Type = "nntp" };
        Assert.True(loc.Ssl);
        Assert.Equal(563, loc.Port);
    }

    [Fact]
    public void WizardState_FreshInstance_DefaultsToTls()
    {
        var state = new WizardState();
        Assert.True(state.NntpSsl);
        Assert.Equal(563, state.NntpPort);
    }

    [Fact]
    public void Config_OmittedSslAndPort_LoadsAsTls()
    {
        var config = Load("""{ "nntp": { "host": "news.example.com", "username": "u" } }""");
        Assert.NotNull(config.Nntp);
        Assert.True(config.Nntp!.Ssl);
        Assert.Equal(563, config.Nntp.Port);
        // Since Phase 6 removed the load-time location migration, no
        // locations are derived from the top-level shape. (Locations
        // written to the file parse with the same TLS defaults — see
        // Location_OmittedSslAndPort_LoadsAsTls.)
        Assert.Empty(config.Locations);
    }

    [Fact]
    public void Location_OmittedSslAndPort_LoadsAsTls()
    {
        var config = Load("""
            { "locations": [
                { "name": "prov", "type": "nntp", "host": "news.example.com" } ] }
            """);
        var loc = Assert.Single(config.Locations);
        Assert.True(loc.Ssl);
        Assert.Equal(563, loc.Port);
    }

    [Fact]
    public void Config_ExplicitPlaintext_LoadsUnchanged()
    {
        // Stored-config semantics: an explicit false/119 in service.json
        // is a recorded user choice, not a default — load must not
        // rewrite it to TLS. (The location checked here is one written
        // to the file; the load-time migration that used to derive an
        // "Usenet" location from the top-level shape was removed in
        // Phase 6.)
        var config = Load("""
            {
              "nntp": { "host": "news.example.com", "port": 119, "ssl": false },
              "locations": [
                { "name": "plain", "type": "nntp", "host": "news.other.example",
                  "port": 119, "ssl": false } ]
            }
            """);
        Assert.NotNull(config.Nntp);
        Assert.False(config.Nntp!.Ssl);
        Assert.Equal(119, config.Nntp.Port);
        var loc = Assert.Single(config.Locations, l => l.Name == "plain");
        Assert.False(loc.Ssl);
        Assert.Equal(119, loc.Port);
    }
}
