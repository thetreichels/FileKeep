namespace FileKeep.Core;

/// <summary>
/// Shared file-backed blob-store I/O: the atomic tmp-write + flush + rename
/// core, the already-exists race check, the narrow delete swallow set, and
/// the chunk-ID path helpers used by both <see cref="LocalBlobStore"/> and
/// <see cref="SmbBlobStore"/> (D1 — the two stores were verbatim duplicates
/// of this logic).
///
/// Canonicalization is per-store, passed as a delegate, and deliberately
/// NOT uniform: Local lowercases chunk IDs (P2-D14 — a mixed-case foreign
/// ID must address the same blob), while Smb passes the ID through
/// unchanged. Lowercasing uniformly would orphan mixed-case blobs already
/// stored on case-sensitive SMB shares (their <c>Exists</c>/<c>Get</c>
/// would miss, <c>Put</c> would write a duplicate lowercase copy, and the
/// orphan would never be pruned). The disguised layout needs no
/// canonicalization parameter: <c>DisguisedLayout.ChunkFileName</c>
/// lowercases internally.
/// </summary>
internal static class BlobStoreIO
{
    /// <summary>
    /// Validates a chunk ID: exactly 64 hex chars. Shared by both stores'
    /// <c>PathFor</c> (and therefore by every <c>Put</c>/<c>Get</c>/
    /// <c>Exists</c>/<c>Delete</c> call, which all funnel through it).
    /// </summary>
    public static void ValidateChunkId(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64 || !chunkIdHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
    }

    /// <summary>
    /// Plain-layout chunk path: <c>chunks/&lt;hh&gt;/&lt;rest&gt;</c>.
    /// <paramref name="canonicalizeId"/> is the per-store canonicalization:
    /// Local passes <c>ToLowerInvariant</c>, Smb passes identity (see the
    /// class doc — do not lowercase uniformly).
    /// </summary>
    public static string PlainChunkPath(string chunksDir, string chunkIdHex,
        Func<string, string> canonicalizeId)
    {
        string id = canonicalizeId(chunkIdHex);
        return Path.Combine(chunksDir, id[..2], id[2..]);
    }

    /// <summary>
    /// Disguised-layout chunk path: flat in the cover folder under the
    /// derived name. <c>ChunkFileName</c> lowercases internally, so no
    /// canonicalization is applied here.
    /// </summary>
    public static string DisguisedChunkPath(string coverDir,
        Service.DisguisedLayout layout, string chunkIdHex) =>
        Path.Combine(coverDir, layout.ChunkFileName(chunkIdHex));

    /// <summary>
    /// True when an <see cref="IOException"/> is the OS reporting that
    /// the move destination already exists — the lost-a-race-with-
    /// another-writer case. Verified empirically on both platforms
    /// (2026-10-27, bare File.Move onto an existing file): Windows
    /// reports Win32 ERROR_ALREADY_EXISTS (183) as 0x800700B7; Linux
    /// reports raw errno EEXIST (17) as 0x11. Checking the failure
    /// cause at failure time, instead of re-probing the filesystem,
    /// is what makes this race-proof: a concurrent prune may delete
    /// the winner's blob between a failed Move and any later
    /// existence check.
    /// </summary>
    public static bool IsAlreadyExists(IOException ex) =>
        ex.HResult == unchecked((int)0x800700B7) // Windows ERROR_ALREADY_EXISTS
        || ex.HResult == 0x11; // Linux EEXIST

    /// <summary>
    /// Best-effort fsync of the directory holding a just-renamed blob, so
    /// the rename (directory entry) is as durable as the file contents.
    /// Unix-only: Windows cannot open a directory as a stream (NTFS
    /// journals metadata instead; over SMB the server handles metadata
    /// durability), so this is a no-op there; any other failure is
    /// swallowed — durability here is strictly best-effort and must
    /// never fail a Put. Residual caveat: the entry of a subdirectory
    /// created moments earlier (the chunk's hh folder in its parent) is
    /// covered only by the filesystem's own journaling, not by this flush.
    /// </summary>
    public static void TryFlushDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            using var fs = new FileStream(dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    /// <summary>
    /// Deletes a stored blob file. Only "already gone" is swallowed: a
    /// delete that fails for permissions, a lock, or an I/O error is a
    /// real failure the caller must see — silently keeping a corrupt blob
    /// while the caller believes it was removed is worse than throwing
    /// (W-11). Callers that want best-effort pruning catch this
    /// themselves (see <c>BackupScheduler.PruneStagedChunks</c>).
    /// </summary>
    public static void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>
    /// Atomically writes <paramref name="blob"/> to <paramref name="path"/>
    /// (dedup: if another writer committed the path first, the lost race
    /// is a no-op, not a failure — see <see cref="IsAlreadyExists"/>).
    /// The caller pre-checks <c>File.Exists(path)</c> for the cheap dedup
    /// path and wraps the bytes (disguised layout) before calling.
    /// </summary>
    public static void PutFile(string path, byte[] blob) =>
        WriteAtomically(path, blob, overwrite: false);

    /// <summary>
    /// Span overload of <see cref="PutFile(string, byte[])"/>: writes the
    /// blob without an intermediate array copy, for backup loops that
    /// encrypt into a pooled buffer. Same atomic tmp-file + move
    /// semantics.
    /// </summary>
    public static void PutFile(string path, ReadOnlySpan<byte> blob) =>
        WriteAtomically(path, blob, overwrite: false);

    /// <summary>
    /// Atomically replaces the blob at <paramref name="path"/>: tmp-write
    /// + flush + rename with overwrite, so a crash leaves the old or the
    /// new blob, never a missing chunk. Unlike <see cref="PutFile"/>,
    /// which is a deduplicating no-op over an existing path, this always
    /// writes the given bytes (W-12, offline rekey).
    /// </summary>
    public static void ReplaceFile(string path, byte[] blob) =>
        WriteAtomically(path, blob, overwrite: true);

    private static void WriteAtomically(string path, ReadOnlySpan<byte> blob, bool overwrite)
    {
        // Write atomically so interrupted backups never leave half a blob,
        // and flush to disk before the rename so a power loss cannot leave
        // the renamed blob's contents behind in a volatile cache (DL-9).
        // Over SMB the flush is best-effort — the server decides how much
        // of it reaches stable storage.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(blob);
                fs.Flush(flushToDisk: true);
            }
            if (overwrite)
            {
                // File.Move with overwrite is an atomic replace
                // (rename(2) on Linux, MoveFileEx REPLACE_EXISTING on
                // Windows).
                File.Move(tmp, path, overwrite: true);
            }
            else
            {
                try
                {
                    File.Move(tmp, path);
                }
                catch (IOException ex) when (IsAlreadyExists(ex))
                {
                    // Lost the race with another writer that committed this
                    // exact chunk concurrently. The chunk ID is the SHA-256
                    // of the plaintext, so the winner's blob decrypts to the
                    // same plaintext (the nonce travels in the blob) — this
                    // is a dedup hit, not a failure. The cause is checked
                    // at failure time, not by re-probing the filesystem: a
                    // concurrent prune may delete the winner's blob between
                    // a failed Move and any later existence check, which
                    // used to turn this handled race into a throw.
                }
            }
            // Best-effort only and never throws (see TryFlushDirectory),
            // so it sits outside the Move's catch.
            TryFlushDirectory(Path.GetDirectoryName(path)!);
        }
        finally
        {
            // Guarded in both stores now (D1 unification): Local used to
            // delete unguarded here, so a failing cleanup after a
            // successful Put could throw; a failing cleanup must never
            // fail the Put itself.
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
