# YubiKey Recovery USB — Technical Specification

**Date:** 2026-10-17
**Status:** Spec — not implemented
**Depends on:** `7ba6abe` (YubiKey challenge in backup manifest)

## Problem

The recovery USB (`src/UsenetBackup.Recovery/`) currently only accepts a typed
passphrase. Backups protected with YubiKey-only mode (no passphrase) cannot be
recovered from bare metal because:

1. The recovery app has no YubiKey challenge-response code
2. The YubiKey challenge was only in `service.json` on the dead machine
3. WinPE has no YubiKey drivers

`7ba6abe` solved #2 by stamping the challenge (not secret) on the backup
manifest, which travels with the backup to Usenet/NAS. This spec covers #1 and #3.

## Design

### Unlock flow in RecoveryWizard

Current: user types passphrase → decrypt.

New: after the user selects a backup, the wizard checks the manifest's
`yubikey_challenge` field:

- **Challenge present + YubiKey detected:** prompt "Touch your YubiKey" →
  challenge-response → derive KEK → unwrap passphrase (if passphrase-wrapped
  mode) or derive data key directly (if YubiKey-only mode) → decrypt.
- **Challenge present + no YubiKey detected:** show "YubiKey required but not
  found. Plug in your YubiKey and try again." with a Rescan button.
- **Challenge absent:** fall back to current typed-passphrase flow (unchanged).

The wizard must NOT require the passphrase to be typed when YubiKey succeeds.
If the backup used "YubiKey unlocks passphrase" mode, the unwrapped passphrase
is used silently. If "YubiKey only" mode, the derived key decrypts directly.

### Code changes

**`src/UsenetBackup.Recovery/`:**

1. Add `YubiKeyRecovery.cs`:
   - `DetectKeys()`: enumerate HID YubiKeys via P/Invoke `SetupDi*` (no SDK
     dependency — the Yubico SDK may not load in WinPE; raw HID is safer).
   - `ChallengeResponse(slot, challenge)`: send CTAPHID / OTP challenge-response
     command, return HMAC-SHA1. Reuse logic from `YubicoSdkYubiKeyProvider`
     where possible, but with a HID fallback path.
   - `DeriveKey(hmacResponse)`: HKDF-SHA256 → 32-byte data key (must match
     `YubiKeyCrypto.DeriveKek` exactly).

2. Modify `RecoveryWizard.cs`:
   - After manifest download, read `manifest.YubiKeyChallenge`.
   - Add a "Touch your YubiKey" panel with animated prompt (reuse `.yk-touch`
     CSS concept from dashboard).
   - On touch success, proceed to chunk download/restore with the derived key.
   - On failure/timeout (30s), show error and offer "Type passphrase instead"
     (only if the backup also has a passphrase; YubiKey-only backups show
     "Recovery sheet required" pointing to the printed/saved recovery secret).

3. Modify `WizardState.cs`:
   - Add `YubiKeyChallenge` (base64) and `YubiKeyHmacResponse` fields.
   - `ValidatePassphrase()` → generalize to `ValidateCredentials()` that
     accepts either passphrase or YubiKey-derived key.

**`winre/build-winre-usb.ps1`:**

4. Add YubiKey HID drivers to the WinPE image:
   - WinPE includes generic HID drivers by default, but verify
     `hidclass.sys`, `hidparse.sys`, `hidusb.sys` are in the boot.wim.
   - If missing, add via `dism /Add-Driver`.
   - Test: plug YubiKey into WinPE, verify it enumerates as HID device
     (check Device Manager or `pnputil /enum-devices`).

5. No SDK DLLs needed if using raw HID P/Invoke. If SDK is required,
   add `Yubico.YubiKey.dll` + dependencies to the recovery app folder
   on the USB.

### Security considerations

- The challenge is public (in the manifest on Usenet). Security comes from
  the YubiKey's secret, which never leaves the hardware.
- The derived key exists only in memory during recovery, wiped on completion
  (reuse `SecureMemory` patterns from `RestoreSession`).
- A stolen USB + stolen YubiKey = full access (same as current passphrase
  model with a stolen passphrase). No regression.

### Testing

1. **Unit:** `YubiKeyRecovery.DeriveKey` produces identical output to
   `YubiKeyCrypto.DeriveKek` for RFC 2202 test vectors.
2. **Integration (VM):** Build USB, boot WinPE in Hyper-V, plug virtual
   YubiKey (or physical via USB passthrough), verify detection.
3. **End-to-end:** Create YubiKey-only backup → boot recovery USB → touch
   key → restore succeeds → verify file hashes match.
4. **Negative:** No YubiKey plugged → clear error. Wrong YubiKey (different
   secret) → decryption fails with "incorrect key" (not a crash).

### Phone authenticator (TOTP) explicitly excluded

TOTP is only usable on a running Windows system, never from the recovery USB.
This is a deliberate design constraint, not a limitation to work around.

Rationale: TOTP secrets cannot be securely stored in the backup manifest
(they're secret, unlike the YubiKey challenge), and any workaround
(recovery codes, encrypted secrets) adds complexity and new secrets to manage.
The recovery USB supports passphrase and YubiKey only.

Enforcement:
- If a backup's manifest indicates TOTP-only protection, the recovery wizard
  shows: "This backup requires phone authenticator, which is not available
  in recovery mode. Use the FileKeep app on a running Windows system, or
  use your recovery sheet."
- The product MUST prevent creating TOTP-only backups without also
  configuring a recovery sheet or another method. TOTP-only with no fallback
  is not a valid configuration.
- "Both required" (passphrase AND YubiKey) cryptographic mode — not
  implemented in the product yet; when it is, recovery will prompt for both.
- Changing the manifest format — `yubikey_challenge` field already exists.

## Effort estimate

- `YubiKeyRecovery.cs` (HID + challenge-response): ~200 lines
- `RecoveryWizard.cs` modifications: ~150 lines
- `WizardState.cs` modifications: ~30 lines
- `build-winre-usb.ps1` driver check: ~20 lines
- Tests: ~100 lines
- WinPE hardware validation: 1–2 hours on VM with physical YubiKey

Total: ~500 lines + hardware testing.
