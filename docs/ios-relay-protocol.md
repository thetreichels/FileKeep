# iOS ↔ Desktop Relay Protocol

How the FileKeep desktop app (Windows service) and the FileKeep iOS Reader app
talk to each other without any live connection between them, without Tailscale,
and without trusting the server in the middle.

Status: agreed design (2026-11-04). Desktop side implemented; iOS side not
yet started.

## 0. Principles

- **No Tailscale.** The connection must not depend on it.
- **The relay is a static folder.** Writable from SMB, readable from HTTP, no
  server-side code at all. Nothing to deploy, patch, or exploit.
- **Read by HTTP anywhere, write by SMB when home.** The phone reads the relay
  over HTTP from anywhere; it writes only when the SMB share is reachable on
  the local network. It detects SMB availability (subnet scan for TCP 445
  + network-path monitoring) and flushes queued outbound items when home.
- **The server is never trusted.** Only the desktop's key is trusted. Every
  file the desktop writes is signed; the phone verifies before acting.
  Unsigned or wrongly signed = ignored.
- **The `.kfb` is authoritative; the `.par2` is an assistant.** A damaged
  par2 never vetoes a good bundle.
- **Never silently overwrite.** Conflicts hold until the user decides.

## 1. Relay layout

One folder, exposed both ways (SMB write, HTTP read):

```
<relay>/
  mailbox/
    phone/
      index.json          # signed by desktop, sequence-numbered
      <id>.bin            # one encrypted message per file
    laptop/
      index.json          # signed by phone
      <id>.bin
  drops/
    <token>.kfb           # one-time beamed bundle
    <token>.kfb.par2      # its paired par2
    <token>.json          # drop metadata (name, size, expiry) — signed
  inbox/
    laptop/
      <uuid>.kfb          # phone-created bundles waiting for desktop import
      <uuid>.kfb.par2
      <uuid>.json         # inbox metadata (name, size, createdUtc) — signed;
                          # without a valid signed .json the bundle is ignored
  received/
    <uuid>.kfb          # desktop-imported phone bundles (with .par2 + .json)
```

Each side owns its outbox directory and only ever writes there
(desktop → `mailbox/phone/`, phone → `mailbox/laptop/`), so two writers
never touch the same file. Drop and inbox files use random token/UUID names.

## 2. Pairing (once)

- Desktop shows a QR code: relay base URL + pairing token + desktop public
  key (or shared-secret seed).
- Phone scans it with the camera. No passwords typed.
- Phone stores its private key / shared secret in the iOS Keychain,
  biometric-gated (Face ID). Desktop stores the shared pairing secret in
  DPAPI-protected config (`ServiceConfig`, alongside the other
  `*Protected` fields).
- Revocation: delete the key on either side; a lost phone is unpaired from
  the desktop.
- Authentication between the apps is HMAC-SHA256 with the pairing secret
  (symmetric; the threat is outsiders, not each other). The QR carries the
  secret seed; both sides derive the same HMAC key from it.

## 3. Identity: proving the server is the right one

A friend's house may have a similar setup, even another FileKeep. Name and
address prove nothing — two setups look identical. Three checks, in order:

1. **Signatures.** Every file the desktop writes is HMAC-signed with the
   pairing secret. The phone verifies before acting. This proves authorship,
   not just integrity.
2. **Write-gate check (before the phone writes anything via SMB).** The
   phone compares the SMB folder's current state against the relay state it
   most recently saw over HTTP. Anchor: the signed mailbox index.
   Same signing key + sequence number equal or higher (pruning allowed) =
   same server, writes allowed. Different key, unrelated content, or an
   older sequence = don't write, and say so in the app. Prefer a fresh HTTP
   fetch when reachable, fall back to the cached snapshot.
3. **Manual override.** No cached HTTP state (first pairing, cache cleared)
   means the check can't run. The user confirms in the app, which re-anchors
   the baseline to the current SMB state so future checks work.

Known edge: a byte-for-byte clone of the whole folder would pass check 2.
The nonce challenge (phone drops a random nonce file, desktop signs it and
writes the answer back) is the deeper check for that case; signatures still
prevent a clone from forging new content.

Discovery (Bonjour) is reachability only, never identity. The paired relay
URL is the primary address. If the server ever gets HTTPS, pin the TLS
certificate fingerprint captured at pairing.

## 4. Messages (mailbox)

- `mailbox/<peer>/index.json`: `{ sequence, messages: [{ id, kind, createdUtc, size }], hmac }`.
  Sequence increases monotonically; the index is re-signed on every change.
  Pruning old entries is allowed; the sequence never goes backwards.
- `mailbox/<peer>/<id>.bin`: the message body, encrypted with the pairing
  secret (AES-GCM), then HMAC'd. Kinds: `approval-request`, `approval`,
  `ack`, `status`, `drop-ready`.
- Readers poll the index (phone over HTTP; desktop over SMB), fetch new
  `.bin` files, verify HMAC, decrypt.
- The phone polls while the app is open; there is no push. Latency of a few
  seconds is acceptable for approvals.

## 5. Beaming a bundle (desktop → phone)

1. Desktop writes `drops/<token>.kfb` via SMB.
2. Desktop generates `drops/<token>.kfb.par2` from the final written bytes
   (see §6) and verifies file-vs-par2 as a self-check.
