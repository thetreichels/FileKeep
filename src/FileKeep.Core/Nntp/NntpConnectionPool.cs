using System.Collections.Concurrent;

namespace FileKeep.Core.Nntp;

/// <summary>
/// Pool of parallel NNTP connections to a single server.
/// Each connection is an independent <see cref="NntpClient"/> (connected
/// and authenticated). Operations acquire a connection, use it exclusively,
/// then release it back to the pool. Thread-safe.
/// </summary>
/// <remarks>
/// The constructor connects and authenticates all <see cref="Size"/> clients
/// upfront, so network failures surface early. This means the constructor
/// performs network I/O and may throw <see cref="NntpException"/> or
/// <see cref="System.Net.Sockets.SocketException"/> if the server is
/// unreachable or credentials are invalid.
/// </remarks>
public sealed class NntpConnectionPool : IDisposable
{
    private readonly ConcurrentQueue<NntpClient> _available = new();
    private readonly List<NntpClient> _allClients = new(); // tracks every client for Dispose
    private readonly SemaphoreSlim _semaphore;
    private readonly object _allClientsLock = new();
    private readonly int _size;
    private readonly string _username;
    private readonly string _password;
    private readonly bool _useTls;
    private readonly TimeSpan? _timeout;
    private readonly Func<NntpClient>? _clientFactory;
    private bool _disposed;

    public int Size => _size;
    public string Host { get; }
    public int Port { get; }

    // TLS by default (security audit D-2), same as NntpClient: an omitted
    // choice means the TLS pair (port 563 + TLS on), never plaintext.
    public NntpConnectionPool(
        string host,
        int port = 563,
        bool useTls = true,
        string? username = null,
        string? password = null,
        int size = 10,
        TimeSpan? timeout = null)
        : this(host, port, useTls, username, password, size, timeout, clientFactory: null)
    {
    }

    private NntpConnectionPool(
        string host,
        int port,
        bool useTls,
        string? username,
        string? password,
        int size,
        TimeSpan? timeout,
        Func<NntpClient>? clientFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        if (size < 1 || size > 100)
            throw new ArgumentOutOfRangeException(nameof(size), "Pool size must be 1-100.");

        Host = host;
        Port = port;
        _size = size;
        _useTls = useTls;
        _username = username ?? "";
        _password = password ?? "";
        _timeout = timeout;
        _clientFactory = clientFactory;
        _semaphore = new SemaphoreSlim(size, size);

        // Connect all clients upfront so failures surface early.
        try
        {
            for (int i = 0; i < size; i++)
            {
                var client = CreateConnectedClient();
                lock (_allClientsLock)
                    _allClients.Add(client);
                _available.Enqueue(client);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private NntpClient CreateConnectedClient()
    {
        // Test seam (C-H1): tests inject already-connected clients over
        // an in-memory transport; production always takes the TCP path.
        if (_clientFactory is not null)
            return _clientFactory();
        var client = new NntpClient(Host, Port, _useTls, _timeout);
        client.Connect();
        if (!string.IsNullOrEmpty(_username))
            client.Authenticate(_username, _password);
        return client;
    }

    /// <summary>
    /// Builds a pool over caller-supplied connected clients instead of
    /// opening TCP connections. Internal test seam (the test assembly
    /// has InternalsVisibleTo); production code uses the public
    /// constructor. <see cref="Host"/>/<see cref="Port"/> are inert
    /// labels on a factory-backed pool.
    /// </summary>
    internal static NntpConnectionPool CreateWithClientFactory(
        int size, Func<NntpClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return new NntpConnectionPool(
            "in-memory", 0, useTls: false, username: null, password: null,
            size, timeout: null, clientFactory);
    }

    /// <summary>
    /// Acquires a connection from the pool. Blocks if all are in use.
    /// Dead connections are transparently replaced.
    /// Call <see cref="Release"/> when done (prefer try/finally).
    /// </summary>
    public NntpClient Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _semaphore.Wait();
        try
        {
            if (_available.TryDequeue(out var client))
            {
                // Health check: replace dead connections instead of handing them out.
                if (!client.IsConnected)
                {
                    try { client.Dispose(); } catch { }
                    lock (_allClientsLock)
                        _allClients.Remove(client);
                    client = CreateConnectedClient();
                    lock (_allClientsLock)
                        _allClients.Add(client);
                }
                return client;
            }
            // Should not happen: semaphore guarantees availability.
            throw new InvalidOperationException("Connection pool exhausted.");
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    /// <summary>Returns a connection to the pool.</summary>
    public void Release(NntpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_disposed)
        {
            try { client.Dispose(); } catch { }
            return;
        }
        _available.Enqueue(client);
        _semaphore.Release();
    }

    /// <summary>
    /// Executes an action with a pooled connection, handling acquire/release.
    /// </summary>
    public T Use<T>(Func<NntpClient, T> action)
    {
        var client = Acquire();
        bool evicted = false;
        try
        {
            return action(client);
        }
        catch (NntpException) when (!evicted)
        {
            // The connection died mid-use (server-side kill, idle timeout).
            // Evict it so the pool doesn't hand out the corpse again, then
            // retry once with a fresh connection. The semaphore slot stays
            // held across the swap (Evict does not release).
            evicted = true;
            try { client.Dispose(); } catch { }
            lock (_allClientsLock)
                _allClients.Remove(client);
            client = CreateConnectedClient();
            lock (_allClientsLock)
                _allClients.Add(client);
            return action(client);
        }
        finally
        {
            Release(client);
        }
    }

    public void Use(Action<NntpClient> action)
    {
        var client = Acquire();
        bool evicted = false;
        try
        {
            action(client);
        }
        catch (NntpException) when (!evicted)
        {
            evicted = true;
            try { client.Dispose(); } catch { }
            lock (_allClientsLock)
                _allClients.Remove(client);
            client = CreateConnectedClient();
            lock (_allClientsLock)
                _allClients.Add(client);
            action(client);
        }
        finally
        {
            Release(client);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Dispose ALL clients, including any currently checked out.
            // Checked-out clients will be disposed again on Release (harmless).
            List<NntpClient> toDispose;
            lock (_allClientsLock)
            {
                toDispose = new List<NntpClient>(_allClients);
                _allClients.Clear();
            }
            foreach (var client in toDispose)
                try { client.Dispose(); } catch { }
            // Drain the queue (clients already disposed above).
            while (_available.TryDequeue(out _)) { }
            _semaphore.Dispose();
        }
    }
}
