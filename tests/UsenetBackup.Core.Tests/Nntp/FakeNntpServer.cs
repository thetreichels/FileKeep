using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Minimal in-memory NNTP server for tests. Speaks just enough of the
/// protocol for <see cref="UsenetBackup.Core.Nntp.NntpClient"/>:
/// greeting, MODE READER, AUTHINFO USER/PASS, POST, STAT, ARTICLE, QUIT.
/// </summary>
internal sealed class FakeNntpServer : IDisposable
{
    private static readonly Encoding Wire = Encoding.Latin1;

    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _postCount;

    public readonly ConcurrentDictionary<string, string> Articles = new();
    public string? RequiredUser { get; set; }
    public string? RequiredPassword { get; set; }

    /// <summary>
    /// When true, POST of message-index articles (Subject contains
    /// "[usenet-backup] message-index") is rejected with 441, simulating
    /// an index-publication failure. Chunk posts are unaffected.
    /// </summary>
    public bool FailIndexPosts { get; set; }

    /// <summary>Starts the optional TCP listener (loopback, ephemeral port).</summary>
    public int Listen()
    {
        if (_listener is null)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoop);
        }
        return Port;
    }

    public int Port => _listener is null
        ? throw new InvalidOperationException("Call Listen() first.")
        : ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int PostCount => Volatile.Read(ref _postCount);
    public int StatCount => Volatile.Read(ref _statCount);
    public int ArticleCount => Volatile.Read(ref _articleCount);
    private int _statCount;
    private int _articleCount;

    private async Task AcceptLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _ = Task.Run(() =>
            {
                using (client)
                    HandleConnection(client.GetStream(), _cts.Token);
            });
        }
    }

    private static string? ReadLine(StreamReader r)
    {
        try { return r.ReadLine(); } catch { return null; }
    }

    /// <summary>
    /// Serves the NNTP dialect over any duplex stream. Tests run this over
    /// an in-memory pipe pair so no TCP socket is needed.
    /// </summary>
    public void HandleConnection(Stream stream, CancellationToken ct)
    {
        using (var reader = new StreamReader(stream, Wire, leaveOpen: true))
        using (var writer = new StreamWriter(stream, Wire, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true })
        {
            writer.WriteLine("200 fake-nntp UsenetBackup test server");
            bool authed = RequiredUser is null;

            while (!ct.IsCancellationRequested)
            {
                string? line = ReadLine(reader);
                if (line is null)
                    return;
                string cmd = line.Split(' ')[0].ToUpperInvariant();
                string arg = line.Length > cmd.Length ? line[(cmd.Length + 1)..] : "";

                switch (cmd)
                {
                    case "MODE":
                        writer.WriteLine("200 reader mode");
                        break;
                    case "AUTHINFO":
                        HandleAuth(writer, arg, ref authed);
                        break;
                    case "POST":
                        if (!authed) { writer.WriteLine("481 authentication required"); break; }
                        writer.WriteLine("340 send article");
                        string article = ReadArticle(reader);
                        string? msgId = ExtractMessageId(article);
                        if (msgId is null) { writer.WriteLine("441 missing Message-ID"); break; }
                        if (FailIndexPosts && article.Contains("[usenet-backup] message-index"))
                        { writer.WriteLine("441 index posting rejected (injected failure)"); break; }
                        Articles[msgId] = article;
                        Interlocked.Increment(ref _postCount);
                        writer.WriteLine("240 article posted");
                        break;
                    case "STAT":
                        Interlocked.Increment(ref _statCount);
                        writer.WriteLine(Articles.ContainsKey(arg) ? "223 0 article exists" : "430 no such article");
                        break;
                    case "ARTICLE":
                        Interlocked.Increment(ref _articleCount);
                        if (Articles.TryGetValue(arg, out string? found))
                        {
                            writer.WriteLine("220 0 article follows");
                            // Re-stuff dots on the way out, like a real server.
                            foreach (string l in found.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                                writer.WriteLine(l.StartsWith('.') ? "." + l : l);
                            writer.WriteLine(".");
                        }
                        else
                        {
                            writer.WriteLine("430 no such article");
                        }
                        break;
                    case "QUIT":
                        writer.WriteLine("205 bye");
                        return;
                    case "LISTGROUP":
                        writer.WriteLine($"211 {Articles.Count} 1 {Articles.Count} {arg} group selected");
                        int n = 1;
                        foreach (string mid in Articles.Keys)
                            writer.WriteLine($"{n++} {mid}");
                        writer.WriteLine(".");
                        break;
                    default:
                        writer.WriteLine("500 unknown command");
                        break;
                }
            }
        }
    }

    private void HandleAuth(StreamWriter writer, string arg, ref bool authed)
    {
        string[] parts = arg.Split(' ', 2);
        if (parts[0].Equals("USER", StringComparison.OrdinalIgnoreCase))
        {
            if (RequiredUser is null || parts[1] == RequiredUser)
                writer.WriteLine("381 password required");
            else
                writer.WriteLine("481 authentication rejected");
        }
        else if (parts[0].Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            if (RequiredPassword is null || parts[1] == RequiredPassword)
            {
                authed = true;
                writer.WriteLine("281 authentication accepted");
            }
            else
            {
                writer.WriteLine("481 authentication rejected");
            }
        }
        else
        {
            writer.WriteLine("500 unknown auth command");
        }
    }

    private static string ReadArticle(StreamReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            string? line = ReadLine(reader);
            if (line is null || line == ".")
                break;
            // Un-dot-stuff. Must be ordinal: culture-sensitive StartsWith("..") treats
            // leading control/format chars (e.g. U+0099) as ignorable and would strip a
            // legitimate first byte from lines like "\u0099..".
            sb.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string? ExtractMessageId(string article)
    {
        foreach (string line in article.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith("Message-ID:", StringComparison.OrdinalIgnoreCase))
                return line["Message-ID:".Length..].Trim();
            if (line.Length == 0)
                break;
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
    }
}
