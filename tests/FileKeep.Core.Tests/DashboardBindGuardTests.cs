using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// D-1: the dashboard has no authentication, so Dashboard.RunAsync must
/// refuse any non-loopback bind loudly instead of silently exposing a
/// LocalSystem-privilege API to the network.
/// </summary>
public sealed class DashboardBindGuardTests
{
    // Facts with case loops rather than Theories: the sandbox reflection
    // runner does not execute Theories, and this classification is the
    // guard's core logic — it must be exercised here too, not only on
    // the Windows suite.
    [Fact]
    public void IsLoopbackBind_AcceptsLoopback()
    {
        foreach (string bind in new[]
        {
            "localhost", "LOCALHOST", "LocalHost",
            "127.0.0.1", "127.0.0.2", "127.255.0.1",
            "::1", "[::1]",
        })
            Assert.True(FileKeep.Core.Service.LoopbackGuard.IsLoopbackBind(bind), $"expected loopback: '{bind}'");
    }

    [Fact]
    public void IsLoopbackBind_RefusesNonLoopback()
    {
        foreach (string bind in new[]
        {
            "0.0.0.0", "::", "[::]", "*", "",
            "192.168.1.10", "10.0.0.5", "172.16.0.1",
            "example.com", "myhost", "localhost.localdomain", "127.0.0.1.evil.com",
        })
            Assert.False(FileKeep.Core.Service.LoopbackGuard.IsLoopbackBind(bind), $"expected refusal: '{bind}'");
    }

    [Fact]
    public async Task RunAsync_RefusesNonLoopbackBind()
    {
        var ex = await RefuseBind("0.0.0.0");
        Assert.Contains("0.0.0.0", ex.Message);
        Assert.Contains("no authentication", ex.Message);
    }

    [Fact]
    public async Task RunAsync_RefusesLanAddressBind()
    {
        var ex = await RefuseBind("192.168.1.10");
        Assert.Contains("192.168.1.10", ex.Message);
    }

    /// <summary>
    /// Drives the production entry point with a bad bind. The guard throws
    /// before any host is built, so this fails fast and cannot hang or
    /// actually bind a socket. One environment caveat: the sandbox
    /// reflection runner hosts the test assembly in a plain .NET process
    /// with no ASP.NET Core shared framework, so merely calling RunAsync
    /// fails JIT with FileNotFoundException (Microsoft.AspNetCore) before
    /// the guard line can execute — skip there; under the real Windows
    /// test host the framework is present and the assertion runs.
    /// </summary>
    private static async Task<InvalidOperationException> RefuseBind(string bind)
    {
        var config = new ServiceConfig
        {
            DashboardBind = bind,
            DashboardPort = 15789,
        };
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(Path.GetTempPath(), "service.json");
        try
        {
            await Dashboard.RunAsync(config, scheduler, configPath, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
        catch (FileNotFoundException ex) when (
            ex.Message.Contains("Microsoft.AspNetCore", StringComparison.Ordinal))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "ASP.NET Core shared framework is not loadable in this test host.");
        }
        throw new InvalidOperationException(
            $"Dashboard.RunAsync accepted the non-loopback bind '{bind}'.");
    }
}
