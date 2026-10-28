using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Startup-error sanitization (security review rec 3): fake secret
/// values must never survive into a log line, while paths and
/// failure kinds are preserved. All fixtures are fake values invented
/// for these tests — never real secrets.
/// </summary>
public sealed class StartupErrorSanitizerTests
{
    [Fact]
    public void FakeDpapiBlob_Redacted_PathPreserved()
    {
        // A DPAPI blob is a long base64 string; the config path beside
        // it must survive (separators break the base64 run).
        string blob = new string('Q', 200);
        string message = $"Cannot read service config 'C:\\ProgramData\\FileKeep\\service.json': {blob}";
        string sanitized = StartupErrorSanitizer.Sanitize(message);
        Assert.DoesNotContain(blob, sanitized);
        Assert.Contains(@"C:\ProgramData\FileKeep\service.json", sanitized);
        Assert.Contains("[redacted]", sanitized);
    }

    [Fact]
    public void FakeGeneratedSecret_Redacted()
    {
        // 52-char base32, the generated-secret shape (26 letters +
        // 6 digits, then 20 more letters).
        string secret = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRST";
        Assert.Equal(52, secret.Length);
        string sanitized = StartupErrorSanitizer.Sanitize($"unlock failed for {secret}");
        Assert.DoesNotContain(secret, sanitized);
    }

    [Fact]
    public void FakeTotpSecret_Redacted()
    {
        // 32-char base32, the TOTP-secret shape (26 letters + 6 digits).
        string secret = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        Assert.Equal(32, secret.Length);
        string sanitized = StartupErrorSanitizer.Sanitize($"totp secret {secret} leaked?");
        Assert.DoesNotContain(secret, sanitized);
    }

    [Fact]
    public void KeyValueSecret_Redacted_KeyPreserved()
    {
        string message = "env dump: FILEKEEP_NNTP_PASSWORD=F4k3P455w0rd!";
        string sanitized = StartupErrorSanitizer.Sanitize(message);
        Assert.DoesNotContain("F4k3P455w0rd!", sanitized);
        Assert.Contains("FILEKEEP_NNTP_PASSWORD=", sanitized);
        Assert.Contains("[redacted]", sanitized);
    }

    [Fact]
    public void NormalMessage_Unchanged()
    {
        string message = "Cannot read service config 'C:\\ProgramData\\FileKeep\\service.json': Access to the path is denied.";
        Assert.Equal(message, StartupErrorSanitizer.Sanitize(message));
    }

    [Fact]
    public void NullAndEmpty_PassThrough()
    {
        Assert.Equal("", StartupErrorSanitizer.Sanitize(null));
        Assert.Equal("", StartupErrorSanitizer.Sanitize(""));
    }

    [Fact]
    public void ShortToken_Preserved_DocumentsThreshold()
    {
        // Pattern redaction cannot catch short secrets: a 16-char
        // token is below the 32-char run threshold and survives.
        // That is the documented limit — call sites must never
        // interpolate user secrets into messages in the first place.
        string message = "token abcdefghijklmnop rejected";
        Assert.Equal(message, StartupErrorSanitizer.Sanitize(message));
    }
}
