using FileKeep.Core.Security;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Middle-path secret centralization: the single resolve helper must
/// preserve the pre-existing env-vs-DPAPI precedence exactly. Tests use
/// a dedicated env var name (never the real FILEKEEP_* names) and a
/// fake unprotect function, so they run on Linux where DPAPI throws.
/// </summary>
public sealed class SecretResolverTests
{
    private const string TestEnvVar = "FILEKEEP_TEST_SECRET_RESOLVER";

    private static void WithEnv(string? value, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(TestEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, previous);
        }
    }

    [Fact]
    public void EnvWinsOverBlob_AndBlobIsNeverTouched()
    {
        WithEnv("env-value", () =>
        {
            bool called = false;
            string? result = SecretResolver.ResolvePassword(TestEnvVar, "BLOB",
                _ => { called = true; return "blob-value"; });
            Assert.Equal("env-value", result);
            Assert.False(called);
        });
    }

    [Fact]
    public void BlobFallback_WhenEnvUnset()
    {
        WithEnv(null, () =>
        {
            string? seen = null;
            string? result = SecretResolver.ResolvePassword(TestEnvVar, "BLOB",
                b => { seen = b; return "decrypted"; });
            Assert.Equal("decrypted", result);
            Assert.Equal("BLOB", seen);
        });
    }

    [Fact]
    public void EmptyEnv_FallsThroughToBlob()
    {
        WithEnv("", () =>
        {
            string? result = SecretResolver.ResolvePassword(TestEnvVar, "BLOB", _ => "decrypted");
            Assert.Equal("decrypted", result);
        });
    }

    [Fact]
    public void WhitespaceEnv_Wins_PreservesIsNullOrEmptySemantics()
    {
        // Pre-existing behavior, preserved deliberately: the old code
        // tested IsNullOrEmpty, not IsNullOrWhiteSpace, so a
        // whitespace-only env var wins over the blob. Changing that
        // would change which source wins.
        WithEnv(" ", () =>
        {
            bool called = false;
            string? result = SecretResolver.ResolvePassword(TestEnvVar, "BLOB",
                _ => { called = true; return "blob-value"; });
            Assert.Equal(" ", result);
            Assert.False(called);
        });
    }

    [Fact]
    public void NoSources_ReturnsNull()
    {
        WithEnv(null, () =>
        {
            Assert.Null(SecretResolver.ResolvePassword(TestEnvVar, null, _ => "x"));
            Assert.Null(SecretResolver.ResolvePassword(TestEnvVar, "", _ => "x"));
        });
    }

    [Fact]
    public void UnprotectFailure_PropagatesToCaller()
    {
        // Callers keep their own handling (job failure, 400, or
        // fall-back-to-no-password); the helper never swallows.
        WithEnv(null, () =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                SecretResolver.ResolvePassword(TestEnvVar, "BLOB",
                    _ => throw new InvalidOperationException("decrypt failed")));
        });
    }

    [Fact]
    public void HasSecret_Matrix()
    {
        WithEnv(null, () =>
        {
            Assert.False(SecretResolver.HasSecret(TestEnvVar, null));
            Assert.False(SecretResolver.HasSecret(TestEnvVar, ""));
            Assert.True(SecretResolver.HasSecret(TestEnvVar, "BLOB"));
        });
        WithEnv("env-value", () =>
        {
            Assert.True(SecretResolver.HasSecret(TestEnvVar, null));
            Assert.True(SecretResolver.HasSecret(TestEnvVar, "BLOB"));
        });
        WithEnv("", () =>
        {
            Assert.False(SecretResolver.HasSecret(TestEnvVar, null));
        });
    }
}
