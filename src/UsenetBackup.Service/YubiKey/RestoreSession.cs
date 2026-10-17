using System.Security.Cryptography;
using System.Text;

namespace UsenetBackup.Service;

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
    /// <summary>Idle expiry for an unlocked session. Default 15 minutes.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private byte[]? _passphraseUtf8;
    private DateTime _lastUsedUtc;

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

    /// <summary>Stores the unwrapped passphrase (copied) and starts the session.</summary>
    public void Unlock(string passphrase)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(passphrase);
        lock (_lock)
        {
            WipeLocked();
            _passphraseUtf8 = bytes;
            _lastUsedUtc = DateTime.UtcNow;
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
    }
}
