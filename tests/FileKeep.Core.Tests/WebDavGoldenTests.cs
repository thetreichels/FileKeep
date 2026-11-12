using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Reader;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// D6 golden tests: PROPFIND XML from both DAV handlers must be
/// byte-identical to the output captured from the pre-change build
/// (captured 2026-11-12 by the scratch harness in
/// ~/workspace/.scratch-d6/golden, fixtures rebuilt identically here).
/// The only non-deterministic bytes are the folder AddedUtc
/// getlastmodified values (set to UtcNow when the folder is added),
/// so both sides are normalized on that element before comparison;
/// everything else — element order, hrefs, names, sizes, namespaces,
/// the fk:readonly element — is compared exactly.
/// </summary>
public sealed class WebDavGoldenTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;
    private readonly OfflineFoldersDavHandler _offline;
    private readonly ReaderDavHandler _reader;

    private const string Pass = "the-current-passphrase";
    private const string VaultSecret = "the-vault-secret";
    private const string ExportSecret = "the-export-passphrase";
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    // Golden captured from the pre-change build (see class doc).
    private const string OfRootDepth1 = """
<multistatus xmlns="DAV:">
  <response>
    <href>/dav/offline/</href>
    <propstat>
      <prop>
        <displayname>Offline Folders</displayname>
        <resourcetype>
          <collection />
        </resourcetype>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/dav/offline/Vault/</href>
    <propstat>
      <prop>
        <displayname>Vault</displayname>
        <resourcetype>
          <collection />
        </resourcetype>
        <getlastmodified>Thu, 12 Nov 2026 05:10:58 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    // Golden captured from the pre-change build (see class doc).
    private const string OfFolderDepth1 = """
<multistatus xmlns="DAV:">
  <response>
    <href>/dav/offline/Vault/</href>
    <propstat>
      <prop>
        <displayname>Vault</displayname>
        <resourcetype>
          <collection />
        </resourcetype>
        <getlastmodified>Thu, 12 Nov 2026 05:10:58 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/dav/offline/Vault/docs/</href>
    <propstat>
      <prop>
        <displayname>docs</displayname>
        <resourcetype>
          <collection />
        </resourcetype>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/dav/offline/Vault/top.txt</href>
    <propstat>
      <prop>
        <displayname>top.txt</displayname>
        <resourcetype />
        <getcontentlength>9</getcontentlength>
        <getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    // Golden captured from the pre-change build (see class doc).
    private const string OfFolderDepth0 = """
<multistatus xmlns="DAV:">
  <response>
    <href>/dav/offline/Vault/</href>
    <propstat>
      <prop>
        <displayname>Vault</displayname>
        <resourcetype>
          <collection />
        </resourcetype>
        <getlastmodified>Thu, 12 Nov 2026 05:10:58 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    // Golden captured from the pre-change build (see class doc).
    private const string OfFileDepth0 = """
<multistatus xmlns="DAV:">
  <response>
    <href>/dav/offline/Vault/docs/hello.txt</href>
    <propstat>
      <prop>
        <displayname>hello.txt</displayname>
        <resourcetype />
        <getcontentlength>13</getcontentlength>
        <getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    // Golden captured from the pre-change build (see class doc).
    private const string OfFileDepth0Readonly = """
<multistatus xmlns="DAV:">
  <response>
    <href>/dav/offline/Vault/docs/hello.txt</href>
    <propstat>
      <prop>
        <displayname>hello.txt</displayname>
        <resourcetype />
        <getcontentlength>13</getcontentlength>
        <getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>
        <readonly xmlns="urn:filekeep">true</readonly>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    // Golden captured from the pre-change build (see class doc).
    private const string ReaderRootDepth1 = """
