using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

public sealed class NntpClientTests : IDisposable
{
    private readonly FakeNntpServer _server = new();

    public void Dispose() => _server.Dispose();

    /// <summary>Connects a client to the fake server over an in-memory pipe.</summary>
    private NntpClient Connect()
    {
        var (clientStream, serverStream) = InMemoryTransport.Create();
        // The client owns clientStream; the server side is closed when the
        // client quits/disconnects, which ends this task.
        _ = Task.Run(() =>
        {
            using (serverStream)
                _server.HandleConnection(serverStream, CancellationToken.None);
        });
        var client = new NntpClient(clientStream);
        client.Connect();
        return client;
    }

    [Fact]
    public void Connect_ReceivesGreeting_QuitCleanly()
    {
        using var client = Connect();
        Assert.NotNull(client.Greeting);
        Assert.Contains("fake-nntp", client.Greeting);
        client.Quit();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Auth_Success()
    {
        _server.RequiredUser = "alice";
        _server.RequiredPassword = "s3cret";
        using var client = Connect();
        client.Authenticate("alice", "s3cret"); // must not throw
        client.Quit();
    }

    [Fact]
    public void Auth_WrongPassword_Throws()
    {
        _server.RequiredUser = "alice";
        _server.RequiredPassword = "s3cret";
        using var client = Connect();
        Assert.Throws<NntpException>(() => client.Authenticate("alice", "wrong"));
        client.Quit();
    }

    [Fact]
    public void Post_Stat_Article_RoundTrip()
    {
        using var client = Connect();

        const string article =
            "From: test\r\n" +
            "Newsgroups: alt.binaries.test\r\n" +
            "Subject: hello\r\n" +
            "Message-ID: <abc123@test>\r\n" +
            "\r\n" +
            "body line one\r\n" +
            ".line starting with a dot\r\n";

        Assert.False(client.Stat("<abc123@test>"));
        Assert.Null(client.GetArticle("<abc123@test>"));

        client.Post(article);

        Assert.Equal(1, _server.PostCount);
        Assert.True(client.Stat("<abc123@test>"));
        string? back = client.GetArticle("<abc123@test>");
        Assert.NotNull(back);
        Assert.Contains("body line one", back);
        Assert.Contains(".line starting with a dot", back); // dot-stuffing round-trips
        client.Quit();
    }

    [Fact]
    public void DotStuffing_IgnorableLeadingByteFollowedByDots_RoundTrips()
    {
        // Regression: culture-sensitive StartsWith("..") treats U+0099 as ignorable,
        // so a line beginning "\u0099.." was misread as dot-stuffed and its first byte
        // stripped. Dot-stuff checks must be ordinal.
        using var client = Connect();

        string trickyLine = new string(new char[] { '\u0099', '.', '.', 'A' });
        string article =
            "From: test\r\n" +
            "Newsgroups: alt.binaries.test\r\n" +
            "Subject: tricky\r\n" +
            "Message-ID: <tricky999@test>\r\n" +
            "\r\n" +
            trickyLine + "\r\n";

        client.Post(article);

        // Upload path: server must store the line intact, not stripped to "..A".
        Assert.True(_server.Articles.TryGetValue("<tricky999@test>", out string? stored));
        Assert.Contains(trickyLine, stored);

        // Download path: client un-dot-stuffing must round-trip it as well.
        string? back = client.GetArticle("<tricky999@test>");
        Assert.NotNull(back);
        Assert.Contains(trickyLine, back);
        client.Quit();
    }

    [Fact]
    public void Commands_WithoutConnect_Throw()
    {
        var (clientStream, serverStream) = InMemoryTransport.Create();
        using (serverStream)
        using (var client = new NntpClient(clientStream))
        {
            Assert.Throws<InvalidOperationException>(() => client.Post("x"));
            Assert.Throws<InvalidOperationException>(() => client.Stat("<x>"));
        }
    }
}
