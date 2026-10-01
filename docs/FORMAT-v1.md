# BACKUP FORMAT v1

Frozen specification. Newer versions may ADD `v2`, `v3`, … — they must
never silently change `v1`. Every reader must support `v1` forever.

## Repository layout

```
<repo>/
  repo.json        # repository config (plaintext)
  catalog.db       # SQLite index: chunks, backups (rebuildable)
  operations.log   # append-only audit log of critical operations
                   # (init, backup, restore, verify); informational only,
                   # not covered by any hash, never required for restore
  manifests/
    <backup-id>.json
  chunks/
    <hh>/<rest-of-hex>    # one file per unique chunk
  parity/                 # reserved for a later milestone (PAR2); empty in v1
```

`catalog.db` is a cache/index. Deleting it must never lose data: it can
be rebuilt by scanning `chunks/` and `manifests/`.

## repo.json

```json
{
  "format_version": "v1",
  "created_utc": "2026-09-29T23:00:00Z",
  "kdf": "pbkdf2-hmac-sha512",
  "kdf_iterations": 600000,
  "kdf_salt_b64": "<32 random bytes, base64>",
  "chunk_size": 4194304
}
```

The salt is not secret. The key is never stored; it is derived from the
user's passphrase at runtime.

## Chunking (v1)

- Fixed-size chunking. Default 4 MiB, configurable per repository
  (`chunk_size` in `repo.json`). Last chunk of a file may be smaller.
- Chunk ID = lowercase hex of **SHA-256(plaintext chunk)**.
- Deduplication: chunks are content-addressed by chunk ID. A chunk
  already present is never written twice, across all backups in the
  repository.

## Encryption (v1)

Boring, standard, no invented cryptography:

- Algorithm: AES-256-GCM (`System.Security.Cryptography.AesGcm`).
- Key: 32 bytes from PBKDF2-HMAC-SHA-512(passphrase, salt, 600000).
- Nonce: 12 fresh random bytes per chunk.
- AAD: the 32-byte chunk ID. This binds each ciphertext to its chunk ID
  and defeats chunk-swapping inside the repository.
- Stored blob layout: `nonce (12) || ciphertext || tag (16)`.
- Stored at `chunks/<first-2-hex-chars>/<remaining-62-hex-chars>`.
- Compression: field present from day one, value `"none"` in v1.
  (Compression, when added in a later format version, always happens
  *before* encryption.)

## Manifest (v1)

`<backup-id>.json`, plaintext JSON, UTF-8:

```json
{
  "format_version": "v1",
  "backup_id": "<uuid>",
  "type": "full",
  "created_utc": "...",
  "source": "C:\\Users\\…",
  "chunk_size": 4194304,
  "compression": "none",
  "files": [
    {
      "path": "relative/path.txt",
      "size": 12345,
      "mtime_utc": "...",
      "sha256": "<hex of plaintext file>",
      "chunks": ["<chunk-id>", "…"]
    }
  ],
  "directories": ["empty-dir", "nested/empty-subdir"],
  "root_sha256": "<hex: SHA-256 over canonical JSON of this manifest minus root_sha256>"
}
```

Notes:

- `path` is relative to the backup source root, `/`-separated.
- `directories` lists every directory in the source tree, relative and
  `/`-separated, sorted (the source root itself is excluded). It is
  omitted when the source contains no subdirectories. Restore recreates
  these so the tree round-trips exactly, including empty directories.
  Like `parent_id`, it is additive and optional: manifests written
  before this field existed verify unchanged.
- Symlinks are recorded as entries with `"symlink_target"` and are not
  followed (v1).
- Filenames are plaintext in v1. Encrypting the manifest is a possible
  future format version; v1 optimizes for independent verifiability.
- `type` is `"full"` for full backups, `"inc"` for incremental backups.
  An incremental manifest carries `"parent_id"` (the backup it was
  diffed against) and is **self-contained**: it lists every file present
  at backup time with complete chunk lists, exactly like a full manifest.
  Unchanged files simply reference already-stored chunks. Because of
  this, a reader that only understands `"full"` can still verify and
  restore an `"inc"` manifest — `parent_id` is informational, never
  required for restore. Adding these two additive, optional fields does
  not change any v1 semantic: every v1 full backup remains byte-identical
  to this spec.
- Files present in the parent but deleted from the source are absent
  from the incremental manifest. Restoring an incremental therefore
  reproduces the source tree as it was at that backup's time, not a
  union with the parent.

## Incrementals (v1, milestone 2)

Change detection is per file, using size + mtime:

- A file whose path, size, and `mtime_utc` all match the parent entry
  is assumed unchanged: its chunk list and `sha256` are copied from the
  parent without re-reading the file.
- Any new file, or any file whose size or mtime differs, is re-chunked
  in full. Content addressing means unchanged chunks are not stored
  twice — only genuinely new chunk blobs are written.

This is conservative by design: a file whose mtime changed but whose
content did not is re-chunked (cheap; dedup absorbs it), while a file
whose content changed without touching size+mtime is the caller's
responsibility to flag (same tradeoff as rsync).

## Verification (v1)

A backup is verified by:

1. Recomputing `root_sha256` over the manifest (tamper-evident manifest).
2. For each file: reading each chunk blob, splitting nonce/ciphertext/tag,
   AES-GCM-decrypting with AAD = chunk ID (fails closed on any bit flip),
   SHA-256-hashing the plaintext and comparing to the chunk ID.
3. Reassembling each file and comparing its SHA-256 to the manifest entry.

Any failure aborts with the offending chunk/file identified. There is no
"best effort" partial restore in v1: verification is all-or-nothing per
file, and the manifest root hash covers the whole backup.

## Usenet articles (v1, milestone 3)

Each chunk blob (nonce || ciphertext || tag, exactly as stored locally) is
posted as **one NNTP article** with a yEnc-encoded body. The article format
is part of v1 so that any v1 reader can fetch and decode articles; NZB
generation (which indexes these articles) is still a later milestone.

**Message-ID.** Deterministic: `<chunkid.repoid@usenet-backup>`, where
`chunkid` is the 64-char lowercase hex chunk ID and `repoid` is the first
16 hex chars of SHA-256(repository salt). Determinism makes uploads
idempotent (re-posting an existing message-ID is a server-side no-op),
makes existence checks possible with a bare STAT, and means a download
needs nothing but the chunk ID — no article index to lose.

**Headers.**

```
From: usenet-backup
Newsgroups: <configured group>
Subject: [usenet-backup] chunk <chunkid>
Message-ID: <chunkid.repoid@usenet-backup>
X-UsenetBackup-Chunk: <chunkid>
X-UsenetBackup-Format: v1
```

`X-UsenetBackup-Chunk` carries the cryptographic hash of every uploaded
object (the chunk ID is SHA-256 of the plaintext). A reader MUST compare
it to the requested chunk ID and reject a mismatch.

**Body.** yEnc (`=ybegin`/`=yend`, 128-char lines) with the `size` and
`crc32` trailer fields. A reader MUST verify both before accepting the
blob; the blob is then decrypted and re-hashed exactly as in local
verification.

**Resume.** The repository catalog records every posted message-ID in an
`uploads` journal. Before posting, the uploader checks the journal, then
falls back to STAT on a journal miss (covers a lost journal and server-side
expiry), and only posts when the server lacks the article. An interrupted
upload therefore resumes without re-posting.

## NZB indexes (v1, milestone 4)

An NZB 1.1 document indexing one backup: **one `<file>` per unique chunk,
one `<segment>` per file** (v1 posts one article per chunk). Chunk files
are sorted by chunk ID, so the same manifest always yields byte-identical
NZB output — an NZB can be regenerated at any time without contacting a
server.

- `<file>`: `poster` = the article `From` value, `date` = Unix time of the
  upload (from the `uploads` journal; falls back to the backup's creation
  time when the chunk has no journal record), `subject` = the article
  subject (`[usenet-backup] chunk <chunkid>`).
- `<segment>`: `bytes` = the exact article size in bytes (headers + yEnc
  body as the uploader would post it), `number` = `1`, body = the article
  message-ID **with** angle brackets.
- `<head>` meta: `title`, `x-usenetbackup-generator`,
  `x-usenetbackup-backup-id`, `x-usenetbackup-root-sha256`,
  `x-usenetbackup-chunk-count`. The root hash lets a downloader confirm the
  NZB describes the intended backup before fetching anything.

The NZB references articles; it does not prove they exist on any server.
Generate after `nntp-upload` (or treat a missing journal record as
"not known to be posted"). The manifest itself is not posted — see
recovery below.

## Download/recovery (v1, milestone 5)

A backup is recovered from Usenet by parsing its NZB and fetching every
referenced article:

1. Parse the NZB (`NzbParser`); extract each file's chunk ID from its
   segment message-ID (`<{chunkid}.{repoid}@usenet-backup>`).
2. For each chunk: `ARTICLE <message-id>`, then validate in order —
   yEnc `size`/`crc32` trailer, `X-UsenetBackup-Chunk` header matches the
   expected chunk ID, AES-256-GCM decryption authenticates, and
   SHA-256(plaintext) equals the chunk ID. A chunk failing any check is
   rejected and never recorded.
3. Store verified blobs in the local chunk store.

**Resume.** The catalog records every verified download in a `downloads`
journal (message-ID → chunk ID). Chunks already present locally are
skipped (and adopted into the journal), so an interrupted download
resumes by re-running; only missing chunks are fetched.

**Same-repo requirement.** Message-IDs embed the repo ID, which is
SHA-256(repo salt)[..16]. Downloading therefore requires the original
repository directory (it holds the salt, manifests, and catalog) — the
NZB plus Usenet alone are not sufficient. Keep a copy of the repo
metadata (small: `repo.json`, `manifests/`, `catalog.db`) somewhere safe;
the bulky `chunks/` directory is what Usenet replaces.

## What v1 deliberately excludes

Compression, PAR2 parity, VSS snapshots — all reserved for later
milestones and later format versions. (The NNTP article format and the NZB
mapping above are defined in v1 as of milestones 3 and 4; multi-article
chunk splitting is not yet needed and not specified.)
