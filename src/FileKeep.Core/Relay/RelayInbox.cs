using System.Text;

namespace FileKeep.Core.Relay;

/// <summary>
/// Reads the phone side of the relay (docs/ios-relay-protocol.md §8):
/// bundles the phone dropped in <c>inbox/laptop/</c> and messages it wrote
/// to <c>mailbox/laptop/</c>. Everything is verified before it is trusted:
/// a bundle without a valid signed <c>.json</c> is reported, never imported
/// (a par2 proves integrity, never authorship); a message that fails
/// decryption is reported, never acted on.
/// </summary>
public sealed class RelayInbox
{
    private readonly string _relayRoot;
    private readonly byte[] _hmacKey;
    private readonly byte[] _encKey;
    private readonly RelayWriter _writer;

    /// <param name="relayRoot">The relay folder.</param>
    /// <param name="hmacKey">Signing key from <see cref="RelayCrypto.DeriveHmacKey"/>.</param>
    /// <param name="encKey">Message-encryption key from <see cref="RelayCrypto.DeriveEncKey"/>.</param>
    /// <param name="writer">The relay writer, used to send acks back to the phone.</param>
    public RelayInbox(string relayRoot, byte[] hmacKey, byte[] encKey, RelayWriter writer)
    {
        ArgumentException.ThrowIfNullOrEmpty(relayRoot);
        _relayRoot = relayRoot;
        _hmacKey = hmacKey.ToArray();
        _encKey = encKey.ToArray();
        _writer = writer;
    }

    private string LaptopMailboxDir => Path.Combine(_relayRoot, "mailbox", "laptop");
    private string LaptopInboxDir => Path.Combine(_relayRoot, "inbox", "laptop");

    /// <summary>
    /// Scans <c>inbox/laptop/</c> for phone-dropped bundles. Every bundle
    /// is reported with its verification outcome; only
    /// <see cref="InboxBundle.SignatureValid"/> bundles are importable.
    /// </summary>
    public IReadOnlyList<InboxBundle> ScanBundles()
    {
        var result = new List<InboxBundle>();
        if (!Directory.Exists(LaptopInboxDir))
            return result;
        foreach (string kfbPath in Directory.EnumerateFiles(LaptopInboxDir, "*.kfb"))
        {
            string token = Path.GetFileNameWithoutExtension(kfbPath);
            result.Add(ScanOneBundle(token, kfbPath));
        }
        return result;
    }

    private InboxBundle ScanOneBundle(string token, string kfbPath)
    {
        byte[] kfbBytes;
        try
        {
            kfbBytes = File.ReadAllBytes(kfbPath);
        }
        catch (Exception ex)
        {
            return new InboxBundle
            {
                Token = token,
                Name = token + ".kfb",
                Size = 0,
                CreatedUtc = DateTimeOffset.UtcNow,
                KfbPath = kfbPath,
                SignatureValid = false,
                Par2Status = InboxPar2Status.Missing,
                RejectionReason = $"Could not read the bundle file: {ex.Message}",
            };
        }

        // Signature: the signed .json sidecar (see RelayInboxMetadata).
        RelayInboxMetadata? metadata = null;
        string? rejection = null;
        string jsonPath = Path.Combine(LaptopInboxDir, token + ".json");
        if (!File.Exists(jsonPath))
        {
            rejection = "Missing signed metadata (.json).";
        }
        else
        {
            try
            {
                metadata = RelayJson.Deserialize(
                    File.ReadAllText(jsonPath), RelayJson.Default, RelayJsonContext.Default.RelayInboxMetadata);
            }
            catch (Exception ex)
            {
                rejection = $"Metadata is not valid JSON: {ex.Message}";
            }
            if (metadata is not null)
            {
                if (!string.Equals(metadata.Token, token, StringComparison.Ordinal))
                    rejection = "Metadata token does not match the file name.";
                else if (metadata.Size != kfbBytes.Length)
                    rejection = $"Metadata size {metadata.Size} does not match the bundle size {kfbBytes.Length}.";
                else if (!RelayCrypto.VerifyInboxHmac(_hmacKey, metadata))
                    rejection = "Metadata HMAC failed — not written by the paired phone.";
            }
        }

        // Par2: assistant only; a missing/bad par2 never vetoes the bundle.
        InboxPar2Status par2Status;
        IReadOnlyList<int>? badBlocks = null;
        string par2Path = kfbPath + ".par2";
        if (!File.Exists(par2Path))
        {
            par2Status = InboxPar2Status.Missing;
        }
        else
        {
            Par2VerifyResult verify;
            try
            {
                verify = RelayPar2.Verify(kfbBytes, File.ReadAllBytes(par2Path));
            }
            catch (Exception ex)
            {
                return new InboxBundle
                {
                    Token = token,
                    Name = metadata?.Name ?? token + ".kfb",
                    Size = kfbBytes.Length,
                    CreatedUtc = metadata?.CreatedUtc ?? File.GetLastWriteTimeUtc(kfbPath),
                    KfbPath = kfbPath,
                    SignatureValid = rejection is null,
                    Par2Status = InboxPar2Status.InvalidPar2,
                    Metadata = metadata,
                    RejectionReason = rejection ?? $"Could not read the par2: {ex.Message}",
                };
            }
            par2Status = verify.Status switch
            {
                Par2VerifyStatus.Valid => InboxPar2Status.Valid,
                Par2VerifyStatus.Damaged => InboxPar2Status.Damaged,
                _ => InboxPar2Status.InvalidPar2,
            };
            badBlocks = verify.BadDataBlocks;
        }

        return new InboxBundle
        {
            Token = token,
            Name = metadata?.Name ?? token + ".kfb",
            Size = kfbBytes.Length,
            CreatedUtc = metadata?.CreatedUtc ?? File.GetLastWriteTimeUtc(kfbPath),
            KfbPath = kfbPath,
            SignatureValid = rejection is null,
            Par2Status = par2Status,
            BadDataBlocks = badBlocks,
            Metadata = metadata,
            RejectionReason = rejection,
        };
    }