3. Desktop writes `drops/<token>.json` (name, size, expiry, HMAC) and
   updates the signed phone mailbox index (new `drop-ready` message).
   The index update is the commit point: until it lands, readers keep using
   the old state; after it lands, file and par2 are visible together.
   Nobody ever sees a file without its matching par2.
4. Phone fetches the `.kfb` (and `.par2`) over HTTP, verifies (§6), opens it
   with passphrase / Face ID / YubiKey NFC as usual.
5. Desktop deletes expired drops and old pairs on its next run. The old
   pair goes with the old version; a par2 never describes any version but
   the current one.

## 6. The par2 companion

- Every bundle/drop gets a tiny `.par2` alongside it: block hashes of the
  `.kfb` plus Reed-Solomon recovery blocks (reuse
  `FileKeep.Core.Redundancy.ReedSolomon`; PAR2-style, a few percent of the
  bundle). Format (all integers little-endian):
  `[magic "FKP2"][blockSize:4][blockCount:4][parityBlockCount:4]`
  `[sha256 per data block][sha256 per parity block][rs parity blocks]`.
  Data blocks are 64 KiB; groups of 100 data blocks get 5 parity blocks
  (~5% overhead). The layout is fixed by the builder (`RelayPar2` in this
  repo); both sides read this format: verify any block, repair up to the
  parity count.
- **Proves integrity, enables repair.** Verifies the bundle is bit-identical
  to what the desktop wrote; damaged blocks are repaired from parity
  instead of forcing a re-download.
- **Does NOT prove authorship.** Anyone can par2 any file. The signature
  does the "is this my server?" job; the signed index does version/merge.
  Comparing two par2s for the same bundle name cheaply shows content
  changed, but not which is newer or who changed it.
- **AES-GCM already detects corruption (all-or-nothing).** PAR2 turns
  detection into repair: fix the bad blocks first, then decrypt.
- **Fallback chain (automatic, but logged/shown):**
  1. Good kfb + good par2 → verify (repair if needed).
  2. Good kfb + bad/missing/stale par2 → ignore the par2, open the bundle;
     GCM authentication proves it. Re-pair (regenerate the par2) to restore
     the pair.
  3. Bad kfb + good par2 → repair from parity, then open.
  4. Bad kfb + bad par2 → unrecoverable from this pair; fetch another copy
     (relay / Usenet / NAS).
- **Pairing rule.** Every upload/update re-pairs the par2 *after* the
  upload, from the final bytes on the server. A par2 made before upload
  could describe bytes that never arrived.

## 7. Approvals (phone as second key)

- Desktop needs a restore approved: it writes an `approval-request` message
  (challenge nonce + what is being approved) to the phone's mailbox.
- Phone polls, shows "Approve restore of X?", Face ID, signs the challenge
  with the pairing secret, writes the `approval` message to its outbox
  (via SMB when home — the reply is queued until then if away).
- Desktop verifies the signature and unlocks the restore session.
- Same challenge-response shape as the YubiKey flow, with the phone as the
  token. Pull-based (no Apple Push service needed): the user opens the app
  and sees the pending approval.

## 8. Backwards sync (phone → desktop)

- While away, the phone queues outbound items: approvals, acks,
  phone-created bundles (e.g. camera-roll `.kfb`).
- When SMB is available, it flushes the queue into the relay: replies into
  `mailbox/laptop/`, bundles into `inbox/laptop/` (each with its par2,
  paired after upload, per §6).
- Desktop polls `inbox/laptop/`, verifies signatures and par2s, imports
  bundles, and writes an `ack` to the phone's mailbox. The phone reads the
  ack over HTTP from anywhere. Closed loop, all files.

## 9. Merges (user-selected)

Three-way merge. Base = last HTTP-seen state, theirs = current server
state, mine = phone's queued changes (each tagged with the base sequence
it was made against).

- No overlap → silent union (auto). The common case.
- Genuine overlap on the same item → merge UI on the phone: per item, keep
  mine / take theirs / keep both (combine file sets where it makes sense).
  Each item shows both versions (name, time, size, source).
- Unresolved conflicts hold in the queue until the user decides. Nothing
  is silently overwritten.

## 10. Threat model (summary)

| Attacker / accident | Defense |
|---|---|
| Friend's FileKeep on the same LAN | Signatures + write-gate check; its files fail verification |
| Cloned folder (byte-for-byte copy) | Nonce challenge (deep check); clone can't forge new signed content |
| Rogue LAN writer | Single-writer dirs + signed files; bad signatures ignored |
| Corrupted bundle in transit/storage | par2 verify + repair; GCM fallback; re-pair |
| Stolen phone | Key in Keychain behind Face ID; unpair from desktop kills it |
| Relay operator / server compromise | E2E encryption + signatures; relay sees metadata only |
| Guessed drop URL | Random token names + short expiry + encrypted content |

## 11. Build order

1. **Desktop (this repo):** relay writer (signed index, drops, par2
   pairing, atomic publish), inbox poller (verify, import, ack), pairing
   secret in config, tests.
2. **iOS (FileKeep-iOS repo, main chat, after v0.0.3):** QR-scan pairing,
   HTTP polling, SMB client for writes, par2 verify/repair, queue + merge
   UI, write-gate check.
