namespace UsenetBackup.Core.Security;

/// <summary>
/// Describes a YubiKey found on the machine.
/// </summary>
public sealed record YubiKeyInfo(string Description);

/// <summary>
/// Hardware abstraction for YubiKey HMAC-SHA1 challenge-response.
/// The real implementation talks to the key over HID (touch required);
/// <see cref="SimulatedYubiKeyProvider"/> emulates it in software for
/// tests and machines without a key attached.
/// </summary>
public interface IYubiKeyProvider
{
    /// <summary>Lists attached YubiKeys. Empty if none are present.</summary>
    IReadOnlyList<YubiKeyInfo> FindDevices();

    /// <summary>
    /// Sends <paramref name="challenge"/> to <paramref name="slot"/> and
    /// returns the 20-byte HMAC-SHA1 response. Blocks until the user touches
    /// the key or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <exception cref="TimeoutException">No touch within the timeout.</exception>
    /// <exception cref="InvalidOperationException">No key, or the slot is not
    /// programmed for HMAC-SHA1 challenge-response.</exception>
    byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout);

    /// <summary>
    /// Programs <paramref name="slot"/> (1 or 2) with an HMAC-SHA1
    /// challenge-response credential using <paramref name="secret"/>.
    /// Overwrites whatever the slot currently holds.
    /// </summary>
    void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch);
}

/// <summary>
/// Software emulation of a YubiKey HMAC-SHA1 slot for tests and for
/// validating the setup/unlock flow on machines without hardware.
/// Computes the response with the programmed secret directly; the
/// <paramref name="timeout"/> touch is simulated as immediate.
/// </summary>
public sealed class SimulatedYubiKeyProvider : IYubiKeyProvider
{
    private readonly Dictionary<int, byte[]> _slots = new();
    private readonly Dictionary<int, bool> _slotRequireTouch = new();
    private readonly List<(int Slot, bool RequireTouch)> _programCalls = new();
    private readonly List<(int Slot, TimeSpan Timeout)> _hmacCalls = new();

    /// <summary>
    /// Every <see cref="ProgramHmacSha1Slot"/> call, in order, with the
    /// requireTouch flag it carried. The simulator used to silently
    /// discard that flag, so a production call site programming a slot
    /// WITHOUT touch-required (YubiKeySetup passes
    /// <c>requireTouch: true</c>) left the whole suite green (S-F1).
    /// Tests now assert on this record.
    /// </summary>
    public IReadOnlyList<(int Slot, bool RequireTouch)> ProgramCalls => _programCalls;

    /// <summary>
    /// Every <see cref="CalculateHmacSha1"/> call, in order, with the
    /// timeout it carried (also previously discarded). Tests assert the
    /// setup/unlock flows pass their documented touch timeouts.
    /// </summary>
    public IReadOnlyList<(int Slot, TimeSpan Timeout)> HmacCalls => _hmacCalls;

    /// <summary>Whether the slot was programmed touch-required.</summary>
    public bool SlotRequiresTouch(int slot) =>
        _slotRequireTouch.TryGetValue(slot, out bool requireTouch) && requireTouch;

    /// <summary>Programs a slot directly (test setup).</summary>
    public void ProgramSlot(int slot, byte[] secret) => _slots[slot] = (byte[])secret.Clone();

    public IReadOnlyList<YubiKeyInfo> FindDevices() =>
        new[] { new YubiKeyInfo("Simulated YubiKey") };

    public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout)
    {
        if (!_slots.TryGetValue(slot, out var secret))
            throw new InvalidOperationException($"Simulated slot {slot} is not programmed for HMAC-SHA1.");
        if (challenge is null || challenge.Length == 0)
            throw new ArgumentException("Challenge must not be empty.", nameof(challenge));
        _hmacCalls.Add((slot, timeout));
        // The touch itself is simulated as immediate, but a non-positive
        // timeout still means no touch could have happened in time —
        // the real provider's race would have timed out too.
        if (timeout <= TimeSpan.Zero)
            throw new TimeoutException("No touch within the timeout (simulated).");
        using var hmac = new System.Security.Cryptography.HMACSHA1(secret);
        return hmac.ComputeHash(challenge);
    }

    public void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch)
    {
        if (slot is not 1 and not 2)
            throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 1 or 2.");
        _programCalls.Add((slot, requireTouch));
        _slotRequireTouch[slot] = requireTouch;
        ProgramSlot(slot, secret);
    }
}
