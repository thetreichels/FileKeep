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

1. **v0.1 — Local repository engine.** Directory → chunk → compress →
   encrypt → store → manifest. Full round-trip reconstruction verified
   on test data. No Usenet yet.
2. **v0.2 — Encryption.** Authenticated encryption, key derivation,
   tamper-detection tests.
3. **v0.3 — Incrementals.** FULL → INC001 → INC002 chains; tests prove
   only changed blocks are captured.
4. **v0.4 — VSS.** Volume Shadow Copy support for live system imaging.
5. **v0.5 — NZB generation.** Encrypted chunks → NNTP articles → NZB files.
6. **v0.6 — NNTP upload.** Usenet backend with resume.
7. **v0.7 — NNTP download.** Restore path from Usenet.
8. **v0.8 — Recovery.** Bare-metal restore, VHD/VHDX reconstruction,
   bootable recovery environment.
9. **v0.9 — Service, GUI, installer.** Windows service, GUI, CLI,
   WiX-based installer, recovery ISO.

Each milestone gets a git tag (`v0.1-local-repository`, …) so any
broken experiment can be rolled back to a known-good state.

## Building

Milestones 1–3 are platform-independent and build/test on Linux:

```sh
dotnet build UsenetBackup.slnx
dotnet test UsenetBackup.slnx
```

Milestones 4+ (VSS, Windows service, recovery environment) require a
Windows 10/11 machine to build and test.

## Status

- [ ] Milestone 1: local repository engine (directory backup, chunking,
      AES-256-GCM encryption, manifest, round-trip restore) — in progress
- [ ] Milestone 2: hardened encryption module
- [ ] Milestone 3: incrementals
- [ ] Milestone 4: VSS
- [ ] Milestone 5: NZB generation
- [ ] Milestone 6: NNTP upload
- [ ] Milestone 7: NNTP download
- [ ] Milestone 8: recovery
- [ ] Milestone 9: service, GUI, installer
