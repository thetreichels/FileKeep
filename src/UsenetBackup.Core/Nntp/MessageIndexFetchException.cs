namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Thrown when a versioned message-identity index was found on the server
/// (at least one version STAT-succeeds) but the latest version could not be
/// retrieved or parsed.
///
/// Callers must surface this as an explicit recovery error — NOT silently
/// fall back to the local index or deterministic message IDs. After a
/// retention refresh, the deterministic ID may refer to an expired article,
/// so continuing without the required index turns an index-fetch problem
/// into a later chunk-download failure on a clean recovery machine.
/// </summary>
public sealed class MessageIndexFetchException : IOException
{
    public MessageIndexFetchException(string message) : base(message) { }

    public MessageIndexFetchException(string message, Exception innerException)
        : base(message, innerException) { }
}
