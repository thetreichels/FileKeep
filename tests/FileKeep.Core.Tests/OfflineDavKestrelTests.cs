using System.Net.Sockets;
using System.Text;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace FileKeep.Core.Tests;

/// <summary>
/// Through-real-Kestrel regression test for the DAV sync-I/O finding:
/// <see cref="OfflineChunkStore"/> reads/writes the HTTP body stream
/// synchronously, and Kestrel disallows sync I/O by default — a
/// <c>Task.Run</c> wrapper does not bypass the ban. The production DAV
/// route opts each request in via <c>IHttpBodyControlFeature</c>; these
/// tests exercise the real production route registration
/// (<see cref="Dashboard.MapOfflineDavRoutes"/>) over a real Kestrel
/// socket, so removing the opt-in fails them. The older handler-level
/// tests drive <c>BodyWriter</c> against a <c>MemoryStream</c> and cannot
/// catch this. Skips gracefully where loopback TCP is intercepted
/// (sandbox); runs for real in the Windows suite.
/// </summary>
public sealed class OfflineDavKestrelTests : IDisposable
{
    private readonly string _workDir;
    private const string Secret = "the-card-secret";
    private static readonly byte[] Report = Encoding.UTF8.GetBytes("quarterly report bytes");
    private static readonly byte[] Upload = Encoding.UTF8.GetBytes("kestrel-upload bytes");

    public OfflineDavKestrelTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "fk-davk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private OfflineFoldersDavHandler SetupHandler()
    {
        string share = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(share);
        var config = new ServiceConfig();
        config.Locations.Add(new BackupLocation { Name = "nas", Type = "smb", Share = share });
        string catalog = Path.Combine(_workDir, "offline.json");
        var session = new OfflineFoldersSession();
        session.Unlock(OfflineFoldersCatalog.DeriveKey(Secret),
            OfflineFileStore.DeriveContentKey(Secret),
            new List<OfflineFolderEntry> { new("Vault", "nas", DateTimeOffset.UtcNow) });
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "docs/report.txt", new MemoryStream(Report), DateTimeOffset.UtcNow).Status);
        return new OfflineFoldersDavHandler(config, session, catalog);
    }

    private static async Task<(WebApplication App, string Base, int Port)> StartServer(OfflineFoldersDavHandler handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        Dashboard.MapOfflineDavRoutes(app, handler);
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        int port = new Uri(address).Port;
        return (app, address.TrimEnd('/'), port);
    }

    /// <summary>Runs <paramref name="clientWork"/> against a real Kestrel
    /// instance serving the production DAV routes. An HTTP probe first:
    /// where loopback is intercepted the probe fails in seconds and the
    /// test skips instead of burning the full client timeout per test.</summary>
    private static async Task WithServer(OfflineFoldersDavHandler handler, Func<string, Task> clientWork)
    {
        var (app, @base, _) = await StartServer(handler);
        try
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                using var probeResp = await probe.GetAsync(@base + "/dav/offline/__probe__");
            }
            catch (Exception ex) when (ex is SocketException or HttpRequestException or TaskCanceledException)
            {
                throw SkipException.ForSkip("Loopback HTTP is intercepted in this environment: " + ex.GetType().Name);
            }
            try
            {
                await clientWork(@base);
            }
            catch (Exception ex) when (ex is SocketException or HttpRequestException or TaskCanceledException)
            {
                throw SkipException.ForSkip("Loopback HTTP is intercepted in this environment: " + ex.GetType().Name);
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static HttpClient NewClient() => new() { Timeout = TimeSpan.FromSeconds(20) };

    [Fact]
    public Task DavGet_ThroughKestrel_ReturnsDecryptedBytes() =>
        WithServer(SetupHandler(), async @base =>
        {
            using var client = NewClient();
            using var response = await client.GetAsync(@base + "/dav/offline/Vault/docs/report.txt");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Report, await response.Content.ReadAsByteArrayAsync());
        });

    [Fact]
    public Task DavPut_ThroughKestrel_StoresBytes() =>
        WithServer(SetupHandler(), async @base =>
        {
            using var client = NewClient();
            using var put = await client.PutAsync(@base + "/dav/offline/Vault/kestrel.txt",
                new ByteArrayContent(Upload));
            Assert.Equal(System.Net.HttpStatusCode.OK, put.StatusCode);
            using var get = await client.GetAsync(@base + "/dav/offline/Vault/kestrel.txt");
            Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);
            Assert.Equal(Upload, await get.Content.ReadAsByteArrayAsync());
        });
}
