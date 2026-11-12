using System.Collections.Concurrent;
using System.Text;

namespace FileKeep.Core.Relay;

/// <summary>
/// Writes the desktop side of the relay (docs/ios-relay-protocol.md §4–5):
/// signed mailbox index + messages, one-time bundle drops with paired par2.
///
/// The desktop only ever writes its own outbox (<c>mailbox/phone/</c>) and
/// <c>drops/</c>; the phone's outbox is read-only here. Every file is
/// written to a temp name in the same directory and renamed into place, so
/// readers never see a partial file. The mailbox index update is the commit
/// point: until it lands, readers keep using the old state; after it lands,
/// the file and its par2 are visible together.
/// </summary>
public sealed class RelayWriter
{
    private readonly string _relayRoot;
    private readonly byte[] _hmacKey;
    private readonly byte[] _encKey;

    /// <summary>
    /// One gate object per mailbox directory (canonical path). A
    /// <see cref="RelayWriter"/> is constructed fresh per beam/poll/ack,
    /// so an instance lock cannot protect the index read-modify-write
    /// (W-4): two writers could both read sequence N and both write N+1,
    /// silently losing one message with a still-valid HMAC. The gate
    /// serializes index updates in-process; the verify-and-retry in
    /// <see cref="UpdateIndex"/> covers a writer outside this process.
    /// Entries live for the process lifetime; in practice there is one
    /// mailbox dir per relay root, so the table stays tiny.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> IndexGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static object IndexGateFor(string mailboxDir) =>
        IndexGates.GetOrAdd(Path.GetFullPath(mailboxDir), static _ => new object());

    /// <param name="relayRoot">The relay folder (SMB-writable, HTTP-readable).</param>
    /// <param name="hmacKey">Signing key from <see cref="RelayCrypto.DeriveHmacKey"/>.</param>
    /// <param name="encKey">Message-encryption key from <see cref="RelayCrypto.DeriveEncKey"/>.</param>
    public RelayWriter(string relayRoot, byte[] hmacKey, byte[] encKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(relayRoot);
        _relayRoot = relayRoot;
        _hmacKey = hmacKey.ToArray();
        _encKey = encKey.ToArray();
        Directory.CreateDirectory(PhoneMailboxDir);
        Directory.CreateDirectory(LaptopMailboxDir);
        Directory.CreateDirectory(DropsDir);
        Directory.CreateDirectory(LaptopInboxDir);
    }

    /// <summary>How long a drop lives before <see cref="PruneExpiredDrops"/> may delete it.</summary>
    public static readonly TimeSpan DefaultDropLifetime = TimeSpan.FromDays(7);

    private string PhoneMailboxDir => Path.Combine(_relayRoot, "mailbox", "phone");
    private string LaptopMailboxDir => Path.Combine(_relayRoot, "mailbox", "laptop");
    private string DropsDir => Path.Combine(_relayRoot, "drops");
    private string LaptopInboxDir => Path.Combine(_relayRoot, "inbox", "laptop");

