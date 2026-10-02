# Bare-metal recovery runbook

This is the "separate narrow WinPE recovery application" from the roadmap:
in practice it is the `usenet-backup` CLI itself, which has no GUI or
service dependencies and runs from WinPE (use a self-contained publish so
no runtime install is needed). Print this file and keep it with your
offline metadata copy.

## What to keep somewhere safe (offline, off-machine)

The bulky `chunks/` directory lives on Usenet. Everything else is small —
keep a copy on USB and/or in a password manager:

- `repo.json` — repository salt and parameters. **Without the same salt,
  the deterministic Usenet message-IDs cannot be recomputed**, so this
  file is mandatory for downloading your chunks back.
- `manifests/` — one JSON per backup (what to restore, chunk lists, hashes).
- `catalog.db` — SQLite catalog with the upload/download journals
  (needed by `nzb-generate` and resume).
- Your NZB files (or regenerate them later with `nzb-generate`).
- Usenet provider hostname, username and password.
- The repository passphrase.
- A published copy of the `usenet-backup` CLI (`win-x64`, self-contained).

## Recovery scenarios

### A. Files are gone but the machine boots

On the machine (or any Windows PC):

```powershell
# 1. Recreate the repo metadata from your offline copy:
mkdir C:\RestoreRepo
#    copy repo.json, manifests\, catalog.db into C:\RestoreRepo

# 2. Download the chunks from Usenet (needs provider credentials):
$env:USENETBACKUP_PASSPHRASE = "<passphrase>"
usenet-backup download C:\RestoreRepo backup.nzb --host <provider> --user <user>
#    (you will be prompted for the NNTP password, or pass --password)

# 3. Verify, then restore:
usenet-backup verify C:\RestoreRepo <backup-id>
usenet-backup restore C:\RestoreRepo <backup-id> C:\Restored
```

`download` is resumable and authenticates every chunk (yEnc CRC-32 →
chunk-ID check → AES-GCM → SHA-256) before storing it; bad chunks are
never journaled. `verify` re-checks the manifest root hash and every
chunk before you trust the restore.

### B. Whole disk / bare metal (WinPE)

1. Boot WinPE with networking (`wpeinit`, then `ipconfig` to confirm).
2. From your USB stick (or a network share), get:
   `usenet-backup.exe` (self-contained), `repo.json`, `manifests/`,
   `catalog.db`, and the NZB of the disk-image backup.
3. Reassemble the repo metadata as in scenario A on a scratch volume
   (e.g. `X:\repo` — WinPE RAM disk — for small repos, or a USB disk).
4. Download the image chunks from Usenet:
   ```
   usenet-backup download X:\repo disk-backup.nzb --host <provider> --user <user>
   ```
5. Write the image to the target drive (**destructive** — triple-check
   the drive number; `diskpart → list disk`):
   ```
   usenet-backup restore-disk X:\repo <backup-id> \\.\PhysicalDrive0
   ```
   Every chunk is decrypted and hash-verified during the write; a
   mismatch aborts with an error instead of writing corrupt data.
6. Reboot from the restored drive.

### C. The repo metadata itself is lost

If `repo.json`/`manifests/`/`catalog.db` are gone but you have the
passphrase and an NZB: the NZB lists every chunk's message-ID and byte
size, but not the manifest (file names, chunk order per file). File-level
restore is not possible from the NZB alone. **This is why the metadata
backup matters** — it is kilobytes; keep several copies.

## Notes

- The passphrase is never stored in the repo. Without it, chunks are
  AES-256-GCM ciphertext and unrecoverable — there is no back door.
- Message-IDs are `<chunk-id>.<repo-id>@usenet-backup` where `repo-id =
  SHA-256(salt)[..16]`; a repo initialized with a *different* salt (or a
  different passphrase-derived key) cannot read articles posted by the
  original repo. Always restore the original `repo.json`.
- After any restore, run `verify` before trusting the data.
