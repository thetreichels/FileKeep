# Usenet Backup

Open-source Windows backup application providing full and incremental
system-image backups, using Usenet as an encrypted long-term storage backend.

## Platform

- Windows 10/11 x64
- C# / .NET 10

## Requirements

1. Full system image backup
2. Incremental block-level backups
3. Windows backup-privilege file access (read locked files)
4. Client-side authenticated encryption
5. Deduplication
6. Configurable chunk size
7. SHA-256 integrity hashes
8. PAR2 recovery data
9. NZB generation
10. NNTP upload
11. NZB-based restoration
12. Multiple Usenet providers
13. Backup repository catalog
14. Backup expiration monitoring
15. Automatic repository verification
16. Windows service for scheduled backups
17. GUI for configuration and restoration
18. Command-line interface for automation
19. Bootable recovery environment
20. No proprietary dependencies

## Architecture

| Module        | Responsibility                                          |
|---------------|---------------------------------------------------------|
| `BackupEngine`  | Orchestrates backup jobs                              |
| `Repository`    | On-disk catalog, manifests, chunk index                 |
| `ChunkStore`    | Content-addressed chunk storage (dedup by SHA-256)      |
| `Encryption`    | Authenticated encryption (AES-256-GCM; boring, standard)|
| `Deduplication` | Cross-backup dedup via chunk hash index                 |
| `Manifest`      | Versioned backup manifests (`BACKUP FORMAT v1`)         |
| `BackupPrivilege` | Windows SeBackupPrivilege locked-file reads          |
| `Usenet`        | Backend abstraction (local / NAS / Usenet)              |
| `Nntp`          | NNTP upload/download with resume                        |
| `Nzb`           | NZB manifest generation and parsing                     |
| `Par2`          | Parity / recovery data                                  |
| `Restore`       | Reconstruct VHD/VHDX from repository or NZB+Usenet      |
| `Scheduler`     | Windows service for scheduled backups                   |
| `Recovery`      | Bootable recovery environment                           |
| `UI`            | GUI for configuration and restoration                   |
| `CLI`           | Command-line interface for automation                   |

## Rules

- Do not implement cryptographic algorithms ourselves. Use established,
  boring, conventional implementations and authenticated encryption.
- Do not store encryption keys with backup data.
- Every backup must be independently verifiable.
- Every uploaded object must have a cryptographic hash.
- Interrupted uploads must resume.
- Interrupted downloads must resume.
- Never delete repository data without explicit retention logic.
- All critical operations must be logged.
- All modules must have automated tests.
- Do not proceed to the next subsystem until the current subsystem
  builds and its tests pass.

## Backup format stability

`BACKUP FORMAT v1` is defined in `docs/FORMAT-v1.md` before the first
production backup is uploaded. Future versions add `v2`, `v3` support —
they never silently change `v1`.

## Development order (incremental milestones)

1. **v0.1 — Local repository engine.** Directory → chunk → encrypt →
   store → manifest. Authenticated encryption (AES-256-GCM, PBKDF2
   key derivation) included, since milestone 1's own spec requires
   encrypted chunks. Full round-trip reconstruction verified on test
   data. No Usenet yet. ✅ done
2. **v0.2 — Incrementals.** FULL → INC → INC chains against a parent
   manifest; tests prove only changed blocks produce new chunks.
3. **v0.3 — Usenet backend.** NNTP adapter behind the storage
   interface, independent of the repository engine. Minimal NNTP client
   (AUTH, POST, STAT, ARTICLE), yEnc article codec with CRC-32, deterministic
   message-IDs, resumable uploads via a catalog journal. ✅ done
   (`v0.3-nntp-backend`, 46/46 tests)
4. **v0.4 — NZB generation.** NZB 1.1 index per backup: one file per chunk,
   one segment per article, deterministic message-IDs, exact article byte
   sizes, journal-backed upload dates, head meta with the manifest root
   hash. CLI `nzb-generate` warns about chunks with no upload record. ✅ done
   (`v0.4-nzb-generation`, 54/54 tests)
