using System.Security.Cryptography;
using System.Text;

namespace FileKeep.Service;

/// <summary>
/// Server-side restore session for YubiKey mode. After a successful
/// <c>/api/restore/unlock</c> (physical YubiKey touch), the unwrapped
/// repository passphrase is held in memory here so restore endpoints can
/// decrypt without another touch. The session expires after idle timeout
/// and is wiped on lock.
/// </summary>
/// <remarks>
/// Only meaningful when <c>ServiceConfig.PassphraseYubiKey</c> is true.
/// When YubiKey mode is off, restore endpoints use the environment/DPAPI
/// passphrase directly and never consult this session.
/// </remarks>
public sealed class RestoreSession
{
    /// <summary>Idle expiry for an unlocked session. Default 1 hour.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private byte[]? _passphraseUtf8;
    private DateTime _lastUsedUtc;
    private bool _verified;

    /// <summary>True if a passphrase is currently held and not expired.</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_lock)
            {
                if (_passphraseUtf8 is null) return false;
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
    /// Stores the unwrapped passphrase (copied) and starts the session.
    /// This entry point is for unlocks that are verified BY CONSTRUCTION
    /// (a YubiKey unwrap only succeeds with the right key response; the
    /// phone path releases the machine's own DPAPI-held passphrase after
    /// a presence proof) — the session starts out verified.
    /// </summary>
    public void Unlock(string passphrase) => UnlockCore(passphrase, verified: true);

    /// <summary>
    /// Starts the session with a passphrase nothing has validated yet
    /// (MU-15): the typed recovery secret accepts any non-empty string —
    /// there is no verifier — so the session must not present itself as
    /// fully "unlocked" until a restore actually decrypts with it
    /// (<see cref="MarkVerified"/>). A wrong secret surfaces at the
    /// first restore, and the status badge says "not yet checked"
    /// until then.
    /// </summary>
    public void UnlockUnverified(string passphrase) => UnlockCore(passphrase, verified: false);

    private void UnlockCore(string passphrase, bool verified)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(passphrase);
        lock (_lock)
        {
            WipeLocked();
            _passphraseUtf8 = bytes;
            _lastUsedUtc = DateTime.UtcNow;
            _verified = verified;
        }
    }

    /// <summary>
    /// True when the held passphrase has been proven — by construction
    /// (see <see cref="Unlock"/>) or by a successful restore since the
    /// unlock (<see cref="MarkVerified"/>). False while locked.
    /// </summary>
    public bool IsVerified
    {
        get { lock (_lock) { return _passphraseUtf8 is not null && _verified; } }
    }

    /// <summary>
    /// Records that the held passphrase successfully decrypted a
    /// restore — the typed-secret path's deferred proof (MU-15).
    /// </summary>
    public void MarkVerified()
    {
        lock (_lock)
        {
            if (_passphraseUtf8 is not null)
                _verified = true;
        }
    }

    /// <summary>
    /// Returns the session passphrase, refreshing the idle timer.
    /// Null when locked or expired.
    /// </summary>
    public string? GetPassphrase()
    {
        lock (_lock)
        {
            if (!IsUnlocked) return null;
            _lastUsedUtc = DateTime.UtcNow;
            return Encoding.UTF8.GetString(_passphraseUtf8!);
        }
    }

    /// <summary>Wipes the held passphrase and locks the session.</summary>
    public void Lock()
    {
        lock (_lock) { WipeLocked(); }
    }

    private void WipeLocked()
    {
        if (_passphraseUtf8 is not null)
        {
            CryptographicOperations.ZeroMemory(_passphraseUtf8);
            _passphraseUtf8 = null;
        }
        _verified = false;
    }
}
