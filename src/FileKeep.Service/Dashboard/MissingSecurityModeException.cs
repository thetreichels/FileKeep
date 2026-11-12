namespace FileKeep.Service;

/// <summary>
/// Thrown by <see cref="OfflineFoldersCatalog.Load"/> when a catalog
/// entry has no usable <c>securityMode</c> — absent, not a string, or
/// an unknown value (O2: <c>securityMode</c> is required on load).
/// v1 catalogs carry no <c>securityMode</c> on any entry, so every v1
/// catalog fails to load with this — that is the decided outcome
/// (no migration path by design).
///
/// Derives from <see cref="IOException"/> — <see cref="InvalidDataException"/>'s
/// parent, the same IO/data-load family (InvalidDataException itself is
/// sealed and cannot be derived from). It stays a distinct type, so the
/// unlock, rekey, and passphrase-change endpoints can catch it FIRST and
/// surface its precise message, while generic catch sites still treat it
/// as an IO load failure: a missing security mode is NOT a decrypt
/// failure (the entry decrypts fine), and the message tells the user
/// the precise remedy (delete and re-create the folder, or delete the
/// catalog file).
///
/// The message names the entry (name + locationId) so the user can
/// tell WHICH folder is stale when a catalog holds several.
/// </summary>
public sealed class MissingSecurityModeException : IOException
{
    /// <summary>The entry's display name, as stored in the catalog.</summary>
    public string FolderName { get; }

    /// <summary>The entry's location id, as stored in the catalog.</summary>
    public string LocationId { get; }

    /// <summary>Why the value was unusable: "missing", "not a string", or "unknown value '...'".</summary>
    public string Reason { get; }

    public MissingSecurityModeException(string folderName, string locationId, string reason)
        : base($"Offline folder '{folderName}' (location '{locationId}') has no usable security mode " +
               $"({reason}) — it was created by an older version. " +
               "Delete and re-create the folder, or delete the catalog file.")
    {
        FolderName = folderName;
        LocationId = locationId;
        Reason = reason;
    }

    public MissingSecurityModeException(string message)
        : base(message)
    {
        FolderName = "";
        LocationId = "";
        Reason = "";
    }

    public MissingSecurityModeException(string message, Exception innerException)
        : base(message, innerException)
    {
        FolderName = "";
        LocationId = "";
        Reason = "";
    }
}
