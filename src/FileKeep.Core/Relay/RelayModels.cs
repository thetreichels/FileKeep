using System.Text.Json.Serialization;

namespace FileKeep.Core.Relay;

/// <summary>
/// Mailbox message kinds (spec §4). The kind is recorded in the signed
/// index entry; the encrypted .bin body carries the kind-specific payload.
/// </summary>
public static class RelayMessageKinds
{
    public const string ApprovalRequest = "approval-request";
    public const string Approval = "approval";
    public const string Ack = "ack";
    public const string Status = "status";
    public const string DropReady = "drop-ready";
}

/// <summary>
/// One entry in a signed mailbox index (spec §4):
/// <c>{ sequence, messages: [{ id, kind, createdUtc, size }], hmac }</c>.
/// <c>size</c> is the .bin file size in bytes. Property names are camelCase
/// on the wire so the iOS side reads the same JSON.
/// </summary>
public sealed class RelayIndexEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// A signed mailbox index. Sequence increases monotonically; pruning old
/// entries is allowed but the sequence never goes backwards. The HMAC is
/// computed over the canonical string in <see cref="RelayCrypto"/>, never
/// over the JSON bytes.
/// </summary>
public sealed class RelayIndex
{
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("messages")]
    public List<RelayIndexEntry> Messages { get; set; } = new();

    [JsonPropertyName("hmac")]
    public string? Hmac { get; set; }
}

/// <summary>
/// Drop metadata (spec §5): <c>{ token, name, size, expiryUtc, hmac }</c>.
/// Lives at <c>drops/&lt;token&gt;.json</c> next to the .kfb and its .par2.
/// <c>size</c> is the .kfb size in bytes; <c>expiryUtc</c> is when the
/// desktop may delete the drop.
/// </summary>
public sealed class RelayDropMetadata
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("expiryUtc")]
    public DateTimeOffset ExpiryUtc { get; set; }

    [JsonPropertyName("hmac")]
    public string? Hmac { get; set; }
}

/// <summary>
/// Inbox-bundle metadata: the phone's authentication for a bundle it drops
/// in <c>inbox/laptop/</c>. Same shape and HMAC as
/// <see cref="RelayDropMetadata"/> so the desktop verifies phone writes
/// exactly the way the phone verifies desktop drops. The inbox file set is
/// <c>&lt;uuid&gt;.kfb</c> + <c>&lt;uuid&gt;.kfb.par2</c> +
/// <c>&lt;uuid&gt;.json</c>; without a valid signed .json the bundle is
/// ignored (a par2 proves integrity, never authorship).
/// </summary>
public sealed class RelayInboxMetadata
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; }

    [JsonPropertyName("hmac")]
    public string? Hmac { get; set; }
}

/// <summary>Body of a <c>drop-ready</c> mailbox message (JSON, encrypted).</summary>
public sealed class DropReadyBody
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("expiryUtc")]
    public DateTimeOffset ExpiryUtc { get; set; }
}

/// <summary>Body of an <c>ack</c> mailbox message (JSON, encrypted).</summary>
public sealed class AckBody
{
    [JsonPropertyName("refId")]
    public string RefId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}