<multistatus xmlns="DAV:">
  <response>
    <href>/</href>
    <propstat>
      <prop>
        <displayname></displayname>
        <resourcetype>
          <collection />
        </resourcetype>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/hello.txt</href>
    <propstat>
      <prop>
        <displayname>hello.txt</displayname>
        <resourcetype />
        <getcontentlength>5</getcontentlength>
        <getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/sub</href>
    <propstat>
      <prop>
        <displayname>sub</displayname>
        <resourcetype>
          <collection />
        </resourcetype>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    // Golden captured from the pre-change build (see class doc).
    private const string ReaderRootInfinity = """
<multistatus xmlns="DAV:">
  <response>
    <href>/</href>
    <propstat>
      <prop>
        <displayname></displayname>
        <resourcetype>
          <collection />
        </resourcetype>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/hello.txt</href>
    <propstat>
      <prop>
        <displayname>hello.txt</displayname>
        <resourcetype />
        <getcontentlength>5</getcontentlength>
        <getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/sub</href>
    <propstat>
      <prop>
        <displayname>sub</displayname>
        <resourcetype>
          <collection />
        </resourcetype>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/sub/deep.txt</href>
    <propstat>
      <prop>
        <displayname>deep.txt</displayname>
        <resourcetype />
        <getcontentlength>4</getcontentlength>
        <getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    // Golden captured from the pre-change build (see class doc).
    private const string ReaderFileDepth0 = """