    /// <summary>
    /// Reads the phone's mailbox messages, verifying the signed index
    /// first (a bad index HMAC throws — fail closed) and decrypting each
    /// .bin. Messages that fail decryption are returned with a failure
    /// reason instead of throwing, so one bad message never blocks the rest.
    /// </summary>
    public IReadOnlyList<PeerMessage> ReadLaptopMessages()
    {
        RelayIndex index = RelayWriter.ReadIndex(LaptopMailboxDir, _hmacKey);
        var result = new List<PeerMessage>();
        foreach (RelayIndexEntry entry in index.Messages)
        {
            string binPath = Path.Combine(LaptopMailboxDir, entry.Id + ".bin");
            if (!File.Exists(binPath))
            {
                result.Add(new PeerMessage { Entry = entry, FailureReason = "Message file is missing." });
                continue;
            }
            byte[]? plaintext;
            try
            {
                plaintext = RelayCrypto.DecryptMessage(_encKey, _hmacKey, entry.Id, File.ReadAllBytes(binPath));
            }
            catch (Exception ex)
            {
                result.Add(new PeerMessage { Entry = entry, FailureReason = $"Could not read the message file: {ex.Message}" });
                continue;
            }
            result.Add(plaintext is null
                ? new PeerMessage { Entry = entry, FailureReason = "Decryption failed — bad HMAC or tampered ciphertext." }
                : new PeerMessage { Entry = entry, Plaintext = plaintext });
        }
        return result;
    }

    /// <summary>
    /// Writes an <c>ack</c> message to the phone's mailbox for a processed
    /// inbox item. Returns the ack message id.
    /// </summary>
    public string Acknowledge(string refId, string status)
    {
        ArgumentException.ThrowIfNullOrEmpty(refId);
        ArgumentException.ThrowIfNullOrEmpty(status);
        var body = new AckBody { RefId = refId, Status = status };
        string bodyJson = RelayJson.Serialize(body, RelayJson.Default, RelayJsonContext.Default.AckBody);
        return _writer.WriteMessage(RelayMessageKinds.Ack, Encoding.UTF8.GetBytes(bodyJson));
    }

    /// <summary>Deletes an inbox bundle and its companion files after import.</summary>
    public void DeleteBundle(string token)
    {
        foreach (string suffix in new[] { ".kfb", ".kfb.par2", ".json" })
        {
            string path = Path.Combine(LaptopInboxDir, token + suffix);
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort; a locked file stays for the next pass.
            }
        }
    }
}

/// <summary>Par2 outcome for a scanned inbox bundle.</summary>
public enum InboxPar2Status
{
    /// <summary>Par2 present and every data block hashes correctly.</summary>
    Valid,
    /// <summary>Par2 present; some data blocks are damaged (repairable via <see cref="RelayPar2.Repair"/>).</summary>
    Damaged,
    /// <summary>Par2 present but malformed or does not describe this bundle.</summary>
    InvalidPar2,
    /// <summary>No par2 alongside the bundle.</summary>
    Missing,
}

/// <summary>One phone-dropped bundle found by <see cref="RelayInbox.ScanBundles"/>.</summary>
public sealed class InboxBundle
{
    public required string Token { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string KfbPath { get; init; }

    /// <summary>True only when the signed .json verifies (token, size, HMAC).</summary>
    public required bool SignatureValid { get; init; }

    public required InboxPar2Status Par2Status { get; init; }
    public IReadOnlyList<int>? BadDataBlocks { get; init; }
    public RelayInboxMetadata? Metadata { get; init; }

    /// <summary>Why this bundle is not importable, when <see cref="SignatureValid"/> is false.</summary>
    public string? RejectionReason { get; init; }
}

/// <summary>One phone mailbox message from <see cref="RelayInbox.ReadLaptopMessages"/>.</summary>
public sealed class PeerMessage
{
    public required RelayIndexEntry Entry { get; init; }

    /// <summary>Decrypted body; null when <see cref="FailureReason"/> is set.</summary>
    public byte[]? Plaintext { get; init; }

    /// <summary>Why this message could not be decrypted, when set.</summary>
    public string? FailureReason { get; init; }
}
