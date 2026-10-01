# BACKUP FORMAT v1

Frozen specification. Newer versions may ADD `v2`, `v3`, … — they must
never silently change `v1`. Every reader must support `v1` forever.

## Repository layout

```
<repo>/
  repo.json        # repository config (plaintext)
  catalog.db       # SQLite index: chunks, backups (rebuildable)
  manifests/
    <backup-id>.json
  chunks/
    <hh>/<rest-of-hex>    # one file per unique chunk
  parity/                 # reserved for v0.8 (PAR2); empty in v1
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
  "root_sha256": "<hex: SHA-256 over canonical JSON of this manifest minus root_sha256>"
}
```

Notes:

- `path` is relative to the backup source root, `/`-separated.
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

## What v1 deliberately excludes

Compression, PAR2 parity, VSS snapshots, NNTP/NZB — all reserved for
later milestones and later format versions.