    /// <summary>
    /// Publishes a one-time bundle drop: writes the .kfb, generates its
    /// .par2 from the final written bytes, self-checks file-vs-par2,
    /// writes the signed drop metadata, then writes the drop-ready
    /// message and updates the signed phone mailbox index (the commit
    /// point). Returns the drop token and expiry.
    /// </summary>
    public DropPublishResult PublishDrop(byte[] kfbBytes, string name, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(kfbBytes);
        if (kfbBytes.Length == 0)
            throw new ArgumentException("Bundle must not be empty.", nameof(kfbBytes));
        ArgumentException.ThrowIfNullOrEmpty(name);
        // RelayCrypto.DropCanonicalString throws on line breaks; fail fast here too.
        if (name.Contains('\n') || name.Contains('\r'))
            throw new ArgumentException("Drop name must not contain line breaks.", nameof(name));

        TimeSpan ttl = lifetime ?? DefaultDropLifetime;
        DateTimeOffset expiryUtc = DateTimeOffset.UtcNow.Add(ttl);
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        string kfbPath = Path.Combine(DropsDir, token + ".kfb");
        string par2Path = kfbPath + ".par2";
        string jsonPath = Path.Combine(DropsDir, token + ".json");

        // 1. Write the .kfb.
        WriteAtomic(kfbPath, kfbBytes);

        // 2. Generate the par2 from the final written bytes and self-check.
        byte[] written = File.ReadAllBytes(kfbPath);
        byte[] par2 = RelayPar2.Generate(written);
        WriteAtomic(par2Path, par2);
        if (RelayPar2.Verify(written, par2).Status != Par2VerifyStatus.Valid)
        {
            TryDelete(kfbPath);
            TryDelete(par2Path);
            throw new InvalidOperationException("Par2 self-check failed for the published drop.");
        }

        // 3. Signed drop metadata.
        var metadata = new RelayDropMetadata
        {
            Token = token,
            Name = name,
            Size = written.Length,
            ExpiryUtc = expiryUtc,
            Hmac = RelayCrypto.ComputeDropHmac(_hmacKey, token, name, written.Length, expiryUtc),
        };
        WriteAtomic(jsonPath, Encoding.UTF8.GetBytes(
            RelayJson.Serialize(metadata, RelayJson.Indented, RelayJsonContext.Default.RelayDropMetadata)));

        // 4. Drop-ready message + signed index update (the commit point).
        var body = new DropReadyBody
        {
            Token = token,
            Name = name,
            Size = written.Length,
            ExpiryUtc = expiryUtc,
        };
        string bodyJson = RelayJson.Serialize(body, RelayJson.Default, RelayJsonContext.Default.DropReadyBody);
        string messageId = WriteMessage(RelayMessageKinds.DropReady, Encoding.UTF8.GetBytes(bodyJson));

        return new DropPublishResult(token, name, written.Length, expiryUtc, messageId);
    }

