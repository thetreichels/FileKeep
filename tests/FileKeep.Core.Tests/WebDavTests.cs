using System.Text;
using FileKeep.Core;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// D6: unit tests for the shared WebDAV helpers in
/// <see cref="WebDav"/> (Depth parsing, Allow header, multistatus
/// builder). Byte-level PROPFIND equality against the pre-change
/// build lives in <see cref="WebDavGoldenTests"/>.
/// </summary>
public sealed class WebDavTests
{
    [Fact]
    public void ParseDepth_MatchesOldDashboardSwitch()
    {
        // One [Fact] (not [Theory]): the sandbox reflection runner only
        // executes [Fact]s, so the case table lives in the body.
        Assert.Equal(0, WebDav.ParseDepth("0"));
        Assert.Equal(1, WebDav.ParseDepth("1"));
        Assert.Equal(int.MaxValue, WebDav.ParseDepth("infinity"));
        Assert.Equal(1, WebDav.ParseDepth(null));
        Assert.Equal(1, WebDav.ParseDepth(""));
        Assert.Equal(1, WebDav.ParseDepth("2"));
        Assert.Equal(1, WebDav.ParseDepth("Infinity"));
        Assert.Equal(1, WebDav.ParseDepth("noreturn"));
    }

    [Fact]
    public void ParseDepth_InfinityIsIntMaxValue() =>
        Assert.Equal(int.MaxValue, WebDav.DepthInfinity);

    [Fact]
    public void AllowMethods_IsTheSharedHeaderValue() =>
        Assert.Equal("OPTIONS, PROPFIND, GET, PUT, DELETE, LOCK, UNLOCK", WebDav.AllowMethods);

    [Fact]
    public void SupportedMethods_ListsTheDispatchedSet() =>
        Assert.Equal(
            new[] { "OPTIONS", "PROPFIND", "GET", "PUT", "DELETE", "LOCK", "UNLOCK", "MKCOL" },
            WebDav.SupportedMethods);

    [Fact]
    public void BuildMultistatus_EmptyList_IsEmptyMultistatus()
    {
        string xml = Encoding.UTF8.GetString(WebDav.BuildMultistatus(Array.Empty<WebDav.DavResource>()));
        Assert.Equal("<multistatus xmlns=\"DAV:\" />", xml);
    }

    [Fact]
    public void BuildMultistatus_FileNode_OmitsCollectionAndReadOnly()
    {
        var nodes = new[]
        {
            new WebDav.DavResource("/f.txt", "f.txt", false, 12,
                new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
        };
        string xml = Encoding.UTF8.GetString(WebDav.BuildMultistatus(nodes));
        Assert.Contains("<displayname>f.txt</displayname>", xml);
        Assert.Contains("<resourcetype />", xml);
        Assert.Contains("<getcontentlength>12</getcontentlength>", xml);
        Assert.Contains("<getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</getlastmodified>", xml);
        Assert.DoesNotContain("readonly", xml);
        Assert.DoesNotContain("urn:filekeep", xml);
    }

    [Fact]
    public void BuildMultistatus_CollectionNode_HasCollectionChildAndNoSize()
    {
        var nodes = new[]
        {
            new WebDav.DavResource("/dir/", "dir", true, null, null),
        };
        string xml = Encoding.UTF8.GetString(WebDav.BuildMultistatus(nodes));
        Assert.Contains("<collection />", xml);
        Assert.DoesNotContain("getcontentlength", xml);
        Assert.DoesNotContain("getlastmodified", xml);
    }

    [Fact]
    public void BuildMultistatus_ReadOnlyNode_EmitsFileKeepReadonly()
    {
        var nodes = new[]
        {
            new WebDav.DavResource("/f.txt", "f.txt", false, 1, null, ReadOnly: true),
        };
        string xml = Encoding.UTF8.GetString(WebDav.BuildMultistatus(nodes));
        Assert.Contains("<readonly xmlns=\"urn:filekeep\">true</readonly>", xml);
    }
}
