using System.Security.Cryptography;

namespace UsenetBackup.Core.Security;

/// <summary>
/// Orchestrates YubiKey setup and restore-session unlock without any
/// UI or HTTP concerns. All YubiKey touches go through
/// <see cref="IYubiKeyProvider"/> so the flow is fully testable with
/// <see cref="SimulatedYubiKeyProvider"/>.
/// </summary>
public static class YubiKeySetup
{
    /// <summary>YubiKey HMAC-SHA1 secret length in bytes.</summary>
    public const int SlotSecretLength = 20;

    /// <summary>How long to wait for a touch during setup.</summary>
    public static readonly TimeSpan SetupTouchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for a touch when opening a restore session.</summary>
    public static readonly TimeSpan UnlockTouchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Full YubiKey setup: programs <paramref name="slot"/> with a fresh
    /// random HMAC-SHA1 secret (touch-required), generates the install
    /// challenge, then wraps <paramref name="passphrase"/> under the
    /// challenge-response-derived KEK (second touch).
    /// </summary>
    /// <returns>
    /// The base64 challenge and wrapped passphrase blob to persist in the
    /// service config (<c>yubiKeyChallenge</c> /
    /// <c>passphraseYubiKeyWrapped</c>).
    /// </returns>
    /// <exception cref="InvalidOperationException">No YubiKey attached.</exception>
    /// <exception cref="TimeoutException">No touch within the timeout.</exception>
    public static (string ChallengeBase64, string WrappedBase64, byte[] SlotSecret) Setup(
        IYubiKeyProvider provider, string passphrase, int slot = 2)
    {
        if (provider.FindDevices().Count == 0)
            throw new InvalidOperationException("No YubiKey detected. Plug one in and try again.");
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));
        if (slot is not 1 and not 2)
            throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1 or 2.");

        // Program the slot with a fresh secret. The secret itself is never
        // stored anywhere; only the key knows it.
        byte[] secret = RandomNumberGenerator.GetBytes(SlotSecretLength);
        provider.ProgramHmacSha1Slot(slot, secret, requireTouch: true);
        // Secret is returned (not wiped here) so the caller can store it
        // encrypted for programming spare keys later. Caller must wipe.

        byte[] challenge = YubiKeyCrypto.GenerateChallenge();
        byte[] hmac = provider.CalculateHmacSha1(challenge, slot, SetupTouchTimeout);
        try
        {
            byte[] kek = YubiKeyCrypto.DeriveKek(hmac);
            try
            {
                string wrapped = YubiKeyCrypto.Wrap(passphrase, kek);
                return (Convert.ToBase64String(challenge), wrapped, secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmac);
        }
    }

    /// <summary>
    /// Programs a spare YubiKey with an existing slot secret (from a previous
    /// <see cref="Setup"/>). The spare will work interchangeably with the
    /// original key for the same challenge.
    /// </summary>
    public static void ProgramSpareKey(
        IYubiKeyProvider provider, byte[] slotSecret, int slot = 2)
    {
        if (provider.FindDevices().Count == 0)
            throw new InvalidOperationException("No YubiKey detected. Plug one in and try again.");
        if (slot is not 1 and not 2)
            throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1 or 2.");
        provider.ProgramHmacSha1Slot(slot, slotSecret, requireTouch: true);
    }

    /// <summary>
    /// Opens a restore session: touches the key for the stored
    /// <paramref name="challengeBase64"/>, derives the KEK and unwraps the
    /// passphrase. The caller holds the returned passphrase in memory for
    /// the session and wipes it on lock.
    /// </summary>
    /// <exception cref="TimeoutException">No touch within the timeout.</exception>
    /// <exception cref="CryptographicException">
    /// The touch succeeded but the wrapped blob did not decrypt: wrong key,
    /// reprogrammed slot, or tampered config.
    /// </exception>
    public static string Unlock(
        IYubiKeyProvider provider, string challengeBase64, string wrappedBase64,
        int slot = 2, TimeSpan? timeout = null)
    {
        if (provider.FindDevices().Count == 0)
            throw new InvalidOperationException("No YubiKey detected. Plug one in and try again.");
        byte[] challenge;
        try { challenge = Convert.FromBase64String(challengeBase64); }
        catch (FormatException ex) { throw new ArgumentException("Stored YubiKey challenge is corrupt.", ex); }

        byte[] hmac = provider.CalculateHmacSha1(challenge, slot, timeout ?? UnlockTouchTimeout);
        try
        {
            byte[] kek = YubiKeyCrypto.DeriveKek(hmac);
            try
            {
                return YubiKeyCrypto.Unwrap(wrappedBase64, kek);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmac);
        }
    }
}
