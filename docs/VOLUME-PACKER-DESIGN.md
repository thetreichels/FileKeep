# Volume Packer — Design

Status: approved for implementation (user: "Yes, build the volume packer", 2026-10-09).
Branch: `volume-packer`.

## Goal

Reduce the sheer number of articles posted to Usenet by packing multiple encrypted
chunk blobs into a single **volume** article. Chunking (dedup granularity) and
crypto are untouched — packing happens at the upload layer only.

With the default 4 MiB chunk size and 32 MiB volumes, a 1 GiB backup posts
~32 volume articles instead of ~256 chunk articles (+ parity + manifest + index).

## Non-goals

- Changing dedup granularity (stays at `chunk_size`).
- Changing chunk crypto (chunks are encrypted *before* packing; volumes are opaque).
- Touching v1 chunk backups: per-chunk NZBs keep working; the NZB selects the path.

## Volume format (binary)

```
magic:      4 bytes  "VOL1"
count:      int32 LE, number of chunk entries (>= 1)
per chunk:
    id:     32 bytes raw (64-hex chunk ID decoded)
    offset: int64 LE, offset into payload
    length: int32 LE, encrypted blob length
payload:    concatenated encrypted chunk blobs
```

- **Volume ID** = SHA-256 hex of the complete volume bytes. Deterministic:
  same chunks + same target size always yield the same volume IDs, so uploads
  stay idempotent and STAT-based existence checks need no index.
- **Unpack validation (fail closed):** magic, 1 <= count <= 1,000,000,
  every offset/length inside payload bounds, entries non-overlapping,
  total size consistent. Violations throw `InvalidDataException`.
- A volume always holds >= 1 chunk: a single chunk larger than the target
  gets a volume of its own.

## Packing policy

- Input: `ChunkOrdering.GetOrderedChunkIds(manifest)` — deduped, ordinal-sorted,
  the same canonical order parity and NZB generation already use.
- Greedy fill: append chunk blobs until the next blob would exceed the target;
  then start a new volume.
- Target size: repo.json `volume_size_bytes`, default **32 MiB** (33,554,432).
  yEnc adds ~2-3%, so a 32 MiB volume posts as a ~33 MiB article.
- Toggle: repo.json `use_volumes` (default true); CLI `nntp-upload --no-volumes`
  and `init --volume-size BYTES`.

## Message IDs and articles

- `ArticleCodec.MakeVolumeMessageId(volumeIdHex, repoId)` ->
  `<volumeid.repoid@usenet-backup>` — deliberately the *same shape* as chunk
  message-IDs, so `NzbParser`'s strict parser, `ChunkMessageIndex`, and the
  catalog journal work unchanged.
- Article headers: `Subject: [usenet-backup] volume <id>`,
  `X-UsenetBackup-Volume: <id>`, `X-UsenetBackup-Format: v1`, then yEnc body.
- `NntpBlobStore.PutVolume(id, bytes)` / `GetVolume(id)` mirror `Put`/`Get`
  (64-hex validation, journal fast path, STAT fallback, `WaitForArticle`).
- Upload guard: posting refuses with a clear error when the article would
  exceed `MaxArticleBytes` ("lower volume_size_bytes"), instead of a bare
  server 441 mid-POST.
- Retention refresh: `MakeRefreshVolumeMessageId` =
  `<volid.repoid.refresh.<unixTime>.<nonce>@usenet-backup>`; republished
  volumes record the new identity in the shared `ChunkMessageIndex`
  (its providerKey -> id -> messageId structure is ID-agnostic).

## Manifest (additive, null-ignored — old manifests verify unchanged)

- `volumes`: list of `{ id, chunk_ids, size_bytes }`, omitted when the backup
  was uploaded per-chunk.
- `volume_size`: target bytes used for this backup's packing (informational).
- Upload flow: `nntp-upload` / scheduler compute the packing, set
  `manifest.Volumes`, save the manifest (root hash recomputed), post volumes,
  then the existing `UploadManifest`, then NZB generation from the updated
  manifest. Manifest is always consistent before anything is posted.

## NZB

- Volume mode: one `<file>` per volume, one `<segment>` carrying the volume
  message-ID, `bytes` = article size, `date` = volume upload time
  (`Catalog.GetUploadTimeUtc(volumeId)` — the uploads table already keys by
  content ID; PAR2 parity rows do the same today).
- Head meta keeps all existing fields and adds `x-usenetbackup-volume-count`.
- `NzbParser` reads head meta into `NzbDocument.Meta`; `IsVolumeNzb` is true
  when `x-usenetbackup-volume-count` is present and > 0. Old NZBs lack it ->
  chunk path, unchanged.