<multistatus xmlns="DAV:">
  <response>
    <href>/hello.txt</href>
    <propstat>
      <prop>
        <displayname>hello.txt</displayname>
        <resourcetype />
        <getcontentlength>5</getcontentlength>
        <getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    // Golden captured from the pre-change build (see class doc).
    private const string ReaderSubDepth1 = """
<multistatus xmlns="DAV:">
  <response>
    <href>/sub</href>
    <propstat>
      <prop>
        <displayname>sub</displayname>
        <resourcetype>
          <collection />
        </resourcetype>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
  <response>
    <href>/sub/deep.txt</href>
    <propstat>
      <prop>
        <displayname>deep.txt</displayname>
        <resourcetype />
        <getcontentlength>4</getcontentlength>
        <getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>
      </prop>
      <status>HTTP/1.1 200 OK</status>
    </propstat>
  </response>
</multistatus>
""";

    public WebDavGoldenTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-d6-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        _offline = BuildOffline();
        _reader = BuildReader();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private OfflineFoldersDavHandler BuildOffline()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        string share = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(share);
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        config.Locations.Add(new BackupLocation { Name = "nas", Type = "smb", Share = share });
        var session = new OfflineFoldersSession();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalog, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, catalog, "Vault", "nas").Status);
        foreach (var (path, content) in new[] { ("docs/hello.txt", "hello offline"), ("top.txt", "top level") })
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
            Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault", path, ms, FixedTime, null).Status);
        }
        return new OfflineFoldersDavHandler(config, session, catalog);
    }

    private ReaderDavHandler BuildReader()
    {
        string readerDir = Path.Combine(_workDir, "reader");
        var stick = new LocalBlobStore(readerDir);
        byte[] key = OfflineChunkStore.DeriveContentKey(VaultSecret);
        List<string> Store(string content)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(content));
            return OfflineChunkStore.Store(input, stick, key).ChunkIds;
        }
        var payload = new OfflineExportEnvelope.Payload
        {
            Folder = "F",
            Location = new OfflineExportEnvelope.LocationPointer { Type = "usb", Name = "STICK", VolumeSerial = "ABCD-1234" },
            Files = new List<OfflineExportEnvelope.FileRef>
            {
                new() { Path = "hello.txt", Size = 5, ModifiedUtc = FixedTime, ChunkIds = Store("hello") },
                new() { Path = "sub/deep.txt", Size = 4, ModifiedUtc = FixedTime, ChunkIds = Store("deep") },
            },
        };
        string packagePath = Path.Combine(readerDir, OfflineStickLayout.ManifestFileName);
        var package = OfflineExportEnvelope.CreatePackage(payload, ExportSecret);
        OfflineStickLayout.WritePackageAtomic(packagePath, JsonSerializer.Serialize(package));
        var session = new ReaderSession(packagePath, payload, key, ExportSecret, stick, null);
        return new ReaderDavHandler(session);
    }

    // Folder AddedUtc is set to UtcNow at creation, so it differs
    // between the capture run and this run; normalize it on both sides.
    private static string Normalize(string xml) =>
        Regex.Replace(xml, "<getlastmodified>[^<]*</getlastmodified>", "<getlastmodified>*</getlastmodified>");

    private static string OfflineText(OfflineFoldersDavHandler.DavResult result)
    {
        Assert.Equal(207, result.Status);
        Assert.Equal("application/xml; charset=utf-8", result.ContentType);
        Assert.NotNull(result.BodyBytes);
        Assert.Equal(result.BodyBytes!.Length, result.ContentLength);
        return Encoding.UTF8.GetString(result.BodyBytes);
    }

    private static string ReaderText(ReaderDavHandler.Response response)
    {
        Assert.Equal(207, response.Status);
        Assert.Equal("application/xml", response.ContentType);
        Assert.NotNull(response.Body);
        return Encoding.UTF8.GetString(response.Body!);
    }

    private OfflineFoldersDavHandler.DavResult OfflinePropfind(int depth, params string[] segments) =>
        _offline.Handle(new OfflineFoldersDavHandler.DavRequest { Method = "PROPFIND", Segments = segments, Depth = depth });

    private ReaderDavHandler.Response ReaderPropfind(int depth, params string[] segments) =>
        _reader.Handle(new ReaderDavHandler.Request { Method = "PROPFIND", Segments = segments, Depth = depth });

    [Fact]
    public void OfflineFolders_Root_Depth1_MatchesGolden()
    {
        Assert.Equal(Normalize(OfRootDepth1), Normalize(OfflineText(OfflinePropfind(1))));
    }

    [Fact]
    public void OfflineFolders_Folder_Depth1_MatchesGolden()
    {
        Assert.Equal(Normalize(OfFolderDepth1), Normalize(OfflineText(OfflinePropfind(1, "Vault"))));
    }

    [Fact]
    public void OfflineFolders_Folder_Depth0_MatchesGolden()
    {
        Assert.Equal(Normalize(OfFolderDepth0), Normalize(OfflineText(OfflinePropfind(0, "Vault"))));
    }

    [Fact]
    public void OfflineFolders_File_Depth0_MatchesGolden()
    {
        Assert.Equal(Normalize(OfFileDepth0), Normalize(OfflineText(OfflinePropfind(0, "Vault", "docs", "hello.txt"))));
    }

    [Fact]
    public void Reader_Root_Depth1_MatchesGolden()
    {
        Assert.Equal(Normalize(ReaderRootDepth1), Normalize(ReaderText(ReaderPropfind(1))));
    }

    [Fact]
    public void Reader_Root_Infinity_MatchesGolden()
    {
        Assert.Equal(Normalize(ReaderRootInfinity), Normalize(ReaderText(ReaderPropfind(WebDav.DepthInfinity))));
    }

    [Fact]
    public void Reader_File_Depth0_MatchesGolden()
    {
        Assert.Equal(Normalize(ReaderFileDepth0), Normalize(ReaderText(ReaderPropfind(0, "hello.txt"))));
    }

    [Fact]
    public void Reader_Sub_Depth1_MatchesGolden()
    {
        Assert.Equal(Normalize(ReaderSubDepth1), Normalize(ReaderText(ReaderPropfind(1, "sub"))));
    }

    [Fact]
    public void OfflineFolders_File_Depth0_ReadOnly_MatchesGolden()
    {
        Assert.NotNull(_offline.IssueOpenToken("Vault", "docs/hello.txt", readOnly: true));
        Assert.Equal(Normalize(OfFileDepth0Readonly), Normalize(OfflineText(OfflinePropfind(0, "Vault", "docs", "hello.txt"))));
    }

    [Fact]
    public void OfflineFolders_Options_MatchesGolden()
    {
        var options = _offline.Handle(new OfflineFoldersDavHandler.DavRequest { Method = "OPTIONS", Segments = Array.Empty<string>() });
        Assert.Equal(200, options.Status);
        Assert.Equal("1", options.Headers["DAV"]);
        Assert.Equal(WebDav.AllowMethods, options.Headers["Allow"]);
    }

    [Fact]
    public void Reader_Options_MatchesGolden()
    {
        var options = _reader.Handle(new ReaderDavHandler.Request { Method = "OPTIONS", Segments = Array.Empty<string>() });
        Assert.Equal(200, options.Status);
        Assert.Equal("1, 2", options.Headers["DAV"]);
        Assert.Equal(WebDav.AllowMethods, options.Headers["Allow"]);
    }
}
