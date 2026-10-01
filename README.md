# Usenet Backup

Open-source Windows backup application providing full and incremental
system-image backups, using Usenet as an encrypted long-term storage backend.

## Platform

- Windows 10/11 x64
- C# / .NET 10

## Requirements

1. Full system image backup
2. Incremental block-level backups
3. Windows VSS support
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
| `Vss`           | Volume Shadow Copy integration                          |
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
6. **v0.6 — VSS and system images.** Volume Shadow Copy support, block
   device imaging, bare-metal recovery. ✅ done (`v0.6-vss-system-images`,
   69/69 tests; VSS snapshot path needs Windows validation)
7. **v0.7 — Product.** Windows service, GUI, WiX installer, recovery ISO.

Each milestone gets a git tag (`v0.1-local-repository`, …) so any
broken experiment can be rolled back to a known-good state.

## Building

Milestones 1–2 are platform-independent and build/test on Linux:

```sh
dotnet build UsenetBackup.slnx
dotnet test UsenetBackup.slnx
```

Milestone 6 (VSS, Windows service, recovery environment) requires a
Windows 10/11 machine to build and test.

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
- [x] Milestone 6 (v0.6): VSS and system images — `ISnapshotProvider`
      abstraction (live passthrough + Windows VSS shadow copies via
      `vssapi.dll`, admin rights required), `backup --vss`, raw block-device
      imaging (`backup-disk`/`restore-disk`) through the normal
      chunk/encrypt/hash pipeline with AES-GCM + SHA-256 fails-closed
      restore, additive manifest `kind`/`snapshot` fields. Tagged
      `v0.6-vss-system-images`. Note: the VSS COM path compiles
      cross-platform but can only be exercised on Windows (vtable order
      flagged for re-verification against the SDK's `vss.h` there); all
      platform-independent behavior is tested on Linux.
- [ ] Milestone 7 (v0.7): Windows service, GUI, installer, recovery ISO
