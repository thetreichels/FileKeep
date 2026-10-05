using System.Collections.Concurrent;

namespace UsenetBackup.Core.Nntp;

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
    private bool _disposed;

    public int Size => _size;
    public string Host { get; }
    public int Port { get; }

    public NntpConnectionPool(
        string host,
        int port = 119,
        bool useTls = false,
        string? username = null,
        string? password = null,
        int size = 10,
        TimeSpan? timeout = null)
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
        var client = new NntpClient(Host, Port, _useTls, _timeout);
        client.Connect();
        if (!string.IsNullOrEmpty(_username))
            client.Authenticate(_username, _password);
        return client;
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
        try { return action(client); }
        finally { Release(client); }
    }

    public void Use(Action<NntpClient> action)
    {
        var client = Acquire();
        try { action(client); }
        finally { Release(client); }
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