5. **v0.5 — Download/recovery pipeline.** NZB download, repair, decrypt,
   verify, restore.
6. **v0.6 — Locked-file reads and system images.** Windows backup-privilege
   file access (`SeBackupPrivilege`, bypasses exclusive locks), block
   device imaging, bare-metal recovery. ✅ done (`v0.6-vss-system-images`,
   69/69 tests; backup-privilege path needs Windows validation)
7. **v0.7 — Product.** Windows service, GUI, WiX installer, recovery ISO.
   ✅ done (`v0.7-service-gui-installer`, 85/85 tests; service + localhost
   web dashboard + PS installer + recovery runbook — WiX MSI and WinPE ISO
   built and validated on Azure VM 2026-10-04; see VERIFICATION_REPORT.md)
8. **USB recovery wizard.** WinForms wizard (`usenet-backup-recovery`,
   self-contained for WinPE): repo location → credentials (with early
   passphrase check) → pick backup → download → verify → restore, with
   typed device-path confirmation for disk restores. Workflow logic in
   `UsenetBackup.Core/Recovery/WizardState.cs`, tested (108/108).

Each milestone gets a git tag (`v0.1-local-repository`, …) so any
broken experiment can be rolled back to a known-good state.

## Building

Milestones 1–2 are platform-independent and build/test on Linux:

```sh
dotnet build UsenetBackup.slnx
dotnet test UsenetBackup.slnx
```

The backup-privilege file-access path (milestone 6) and running as a Windows service
(milestone 7) require a Windows 10/11 machine to execute; both compile
cross-platform and fail fast with a clear error elsewhere. The service
also runs in `--console` mode on any OS (useful for testing), and the
dashboard is a localhost web UI reachable from any browser.

## Service & dashboard (milestone 7)

`usenet-backup-service` runs scheduled backups as a Windows service
(raw SCM P/Invoke, no extra dependencies) and serves a localhost web
dashboard (the GUI) with job status, manual "run now", backup lists and
the operations log:

```sh
# console mode (any OS) — handy for trying it out:
USENETBACKUP_PASSPHRASE=... usenet-backup-service --console --config service.json
# then open http://127.0.0.1:15789/
```

`service.json` (see `src/UsenetBackup.Service/service.example.json`)
defines jobs: repo, source, schedule (`"daily HH:mm"` or
`"interval N"` minutes), mode (`incremental` — falls back to full when
no parent exists — or `full`), and `backup-privilege` for locked-file reads.
The passphrase comes from the `USENETBACKUP_PASSPHRASE` environment
variable; scheduled runs fail fast with a clear error when it is missing.
On Windows, `install/install.ps1` (run as admin) publishes, registers
and starts the service.

Windows-only paths (backup-privilege file access, SCM hosting) are validated by
`validate/validate-windows.ps1` — run it as admin on a Windows 10/11
machine with the .NET SDK (or prebuilt binaries); it publishes, then
checks backup-privilege locked-file backup, disk-image
round-trip, and service install → dashboard run → uninstall, cleaning
up afterwards.

Bare-metal recovery is documented in `docs/RECOVERY.md`: the CLI doubles
as the WinPE recovery tool — boot WinPE, reassemble the repo metadata
(`repo.json`, `manifests/`, `catalog.db`), `download` the chunks from
Usenet, `verify`, then `restore` (or `restore-disk` for images).

## Security notes

- **Passphrase handling.** The encryption key is derived at runtime
  (PBKDF2-HMAC-SHA-512, 600,000 iterations) and zeroed on dispose; it is
  never written to the repo. Prefer the `USENETBACKUP_PASSPHRASE`
  environment variable or the interactive prompt — `--passphrase` on the
  command line is visible in the process list (the CLI warns about this).
  The Windows service reads the passphrase from a machine-level
  environment variable; treat that machine as trusted, or use DPAPI/a
  secret store instead.