- `NzbFile.VolumeId` recovered from the first segment via the existing
  strict message-ID parse.

## Download

- `BackupRepository` dispatches on `nzb.IsVolumeNzb`: chunk NZB -> existing
  `DownloadChunks`; volume NZB -> new `DownloadVolumes`.
- Per volume: fetch via `GetVolume` (validates the `X-UsenetBackup-Volume`
  header matches), `VolumePacker.Unpack`, then the *existing*
  `VerifyDownloadedBlob` per chunk (size guard, AES-GCM decrypt, SHA-256
  == chunk ID, fail closed). Unpack-then-verify means the oversized-chunk
  guard needs no changes.
- Journal: `RecordDownload(volumeMessageId, volumeId)` once per volume.
  Resume: skip a volume when all its chunks exist locally (plan from the
  manifest's `volumes`, loaded by backup ID from NZB meta; falls back to
  fetch-and-check when the manifest is unavailable).
- `store.MaxArticleBytes` on download must cover volumes, not chunks:
  `max(repo.MaxDownloadBytes, maxVolumeBytes * 2)`.

## Parity over volumes

- `XorParity` / `Par2Redundancy` already take `IReadOnlyList<string>` IDs +
  `Func<string,byte[]>` — ID-agnostic. Groups are positional over
  canonically sorted **volume** IDs; `MakeParityId` works unchanged.
- Scheduler parity phase posts parity blobs via `PutVolume`; reconstruction
  fetches the volume group + parity articles, rebuilds missing **volume
  bytes**, unpacks, and re-verifies every chunk through
  `VerifyDownloadedBlob` (reconstruction can't smuggle a bad blob).
- **XOR parity IDs fixed 2026-10-10** (user decision: fix, not deprecate):
  `XorParity.MakeParityId` was `parity-xor-<64hex>` (75 chars, rejected by
  the NNTP layer's 64-hex validation — `xor` mode was broken end-to-end).
  Now `SHA-256("xor:" + sorted IDs)` → 64 hex, NNTP-safe like PAR2's
  `SHA-256("par2:{i}:" + sorted IDs)`. Domain separators keep the two
  schemes from colliding. Old xor parity articles were never postable, so
  nothing recoverable was lost.

## Retention

- `RetentionManager` samples **volumes** (from `manifest.volumes`),
  STAT-checks via `ExistsOnServer(volumeId)` (index-aware, handles refresh
  identities), treats check failures as missing (conservative, as today).
- Any miss -> full republish: repack all volumes from local chunks, post
  each with a refresh message-ID (`RepublishVolumeWithNewIdentity`),
  `RecordNewIdentity`, post the updated message index, *then* advance the
  `UsenetUploadTracker` clock. Same ordering guarantees as the chunk path.
- `UsenetUploadTracker` unchanged (per-backup records).

## Catalog

No schema change. The `uploads`/`downloads` tables key `message_id`, and
their `chunk_id` column already stores non-chunk content IDs (PAR2 parity
rows); volume rows follow the same convention.

## nntp-check

New opt-in `--probe-post-size <MB>`: posts a single probe article of that
size (random bytes, `[usenet-backup] probe` subject) to the newsgroup,
STAT-waits, and reports accept/reject with classification. Opt-in because
the probe article permanently lands in the group.

## Dashboard

`UploadProgressTracker.totalUnits` counts volumes (+ parity volumes + 2)
instead of chunks when packing is active; phase labels unchanged.

## Docs

`docs/FORMAT-v1.md` gains a "Volume packing" section: binary format,
message IDs, NZB mapping, manifest fields.

## Tests

- `VolumePackerTests`: round-trip, multi-volume split on target size,
  oversized single chunk gets its own volume, empty input, deterministic
  volume IDs, corrupt magic / out-of-bounds offset / overlapping entries
  rejected.
- E2E via `FakeNntpServer`: pack -> post -> NZB -> wipe local -> download ->
  verify -> restore byte-identical.
- Parity-over-volumes: drop one volume article, reconstruct, verify.
- Retention: volume republish mints new identity, index updated.

## Validation

- Sandbox: build 0 warnings; new-code tests via console harness
  (`dotnet test` testhost hangs in the sandbox).
- Azure VM: full solution build, entire xunit suite, live Frugal Usenet
  loop with volumes (init -> backup -> upload -> NZB -> wipe -> download
  -> verify -> restore, SHA-256 identical).
