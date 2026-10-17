using UsenetBackup.Core.Security;
using Yubico.YubiKey;
using Yubico.YubiKey.Otp;

namespace UsenetBackup.Service;

/// <summary>
/// <see cref="IYubiKeyProvider"/> backed by the official Yubico .NET SDK.
/// Talks to the key over HID; challenge-response blocks until the user
/// physically touches the key (when the slot requires touch).
/// </summary>
public sealed class YubicoSdkYubiKeyProvider : IYubiKeyProvider
{
    public IReadOnlyList<YubiKeyInfo> FindDevices()
    {
        try
        {
            return YubiKeyDevice.FindAll()
                .Select(d => new YubiKeyInfo($"{d.FormFactor} (serial {d.SerialNumber})"))
                .ToList();
        }
        catch
        {
            return Array.Empty<YubiKeyInfo>();
        }
    }

    public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout)
    {
        if (challenge is null || challenge.Length == 0)
            throw new ArgumentException("Challenge must not be empty.", nameof(challenge));
        var yubiSlot = ToSlot(slot);

        IYubiKeyDevice? device = FindFirstDevice()
            ?? throw new InvalidOperationException("No YubiKey detected.");

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
            finally { done.Set(); }
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

        using var otp = new OtpSession(device);
        var op = otp.ConfigureChallengeResponse(yubiSlot)
            .UseHmacSha1()
            .UseKey(secret);
        if (requireTouch)
            op.UseButton(true);
        op.Execute();
    }

    private static IYubiKeyDevice? FindFirstDevice()
    {
        try { return YubiKeyDevice.FindAll().FirstOrDefault(); }
        catch { return null; }
    }

    private static Slot ToSlot(int slot) => slot switch
    {
        1 => Slot.ShortPress,
        2 => Slot.LongPress,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1 or 2."),
    };
}