    /// <summary>
    /// Writes an encrypted message to the phone's mailbox and updates the
    /// signed index (the commit point). Returns the message id.
    /// </summary>
    public string WriteMessage(string kind, byte[] plaintextBody)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(plaintextBody);
        string id = Guid.NewGuid().ToString("N");
        byte[] bin = RelayCrypto.EncryptMessage(_encKey, _hmacKey, id, plaintextBody);
        WriteAtomic(Path.Combine(PhoneMailboxDir, id + ".bin"), bin);
        UpdateIndex(PhoneMailboxDir, new RelayIndexEntry
        {
            Id = id,
            Kind = kind,
            CreatedUtc = DateTimeOffset.UtcNow,
            Size = bin.Length,
        });
        return id;
    }

    /// <summary>
    /// Reads the phone mailbox index, verifying its HMAC. A missing index
    /// reads as empty (sequence 0); a present-but-badly-signed index throws
    /// (fail closed — never build on a corrupt index).
    /// </summary>
    public RelayIndex ReadPhoneIndex() => ReadIndex(PhoneMailboxDir);

    /// <summary>
    /// Drop tokens are 32 lowercase hex chars (16 random bytes), as
    /// generated in <see cref="PublishDrop"/>. Anything else never came
    /// from this writer; letting it reach a path (e.g. in a
    /// <c>Path.Combine</c>) is unsafe, so it is rejected everywhere a
    /// token is acted on.
    /// </summary>
    internal static bool IsValidDropToken(string? token)
    {
        if (token is null || token.Length != 32)
            return false;
        foreach (char c in token)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Verifies a drop's metadata HMAC. A tampered name (line breaks)
    /// makes the canonical string throw; that is a verification
    /// failure, not a prune failure.
    /// </summary>
    private bool DropHmacVerifies(RelayDropMetadata meta)
    {
        // A null name would throw inside the canonical string; a null
        // name is not verifiable metadata either way.
        if (meta.Name is null)
            return false;
        try
        {
            return RelayCrypto.VerifyDropHmac(_hmacKey, meta);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes one drop's files (.kfb, .kfb.par2, .json). Refuses tokens
    /// that did not come from <see cref="PublishDrop"/> (defence in
    /// depth — callers validate too); deletes nothing on mismatch.
    /// Returns true when the token was valid (deletion attempted).
    /// </summary>
    private bool DeleteDropFiles(string token)
    {
        if (!IsValidDropToken(token))
            return false;
        foreach (string suffix in new[] { ".kfb", ".kfb.par2", ".json" })
        {
            string path = Path.Combine(DropsDir, token + suffix);
            TryDelete(path);
        }
        return true;
    }

    /// <summary>
    /// Deletes an unverifiable drop (unreadable or badly signed
    /// metadata) by its filename token, but only once it is old enough
    /// not to be mid-publish. The filename token cannot contain a
    /// separator, and <see cref="DeleteDropFiles"/> still validates its
    /// shape. Returns true when the drop counted as pruned.
    /// </summary>
    private bool PruneUnverifiableDrop(string jsonPath, DateTimeOffset nowUtc)
    {
        if (File.GetLastWriteTimeUtc(jsonPath) < nowUtc.AddHours(-1).UtcDateTime)
        {
            string token = Path.GetFileNameWithoutExtension(jsonPath);
            return DeleteDropFiles(token);
        }
        return false;
    }

    /// <summary>
    /// Deletes expired drops (and their par2 + metadata) and orphaned
    /// .kfb/.par2 files with no metadata. Orphans younger than one hour
    /// are left alone — they may be mid-publish. Index entries are left
    /// in place (pruning them is allowed but not required; they are small).
    /// The relay is untrusted, so a drop's metadata is never acted on
    /// until its HMAC verifies and its token has the generated shape;
    /// unverifiable drops are cleaned up by filename token only once
    /// old enough. Returns the number of drops pruned.
    /// </summary>
    public int PruneExpiredDrops(DateTimeOffset nowUtc)
    {
        int pruned = 0;
        foreach (string jsonPath in Directory.EnumerateFiles(DropsDir, "*.json"))
        {
            RelayDropMetadata? meta = null;
            try
            {
                string json = File.ReadAllText(jsonPath);
                meta = RelayJson.Deserialize(json, RelayJson.Default, RelayJsonContext.Default.RelayDropMetadata);
            }
            catch
            {
                // Unreadable metadata: the drop is unverifiable; delete it
                // only if it is old enough not to be mid-publish.
                if (PruneUnverifiableDrop(jsonPath, nowUtc))
                    pruned++;
                continue;
            }
            if (meta is null)
                continue;
            // Never act on untrusted metadata: a token that did not come
            // from PublishDrop, or a metadata HMAC that does not verify,
            // means the expiry cannot be trusted either.
            if (!IsValidDropToken(meta.Token) || !DropHmacVerifies(meta))
            {
                if (PruneUnverifiableDrop(jsonPath, nowUtc))
                    pruned++;
                continue;
            }
            if (meta.ExpiryUtc <= nowUtc)
            {
                DeleteDropFiles(meta.Token);
                pruned++;
            }
        }

        // Orphaned .kfb files with no metadata at all.
        foreach (string kfbPath in Directory.EnumerateFiles(DropsDir, "*.kfb"))
        {
            string token = Path.GetFileNameWithoutExtension(kfbPath);
            if (!File.Exists(Path.Combine(DropsDir, token + ".json"))
                && File.GetLastWriteTimeUtc(kfbPath) < nowUtc.AddHours(-1).UtcDateTime)
            {
                if (DeleteDropFiles(token))
                    pruned++;
            }
        }
        return pruned;
    }

    /// <summary>
    /// Appends one entry to a mailbox index (sequence +1, re-signed) and
    /// writes it atomically. The read-modify-write is serialized per
    /// mailbox directory (W-4): without the gate, two concurrent writers
    /// both read sequence N, both write N+1, and one message is silently
    /// lost from the signed index. After each write the fresh index is
    /// re-read and the entry's presence verified — a writer outside this
    /// process (same folder, different spelling) can still overwrite us
    /// between the read and the rename, so a missing entry retries from
    /// the fresh index instead of losing the message. After
    /// <see cref="MaxIndexUpdateAttempts"/> failed attempts the update
    /// throws rather than reporting success for a lost message.
    /// </summary>
    private const int MaxIndexUpdateAttempts = 5;

    private void UpdateIndex(string mailboxDir, RelayIndexEntry newEntry)
    {
        lock (IndexGateFor(mailboxDir))
        {
            for (int attempt = 1; ; attempt++)
            {
                RelayIndex index = ReadIndex(mailboxDir);
                if (index.Messages.Any(m => m.Id == newEntry.Id))
                {
                    // Already recorded: a previous attempt's write landed
                    // (or the entry was appended by a retry that raced).
                    return;
                }
                index.Messages.Add(newEntry);
                index.Sequence++;
                index.Hmac = RelayCrypto.ComputeIndexHmac(_hmacKey, index.Sequence, index.Messages);
                WriteAtomic(
                    Path.Combine(mailboxDir, "index.json"),
                    Encoding.UTF8.GetBytes(RelayJson.Serialize(index, RelayJson.Indented, RelayJsonContext.Default.RelayIndex)),
                    overwrite: true);

                // Verify the entry landed: a concurrent writer outside
                // this process may have overwritten the index between
                // our read and our rename. Re-read from the fresh index.
                RelayIndex written = ReadIndex(mailboxDir);
                if (written.Messages.Any(m => m.Id == newEntry.Id))
                    return;
                if (attempt >= MaxIndexUpdateAttempts)
                {
                    throw new InvalidOperationException(
                        $"Mailbox index update for message {newEntry.Id} did not land after {MaxIndexUpdateAttempts} attempts — a concurrent writer kept overwriting it.");
                }
            }
        }
    }

    /// <summary>
    /// Reads a mailbox index from <paramref name="mailboxDir"/>, verifying
    /// its HMAC. Missing reads as empty; badly signed throws.
    /// </summary>
    public static RelayIndex ReadIndex(string mailboxDir, byte[] hmacKey)
    {
        string path = Path.Combine(mailboxDir, "index.json");
        if (!File.Exists(path))
            return new RelayIndex { Sequence = 0, Messages = new() };
        RelayIndex? index;
        try
        {
            index = RelayJson.Deserialize(File.ReadAllText(path), RelayJson.Default, RelayJsonContext.Default.RelayIndex);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Mailbox index at {path} is not valid JSON.", ex);
        }
        if (index is null)
            throw new InvalidDataException($"Mailbox index at {path} deserialized to null.");
        if (!RelayCrypto.VerifyIndexHmac(hmacKey, index))
            throw new InvalidDataException($"Mailbox index at {path} has a bad HMAC — it was not written by the paired desktop.");
        return index;
    }

    private RelayIndex ReadIndex(string mailboxDir) => ReadIndex(mailboxDir, _hmacKey);

    /// <summary>Writes bytes to a temp name in the same directory, then renames into place.</summary>
    /// <param name="overwrite">True for files that are updated in place (the index); false for write-once files (drops, messages), where an existing final name is a collision.</param>
    internal static void WriteAtomic(string finalPath, byte[] bytes, bool overwrite = false)
    {
        string tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, finalPath, overwrite: overwrite);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // Best effort; a locked file stays for the next prune.
        }
        return false;
    }
}

/// <summary>Result of <see cref="RelayWriter.PublishDrop"/>.</summary>
public sealed record DropPublishResult(
    string Token,
    string Name,
    long Size,
    DateTimeOffset ExpiryUtc,
    string MessageId);
