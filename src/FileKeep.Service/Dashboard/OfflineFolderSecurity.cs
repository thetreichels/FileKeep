using System.Security.Cryptography;
using System.Text;

namespace FileKeep.Service;

/// <summary>
/// Per-folder security for Stealth Folders (2026-10-30, Joel).
///
/// A folder is either:
/// - <see cref="FolderSecurityMode.Global"/>: encrypted under the FileKeep
///   global key (Security tab). Unlocks with the global unlock. This is the
///   pre-existing behavior; Folder1/Folder2 stay on it.
/// - <see cref="FolderSecurityMode.Custom"/>: encrypted under its own
///   independent key (new passphrase, new YubiKey, and/or TOTP). Unlocks
///   individually; other folders stay locked.
///
/// Security properties (accepted 2026-10-30):
/// - Passphrase/YubiKey custom keys are true standalone factors (key in head
///   / hardware). Safe against USB theft AND machine+USB theft.
/// - TOTP custom "key" is phone+machine (folder key stored DPAPI-encrypted on
///   the machine, gated by a TOTP code). Safe against USB theft; falls if the
///   machine itself is compromised. This matches Joel's threat model
///   (USB/SMB chunk theft).
/// </summary>
public enum FolderSecurityMode
{
    Global,
    Custom,
}

/// <summary>
/// Key material for a <see cref="FolderSecurityMode.Custom"/> folder.
/// Exactly one of the three kinds is populated; TOTP may stand alone
/// (Joel 2026-10-30) or pair with passphrase/YubiKey.
/// </summary>
public sealed record CustomFolderKeyInfo
{
    /// <summary>"passphrase" | "yubikey" | "totp" — the primary method.</summary>
    public string Kind { get; init; } = "";

    // Passphrase: PBKDF2-HMAC-SHA512 salt + iterations. The key is derived
    // at unlock; nothing key-equivalent is stored.
    public string? SaltB64 { get; init; }
    public int Iterations { get; init; }

    // YubiKey: per-folder challenge; the folder key is wrapped by the
    // YubiKey's challenge-response (secret never leaves hardware).
    public string? ChallengeB64 { get; init; }
    public string? WrappedKeyB64 { get; init; }
    public int YubiKeySlot { get; init; } = 2;

    // TOTP: folder key + TOTP secret, both DPAPI-encrypted on the machine.
    // A valid code gates release of the folder key for the session.
    public string? KeyProtectedB64 { get; init; }
    public string? TotpSecretProtectedB64 { get; init; }

    // Optional second factor: TOTP paired with passphrase/YubiKey.
    public string? SecondTotpSecretProtectedB64 { get; init; }
}

/// <summary>
/// Helpers for per-folder key derivation and verification.
/// </summary>
public static class FolderSecurityCrypto
{
    private const int Pbkdf2Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>
    /// Derives a 32-byte folder key from a passphrase via PBKDF2-HMAC-SHA512.
    /// </summary>
    public static byte[] DerivePassphraseKey(string passphrase, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(salt);
        return Rfc2898DeriveBytes.Pbkdf2(
            passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, KeySize);
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] NewFolderKey() => RandomNumberGenerator.GetBytes(KeySize);

    public const int DefaultPbkdf2Iterations = Pbkdf2Iterations;
}