- **What's public.** Chunk message-IDs posted to Usenet embed the
  SHA-256 of the plaintext chunk (required for content addressing and
  dedup). Ciphertext is safe, but anyone with an NZB can confirm guesses
  about plaintext. Filenames are plaintext in manifests (v1 tradeoff for
  independent verifiability). There is no forward secrecy: a compromised
  passphrase decrypts all past Usenet posts.
- **Destructive commands.** `restore-disk` overwrites a block device; it
  requires typing the device path to confirm (or `--yes` for scripts) and
  refuses when stdin isn't interactive.
- **Dashboard.** Binds to loopback only, no login. State-changing calls
  require a per-startup CSRF token (embedded in the served page), but
  treat it as single-user: don't expose the port, and don't run it on a
  shared machine without a reverse proxy.
- **Service account.** The installer defaults to LocalSystem (needed for
  backup privilege); pass `-ServiceAccount "NT SERVICE\UsenetBackup"` for least
  privilege when no job uses backup privilege.
- **Dependencies.** `SQLitePCLRaw.lib.e_sqlite3` was upgraded from 2.1.11 to 2.1.12
  on 2026-10-04 to patch CVE-2025-6965 (High, CVSS 9.8). `dotnet list package
  --vulnerable` reports clean.

## Status

- [x] Milestone 1 (v0.1): local repository engine — directory backup,
      chunking, AES-256-GCM encryption, manifest, round-trip restore.
      Tagged `v0.1-local-repository`.
- [x] Milestone 2 (v0.2): incrementals — parent-linked, self-contained
      manifests, size+mtime fast path. Tagged `v0.2-incrementals`
      (plus `v0.2.1` requirements-compliance revision).
- [x] Milestone 3 (v0.3): Usenet backend adapter — NNTP client, yEnc
      articles, deterministic message-IDs, resumable uploads.
      Tagged `v0.3-nntp-backend`.
- [x] Milestone 4 (v0.4): NZB generation. Tagged `v0.4-nzb-generation`.
- [x] Milestone 5 (v0.5): download/recovery pipeline — NZB parsing,
      resumable chunk download with per-chunk authentication and
      hash verification. Tagged `v0.5-download-pipeline`.
- [x] Milestone 6 (v0.6): locked-file reads and system images — `ISnapshotProvider`
      abstraction (live passthrough + Windows backup-privilege reads via
      `SeBackupPrivilege`/`FILE_FLAG_BACKUP_SEMANTICS`, admin rights required),
      `backup --backup-privilege`, raw block-device imaging
      (`backup-disk`/`restore-disk`) through the normal chunk/encrypt/hash
      pipeline with AES-GCM + SHA-256 fails-closed restore, additive manifest
      `kind`/`snapshot` fields. Tagged `v0.6-vss-system-images` (tag predates
      the rename; the mechanism was never Volume Shadow Copy). Note: the
      backup-privilege path compiles cross-platform but can only be exercised
      on Windows; all platform-independent behavior is tested on Linux.
      Reads the live files — not a point-in-time copy.
- [x] Milestone 7 (v0.7): Windows service, GUI, installer, recovery —
      `usenet-backup-service`: SCM-hosted Windows service (raw P/Invoke,
      zero new dependencies) running scheduled `daily HH:mm` / `interval N`
      backup jobs from `service.json` with failure tracking and resume;
      localhost web dashboard (status, run-now, backups, operations log);
      admin PowerShell installer (`install/install.ps1`); bare-metal
      recovery runbook (`docs/RECOVERY.md`, CLI as the WinPE tool).
      Tagged `v0.7-service-gui-installer`. Not done here: WiX MSI packaging
      and a purpose-built WinPE ISO need Windows tooling.
