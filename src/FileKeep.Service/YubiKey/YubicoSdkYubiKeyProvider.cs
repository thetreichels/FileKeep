using FileKeep.Core.Security;
using Yubico.YubiKey;
using Yubico.YubiKey.Otp;

namespace FileKeep.Service;

/// <summary>
/// <see cref="IYubiKeyProvider"/> backed by the official Yubico .NET SDK.
/// Talks to the key over HID; challenge-response blocks until the user
/// physically touches the key (when the slot requires touch).
/// </summary>
public sealed class YubicoSdkYubiKeyProvider : IYubiKeyProvider
{
    /// <summary>
    /// Serializes device attempts (MU-14): a timed-out challenge keeps
    /// its blocking SDK call running against the key, and a second
    /// attempt started on top of it races two sessions on one device.
    /// Calls fail fast while an earlier attempt is still outstanding.
    /// </summary>
    private readonly SemaphoreSlim _attemptGate = new(1, 1);

    public IReadOnlyList<YubiKeyInfo> FindDevices()
    {
        // MU-14: detection failures used to be swallowed into an empty
        // list, so a broken HID stack reported as "no key detected."
        // Let the SDK exception propagate — callers distinguish "no
        // devices" (empty list) from "detection failed" (throw).
        return YubiKeyDevice.FindAll()
            .Select(d => new YubiKeyInfo($"{d.FormFactor} (serial {d.SerialNumber})"))
            .ToList();
    }

    public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout)
    {
        if (challenge is null || challenge.Length == 0)
            throw new ArgumentException("Challenge must not be empty.", nameof(challenge));
        var yubiSlot = ToSlot(slot);

        IYubiKeyDevice? device = FindFirstDevice()
            ?? throw new InvalidOperationException("No YubiKey detected.");

        // MU-14: the SDK call below can't be cancelled once started —
        // on timeout it keeps running against the device. Fail fast
        // instead of stacking a second session on the same key; the
        // outstanding attempt releases the gate when it ends.
        if (!_attemptGate.Wait(0))
            throw new InvalidOperationException(
                "A YubiKey attempt is still in progress (waiting for a touch). " +
                "Let it finish or time out before trying again.");

        // The SDK blocks indefinitely waiting for a touch; race it against
        // the timeout so the dashboard unlock endpoint can't hang forever.
        // An abandoned attempt's response bytes are wiped by the continuation.
        byte[]? response = null;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                using var otp = new OtpSession(device);
                var resp = otp.CalculateChallengeResponse(yubiSlot)
                    .UseChallenge(challenge)
                    .UseYubiOtp(false) // HMAC-SHA1, not Yubico OTP
                    .GetDataBytes();
                response = resp.ToArray();
            }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); _attemptGate.Release(); }
        });
        thread.IsBackground = true;
        thread.Start();

        if (!done.Wait(timeout))
            throw new TimeoutException(
                $"No YubiKey touch within {timeout.TotalSeconds:F0} seconds.");
        if (error is not null)
            throw new InvalidOperationException(
                "YubiKey challenge-response failed. Is slot " + slot +
                " programmed for HMAC-SHA1?", error);
        return response ?? throw new InvalidOperationException("YubiKey returned no response.");
    }

    public void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch)
    {
        if (slot is not 1 and not 2)
            throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1 or 2.");
        if (secret is null || secret.Length == 0)
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        var yubiSlot = ToSlot(slot);

        IYubiKeyDevice? device = FindFirstDevice()
            ?? throw new InvalidOperationException("No YubiKey detected.");

        if (!_attemptGate.Wait(0))
            throw new InvalidOperationException(
                "A YubiKey attempt is still in progress (waiting for a touch). " +
                "Let it finish or time out before programming a slot.");
        try
        {
            using var otp = new OtpSession(device);
            var op = otp.ConfigureChallengeResponse(yubiSlot)
                .UseHmacSha1()
                .UseKey(secret);
            if (requireTouch)
                op.UseButton(true);
            op.Execute();
        }
        finally { _attemptGate.Release(); }
    }

    private static IYubiKeyDevice? FindFirstDevice()
    {
        // MU-14: an enumeration FAILURE is not "no key" — surface it.
        try { return YubiKeyDevice.FindAll().FirstOrDefault(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"YubiKey detection failed: {ex.Message}", ex);
        }
    }

    private static Slot ToSlot(int slot) => slot switch
    {
        1 => Slot.ShortPress,
        2 => Slot.LongPress,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1 or 2."),
    };
}
