using System.Security.Cryptography;

namespace FileKeep.Service;

/// <summary>
/// Server-side session for the Offline Folders sidebar card (slice 1:
/// locked card + encrypted catalog). It mirrors <see cref="RestoreSession"/>
/// deliberately — same 1-hour idle expiry, same wipe-on-lock — but it is a
/// SEPARATE session object holding a SEPARATE secret: the catalog key
/// derived from the vault root secret, never the restore passphrase.
///
/// Separation is the point: unlocking the card must not authorize
/// restores, and unlocking restores must not open the card. The two
/// sessions share no state; each expires and wipes independently. The
/// decrypted catalog entries live here, in memory only — never on disk
/// in plaintext — and are dropped together with the key on lock/expiry.
/// </summary>
public sealed class OfflineFoldersSession
{
    /// <summary>Idle expiry for an unlocked card session. Default 1 hour (same as restores).</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private byte[]? _catalogKey;
    private byte[]? _contentKey;
    private List<OfflineFolderEntry> _entries = new();
    private DateTime _lastUsedUtc;

    /// <summary>True if a catalog key is currently held and not expired.</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_lock)
            {
                if (_catalogKey is null) return false;
                if (DateTime.UtcNow - _lastUsedUtc > IdleTimeout)
                {
                    WipeLocked();
                    return false;
                }
                return true;
            }
        }
    }

    /// <summary>
    /// Opens the session with a copy of the derived catalog key and the
    /// decrypted entries. Any previous session state is wiped first.
    /// Slice 1 shape: no file-content key, so file serving is refused
    /// until a slice 2 unlock supplies one.
    /// </summary>
    public void Unlock(byte[] catalogKey, List<OfflineFolderEntry> entries) =>
        Unlock(catalogKey, null, entries);

    /// <summary>
    /// Opens the session with copies of the derived catalog key, the
    /// derived file-content key (slice 2), and the decrypted entries.
    /// Any previous session state is wiped first. Both keys are zeroed
    /// on lock/expiry, together with the entries.
    /// </summary>
    public void Unlock(byte[] catalogKey, byte[]? contentKey, List<OfflineFolderEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        ArgumentNullException.ThrowIfNull(entries);
        byte[] keyCopy = catalogKey.ToArray();
        byte[]? contentCopy = contentKey?.ToArray();
        List<OfflineFolderEntry> entriesCopy = entries.ToList();
        lock (_lock)
        {
            WipeLocked();
            _catalogKey = keyCopy;
            _contentKey = contentCopy;
            _entries = entriesCopy;
            _lastUsedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Returns a copy of the decrypted entries, refreshing the idle
    /// timer. Null when locked or expired.
    /// </summary>
    public List<OfflineFolderEntry>? GetEntries()
    {
        lock (_lock)
        {
            if (!IsUnlocked) return null;
            _lastUsedUtc = DateTime.UtcNow;
            return _entries.ToList();
        }
    }

    /// <summary>
    /// Replaces the session's entry list (after an add/delete that has
    /// already been persisted). No-op when locked.
    /// </summary>
    public void SetEntries(List<OfflineFolderEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_lock)
        {
            if (_catalogKey is null) return;
            _entries = entries.ToList();
            _lastUsedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Returns a copy of the catalog key for a persist operation, or
    /// null when locked or expired. The caller must zero the copy
    /// after use.
    /// </summary>
    public byte[]? CopyKey()
    {
        lock (_lock)
        {
            if (!IsUnlocked) return null;
            _lastUsedUtc = DateTime.UtcNow;
            return _catalogKey!.ToArray();
        }
    }

    /// <summary>
    /// Returns a copy of the file-content key for a store/retrieve
    /// operation, or null when locked, expired, or unlocked without
    /// one (slice 1 unlock shape). The caller must zero the copy
    /// after use.
    /// </summary>
    public byte[]? CopyContentKey()
    {
        lock (_lock)
        {
            if (!IsUnlocked) return null;
            _lastUsedUtc = DateTime.UtcNow;
            return _contentKey?.ToArray();
        }
    }

    /// <summary>
    /// Wipes the catalog key, the file-content key, and the decrypted
    /// entries. This is the forensic lockup core: after this call no
    /// key material and no decrypted catalog data remains reachable
    /// from the session.
    /// </summary>
    public void Lock()
    {
        lock (_lock) { WipeLocked(); }
    }

    private void WipeLocked()
    {
        if (_catalogKey is not null)
        {
            CryptographicOperations.ZeroMemory(_catalogKey);
            _catalogKey = null;
        }
        if (_contentKey is not null)
        {
            CryptographicOperations.ZeroMemory(_contentKey);
            _contentKey = null;
        }
        _entries = new();
    }
}
