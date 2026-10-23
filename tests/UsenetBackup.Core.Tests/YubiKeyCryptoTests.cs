using System.Security.Cryptography;
using UsenetBackup.Core.Security;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// YubiKey challenge-response crypto: KEK derivation, passphrase
/// wrapping, and the setup/unlock flow against the simulated provider.
/// </summary>
public sealed class YubiKeyCryptoTests
{
    [Fact]
    public void DeriveKek_IsDeterministic()
    {
        byte[] hmac = new byte[20];
        RandomNumberGenerator.Fill(hmac);
        byte[] k1 = YubiKeyCrypto.DeriveKek(hmac);
        byte[] k2 = YubiKeyCrypto.DeriveKek(hmac);
        Assert.Equal(32, k1.Length);
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void DeriveKek_DiffersPerResponse()
    {
        byte[] k1 = YubiKeyCrypto.DeriveKek(new byte[20]);
        byte[] h2 = new byte[20]; h2[0] = 1;
        byte[] k2 = YubiKeyCrypto.DeriveKek(h2);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void DeriveKek_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => YubiKeyCrypto.DeriveKek(Array.Empty<byte>()));
    }

    [Fact]
    public void Wrap_UsesRandomNonce()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        string w1 = YubiKeyCrypto.Wrap("secret", kek);
        string w2 = YubiKeyCrypto.Wrap("secret", kek);
        Assert.NotEqual(w1, w2);
        // Both still decrypt.
        Assert.Equal("secret", YubiKeyCrypto.Unwrap(w1, kek));
        Assert.Equal("secret", YubiKeyCrypto.Unwrap(w2, kek));
    }

    [Fact]
    public void Unwrap_WrongKek_Throws()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        byte[] wrong = YubiKeyCrypto.DeriveKek(new byte[] { 1 }.Concat(new byte[19]).ToArray());
        string wrapped = YubiKeyCrypto.Wrap("secret", kek);
        Assert.ThrowsAny<CryptographicException>(() => YubiKeyCrypto.Unwrap(wrapped, wrong));
    }

    [Fact]
    public void Unwrap_TamperedBlob_Throws()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        byte[] blob = Convert.FromBase64String(YubiKeyCrypto.Wrap("secret", kek));
        blob[blob.Length - 1] ^= 0x01; // flip a tag bit
        Assert.ThrowsAny<CryptographicException>(
            () => YubiKeyCrypto.Unwrap(Convert.ToBase64String(blob), kek));
    }

    [Fact]
    public void Unwrap_ShortBlob_Throws()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        Assert.Throws<ArgumentException>(
            () => YubiKeyCrypto.Unwrap(Convert.ToBase64String(new byte[10]), kek));
    }

    [Fact]
    public void SimulatedProvider_MatchesRfc2202Vector()
    {
        // RFC 2202 test case 1: key = 0x0b * 20, data = "Hi There".
        var provider = new SimulatedYubiKeyProvider();
        byte[] key = Enumerable.Repeat((byte)0x0b, 20).ToArray();
        provider.ProgramSlot(2, key);
        byte[] response = provider.CalculateHmacSha1(
            System.Text.Encoding.ASCII.GetBytes("Hi There"), 2, TimeSpan.FromSeconds(1));
        byte[] expected = Convert.FromHexString("b617318655057264e28bc0b6fb378c8ef146be00");
        Assert.Equal(expected, response);
    }

    [Fact]
    public void SimulatedProvider_UnprogrammedSlot_Throws()
    {
        var provider = new SimulatedYubiKeyProvider();
        Assert.Throws<InvalidOperationException>(() =>
            provider.CalculateHmacSha1(new byte[8], 2, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Setup_Unlock_RoundTrip()
    {
        var provider = new SimulatedYubiKeyProvider();
        const string passphrase = "my-test-passphrase";
        var (challenge, wrapped, slotSecret) = YubiKeySetup.Setup(provider, passphrase, slot: 2);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(slotSecret);
        Assert.False(string.IsNullOrEmpty(challenge));
        Assert.False(string.IsNullOrEmpty(wrapped));
        Assert.Equal(YubiKeyCrypto.ChallengeLength, Convert.FromBase64String(challenge).Length);

        string unlocked = YubiKeySetup.Unlock(provider, challenge, wrapped, slot: 2);
        Assert.Equal(passphrase, unlocked);
    }

    [Fact]
    public void Unlock_ReprogrammedSlot_Fails()
    {
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, slotSecret2) = YubiKeySetup.Setup(provider, "secret", slot: 2);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(slotSecret2);
        // User wipes/reprograms the key: the wrapped copy is unrecoverable.
        provider.ProgramSlot(2, RandomNumberGenerator.GetBytes(20));
        Assert.ThrowsAny<CryptographicException>(
            () => YubiKeySetup.Unlock(provider, challenge, wrapped, slot: 2));
    }

    /// <summary>
    /// Wraps the simulated provider with fault injection: the wrap
    /// touch can be made to time out, and a chosen Program call can be
    /// made to fail (SF-16 scenarios).
    /// </summary>
    private sealed class FlakyProvider : IYubiKeyProvider
    {
        private readonly SimulatedYubiKeyProvider _inner = new();
        public bool FailNextChallenge;
        /// <summary>Absolute Program-call number to fail (0 = never).</summary>
        public int FailOnProgramCall;
        public int ProgramCalls;
        /// <summary>The requireTouch flag of every Program call, in order.</summary>
        public List<bool> ProgramRequireTouchValues { get; } = new();

        public IReadOnlyList<YubiKeyInfo> FindDevices() => _inner.FindDevices();

        public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout)
        {
            if (FailNextChallenge)
            {
                FailNextChallenge = false;
                throw new TimeoutException("no touch (simulated)");
            }
            return _inner.CalculateHmacSha1(challenge, slot, timeout);
        }

        public void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch)
        {
            ProgramCalls++;
            ProgramRequireTouchValues.Add(requireTouch);
            if (ProgramCalls == FailOnProgramCall)
                throw new InvalidOperationException("program failed (simulated)");
            _inner.ProgramHmacSha1Slot(slot, secret, requireTouch);
        }
    }

    [Fact]
    public void Setup_WrapTouchFails_PreviousSecretRestored()
    {
        // SF-16: re-setup used to program the slot with a fresh secret
        // BEFORE the wrap touch — a missed touch left the key unable
        // to answer the stored (old) challenge/wrapped pair, silently
        // breaking the working unlock path. With the previous secret
        // supplied, the failed setup rolls the slot back.
        var provider = new FlakyProvider();
        var (challenge1, wrapped1, secret1) = YubiKeySetup.Setup(provider, "pass-one", slot: 2);

        provider.FailNextChallenge = true; // the wrap touch never comes
        Assert.Throws<TimeoutException>(() =>
            YubiKeySetup.Setup(provider, "pass-two", slot: 2, previousSlotSecret: secret1));

        // The old pair still unlocks: rollback restored the old secret.
        Assert.Equal("pass-one", YubiKeySetup.Unlock(provider, challenge1, wrapped1, slot: 2));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret1);
    }

    [Fact]
    public void Setup_WrapTouchFails_RollbackFails_HonestError()
    {
        // If the rollback re-program ALSO fails, the error must say
        // the key no longer matches the saved settings — not a plain
        // "try again" while the old pair is silently dead.
        var provider = new FlakyProvider();
        var (challenge1, wrapped1, secret1) = YubiKeySetup.Setup(provider, "pass-one", slot: 2);

        provider.FailNextChallenge = true;
        // Program calls so far: 1 (first setup). The re-setup's own
        // program is call 2 (succeeds); the rollback is call 3 (fails).
        provider.FailOnProgramCall = 3;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            YubiKeySetup.Setup(provider, "pass-two", slot: 2, previousSlotSecret: secret1));
        Assert.Contains("could not be restored", ex.Message);
        Assert.Contains("no longer matches", ex.Message);
        // And the state really is broken (the message is honest):
        Assert.ThrowsAny<CryptographicException>(() =>
            YubiKeySetup.Unlock(provider, challenge1, wrapped1, slot: 2));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret1);
    }

    [Fact]
    public void Setup_WrapTouchFails_NoPreviousSecret_OriginalError()
    {
        // First-time setup has nothing to roll back to: the original
        // timeout simply propagates.
        var provider = new FlakyProvider();
        provider.FailNextChallenge = true;
        Assert.Throws<TimeoutException>(() =>
            YubiKeySetup.Setup(provider, "pass", slot: 2));
    }

    // ---------- provider contract (S-F1) ----------
    // The simulator used to silently discard requireTouch and timeout,
    // so flipping a production call site to requireTouch: false — a
    // slot programmed to answer WITHOUT a physical touch — left every
    // test green. These tests pin what YubiKeySetup actually passes.

    [Fact]
    public void Setup_ProgramsSlotWithTouchRequired()
    {
        var provider = new SimulatedYubiKeyProvider();
        var (_, _, slotSecret) = YubiKeySetup.Setup(provider, "pass", slot: 2);
        CryptographicOperations.ZeroMemory(slotSecret);
        var call = Assert.Single(provider.ProgramCalls);
        Assert.Equal(2, call.Slot);
        Assert.True(call.RequireTouch);
        Assert.True(provider.SlotRequiresTouch(2));
    }

    [Fact]
    public void ProgramSpareKey_ProgramsWithTouchRequired()
    {
        var provider = new SimulatedYubiKeyProvider();
        byte[] secret = RandomNumberGenerator.GetBytes(YubiKeySetup.SlotSecretLength);
        YubiKeySetup.ProgramSpareKey(provider, secret, slot: 2);
        var call = Assert.Single(provider.ProgramCalls);
        Assert.Equal(2, call.Slot);
        Assert.True(call.RequireTouch);
        Assert.True(provider.SlotRequiresTouch(2));
    }

    [Fact]
    public void Setup_RollbackProgramsWithTouchRequired()
    {
        // Both the re-setup program AND the SF-16 rollback re-program
        // must keep the slot touch-required.
        var provider = new FlakyProvider();
        var (_, _, secret1) = YubiKeySetup.Setup(provider, "pass-one", slot: 2);
        provider.FailNextChallenge = true; // wrap touch never comes
        Assert.Throws<TimeoutException>(() =>
            YubiKeySetup.Setup(provider, "pass-two", slot: 2, previousSlotSecret: secret1));
        CryptographicOperations.ZeroMemory(secret1);
        // Call 1: first setup. Call 2: re-setup. Call 3: rollback.
        Assert.Equal(3, provider.ProgramCalls);
        Assert.All(provider.ProgramRequireTouchValues, v => Assert.True(v));
    }

    [Fact]
    public void SetupAndUnlock_PassDocumentedTouchTimeouts()
    {
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, slotSecret) = YubiKeySetup.Setup(provider, "pass", slot: 2);
        CryptographicOperations.ZeroMemory(slotSecret);
        Assert.Equal("pass", YubiKeySetup.Unlock(provider, challenge, wrapped, slot: 2));
        Assert.Equal(2, provider.HmacCalls.Count);
        Assert.All(provider.HmacCalls, c => Assert.Equal(2, c.Slot));
        Assert.Equal(YubiKeySetup.SetupTouchTimeout, provider.HmacCalls[0].Timeout);
        Assert.Equal(YubiKeySetup.UnlockTouchTimeout, provider.HmacCalls[1].Timeout);
    }

    [Fact]
    public void Setup_Slot1_RoundTrip()
    {
        // Everything else exercises slot 2 only; slot-1 plumbing
        // (program + challenge-response) must work identically.
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, slotSecret) = YubiKeySetup.Setup(provider, "pass", slot: 1);
        CryptographicOperations.ZeroMemory(slotSecret);
        var call = Assert.Single(provider.ProgramCalls);
        Assert.Equal(1, call.Slot);
        Assert.True(call.RequireTouch);
        Assert.Equal("pass", YubiKeySetup.Unlock(provider, challenge, wrapped, slot: 1));
    }

    [Fact]
    public void Setup_NoDevice_Throws()
    {
        var empty = new EmptyProvider();
        Assert.Throws<InvalidOperationException>(() => YubiKeySetup.Setup(empty, "x"));
    }

    [Fact]
    public void Setup_RejectsBadSlot()
    {
        var provider = new SimulatedYubiKeyProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => YubiKeySetup.Setup(provider, "x", slot: 3));
    }

    private sealed class EmptyProvider : IYubiKeyProvider
    {
        public IReadOnlyList<YubiKeyInfo> FindDevices() => Array.Empty<YubiKeyInfo>();
        public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout) =>
            throw new NotImplementedException();
        public void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch) =>
            throw new NotImplementedException();
    }
}
