namespace UsenetBackup.Core;

/// <summary>
/// Local <see cref="IBlobStore"/>: blobs at chunks/&lt;hh&gt;/&lt;rest&gt;.
/// Content addressing makes deduplication automatic — a blob that already
/// exists is never written twice.
/// </summary>
public sealed class LocalBlobStore : IBlobStore
{
    private readonly string _rootDir;

    public LocalBlobStore(string repositoryRoot)
    {
        _rootDir = Path.Combine(repositoryRoot, "chunks");
    }

    public string PathFor(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64 || !chunkIdHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
        return Path.Combine(_rootDir, chunkIdHex[..2], chunkIdHex[2..]);
    }

    public bool Exists(string chunkIdHex) => File.Exists(PathFor(chunkIdHex));

    public void Put(string chunkIdHex, byte[] blob)
    {
        string path = PathFor(chunkIdHex);
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write atomically so interrupted backups never leave half a blob.
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    /// <summary>
    /// Span overload: writes the blob without an intermediate array copy,
    /// for backup loops that encrypt into a pooled buffer. Same atomic
    /// tmp-file + move semantics as <see cref="Put(string, byte[])"/>.
    /// </summary>
    public void Put(string chunkIdHex, ReadOnlySpan<byte> blob)
    {
        string path = PathFor(chunkIdHex);
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(blob);
            }
            File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    public byte[] Get(string chunkIdHex)
    {
        string path = PathFor(chunkIdHex);
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException($"Chunk {chunkIdHex} is missing from the repository.", ex);
        }
    }

    public long StoredCount
    {
        get
        {
            if (!Directory.Exists(_rootDir))
                return 0;
            return Directory.EnumerateFiles(_rootDir, "*", SearchOption.AllDirectories)
                .LongCount(f => !f.Contains(".tmp-", StringComparison.Ordinal));
        }
    }
}
