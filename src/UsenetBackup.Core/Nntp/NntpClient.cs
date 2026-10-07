using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Minimal NNTP client: just enough for a backup backend —
/// greeting, MODE READER, AUTHINFO USER/PASS, POST, STAT, ARTICLE, QUIT.
/// Protocol lines and article bodies travel as Latin-1 so yEnc's raw
/// high bytes survive the string layer untouched.
/// </summary>
public sealed class NntpClient : IDisposable
{
    private static readonly Encoding WireEncoding = Encoding.Latin1;

    private readonly string? _host;
    private readonly int _port;
    private readonly bool _useTls;
    private readonly TimeSpan _timeout;
    private readonly Stream? _externalTransport;

    private TcpClient? _tcp;
    private Stream? _ownedStream; // TCP/TLS stream created by Connect()
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public string? Greeting { get; private set; }
    public bool IsConnected => _reader is not null;

    public NntpClient(string host, int port = 119, bool useTls = false,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        _host = host;
        _port = port;
        _useTls = useTls;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Uses a pre-connected duplex stream as the transport (tests, proxied
    /// or tunneled connections). The client takes ownership and disposes it.
    /// </summary>
    public NntpClient(Stream transport, TimeSpan? timeout = null)
    {
        _externalTransport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public void Connect()
    {
        ThrowIfDisposed();
        Disconnect();

        if (_externalTransport is not null)
        {
            _ownedStream = _externalTransport;
            AttachTransport(_externalTransport);
        }
        else
        {
            _tcp = new TcpClient();
            if (!_tcp.ConnectAsync(_host!, _port).Wait(_timeout))
                throw new NntpException($"Timed out connecting to {_host}:{_port}.");
            _tcp.SendTimeout = (int)_timeout.TotalMilliseconds;
            _tcp.ReceiveTimeout = (int)_timeout.TotalMilliseconds;

            Stream stream = _tcp.GetStream();
            if (_useTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                ssl.AuthenticateAsClient(_host!);
                stream = ssl;
            }
            _ownedStream = stream;
            AttachTransport(stream);
        }

        var (code, text) = ReadResponse();
        if (code != 200 && code != 201)
            throw new NntpException($"Unexpected greeting from NNTP server: {code} {text}");
        Greeting = text;

        // Optional per RFC 3977; tolerate servers that reject it.
        try { SendCommand("MODE READER"); } catch (NntpException) { /* keep going */ }
    }

    private void AttachTransport(Stream stream)
    {
        _reader = new StreamReader(stream, WireEncoding, leaveOpen: true);
        _writer = new StreamWriter(stream, WireEncoding, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true,
        };
    }

    public void Authenticate(string user, string password)
    {
        EnsureConnected();
        var (code, _) = SendCommand($"AUTHINFO USER {user}");
        if (code == 281)
            return; // no password required
        if (code != 381)
            throw new NntpException($"AUTHINFO USER rejected: {code}");
        (code, _) = SendCommand($"AUTHINFO PASS {password}");
        if (code != 281)
            throw new NntpException($"NNTP authentication failed: {code}");
    }

    /// <summary>Posts a complete article (headers + blank line + body).</summary>
    public void Post(string articleText)
    {
        EnsureConnected();
        var (code, text) = SendCommand("POST");
        if (code != 340)
            throw new NntpException($"Server refused POST: {code} {text}");

        foreach (string line in SplitLines(articleText))
        {
            // Dot-stuffing (RFC 3977 §3.1.1).
            _writer!.WriteLine(line.StartsWith('.') ? "." + line : line);
        }
        _writer!.WriteLine(".");

        (code, text) = ReadResponse();
        if (code != 240)
            throw new NntpException($"POST failed: {code} {text}");
    }

    /// <summary>True when an article with this message-ID exists on the server.</summary>
    public bool Stat(string messageId)
    {
        EnsureConnected();
        var (code, _) = SendCommand($"STAT {messageId}");
        return code switch
        {
            223 => true,
            430 => false,
            _ => throw new NntpException($"STAT {messageId} failed: {code}"),
        };
    }

    /// <summary>Returns the full article text, or null when it doesn't exist.</summary>
    public string? GetArticle(string messageId)
    {
        EnsureConnected();
        var (code, text) = SendCommand($"ARTICLE {messageId}");
        if (code == 430)
            return null;
        if (code != 220)
            throw new NntpException($"ARTICLE {messageId} failed: {code} {text}");
        return ReadMultiline();
    }

    /// <summary>
    /// <summary>
    /// Lists message-IDs in a newsgroup via LISTGROUP. Used by the recovery
    /// wizard to discover manifest articles newer than the USB stick.
    /// WARNING: This downloads the entire group listing. On large public
    /// groups (e.g., alt.binaries.test with billions of articles) this is
    /// infeasible. A safeguard aborts if the listing exceeds 100k entries.
    /// </summary>
    public IReadOnlyList<string> ListGroup(string newsgroup)
    {
        EnsureConnected();
        var (code, _) = SendCommand($"LISTGROUP {newsgroup}");
        if (code != 211)
            throw new NntpException($"LISTGROUP {newsgroup} failed: {code}");
        const int maxArticles = 100_000;
        var result = new List<string>();
        foreach (string line in ReadMultiline().Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0 || t == ".")
                continue;
            // LISTGROUP lines: "article-number message-id" or just "article-number".
            int space = t.IndexOf(' ');
            if (space > 0 && space + 1 < t.Length)
                result.Add(t[(space + 1)..].Trim());
            if (result.Count > maxArticles)
                throw new InvalidOperationException(
                    $"Newsgroup {newsgroup} has more than {maxArticles:N0} articles; " +
                    "manifest discovery via LISTGROUP is infeasible. " +
                    "Provide backup IDs directly instead.");
        }
        return result;
    }

    public void Quit()
    {
        if (!IsConnected)
            return;
        try { SendCommand("QUIT"); } catch { /* best effort */ }
        Disconnect();
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!IsConnected || _reader is null || _writer is null)
            throw new InvalidOperationException("NNTP client is not connected. Call Connect() first.");
    }

    private (int Code, string Text) SendCommand(string command)
    {
        _writer!.WriteLine(command);
        return ReadResponse();
    }

    private (int Code, string Text) ReadResponse()
    {
        string? line = _reader!.ReadLine();
        if (line is null)
            throw new NntpException("Connection closed by server.");
        if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), out int code))
            throw new NntpException($"Malformed NNTP response: '{line}'.");
        return (code, line.Length > 4 ? line[4..] : "");
    }

    private string ReadMultiline()
    {
        var sb = new StringBuilder();
        while (true)
        {
            string? line = _reader!.ReadLine();
            if (line is null)
                throw new NntpException("Connection closed mid-article.");
            if (line == ".")
                break;
            // Un-dot-stuff. Must be ordinal: culture-sensitive StartsWith("..") treats
            // leading control/format chars (e.g. U+0099) as ignorable and would strip a
            // legitimate first byte from lines like "\u0099..".
            sb.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string[] SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

    private void Disconnect()
    {
        // Never throw from Dispose: swallow teardown errors, but note them
        // in debug output for connection-leak diagnostics.
        try { _writer?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"NntpClient.Disconnect: writer dispose failed: {ex.Message}"); }
        try { _reader?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"NntpClient.Disconnect: reader dispose failed: {ex.Message}"); }
        // The transport stream is owned by the client in both modes:
        // TCP/TLS streams are created here; a pre-connected stream is
        // ownership-transferred by the Stream constructor.
        try { _ownedStream?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"NntpClient.Disconnect: stream dispose failed: {ex.Message}"); }
        try { _tcp?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"NntpClient.Disconnect: tcp dispose failed: {ex.Message}"); }
        _writer = null;
        _reader = null;
        _ownedStream = null;
        _tcp = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NntpClient));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Disconnect();
            _disposed = true;
        }
    }
}

public sealed class NntpException : Exception
{
    public NntpException(string message) : base(message) { }
    public NntpException(string message, Exception inner) : base(message, inner) { }
}
