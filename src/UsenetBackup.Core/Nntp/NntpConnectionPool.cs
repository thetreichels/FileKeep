using System.Collections.Concurrent;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Pool of parallel NNTP connections to a single server.
/// Each connection is an independent <see cref="NntpClient"/> (connected
/// and authenticated). Operations acquire a connection, use it exclusively,
/// then release it back to the pool. Thread-safe.
/// </summary>
public sealed class NntpConnectionPool : IDisposable
{
    private readonly ConcurrentQueue<NntpClient> _available = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly int _size;
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
        _semaphore = new SemaphoreSlim(size, size);

        // Connect all clients upfront so failures surface early.
        var connected = new List<NntpClient>(size);
        try
        {
            for (int i = 0; i < size; i++)
            {
                var client = new NntpClient(host, port, useTls, timeout);
                client.Connect();
                if (!string.IsNullOrEmpty(username))
                    client.Authenticate(username, password ?? "");
                connected.Add(client);
                _available.Enqueue(client);
            }
        }
        catch
        {
            foreach (var c in connected)
                try { c.Dispose(); } catch { }
            _semaphore.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Acquires a connection from the pool. Blocks if all are in use.
    /// Call <see cref="Release"/> when done (prefer try/finally).
    /// </summary>
    public NntpClient Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _semaphore.Wait();
        if (_available.TryDequeue(out var client))
            return client;
        // Should not happen: semaphore guarantees availability.
        _semaphore.Release();
        throw new InvalidOperationException("Connection pool exhausted.");
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
            while (_available.TryDequeue(out var client))
                try { client.Dispose(); } catch { }
            _semaphore.Dispose();
        }
    }
}
