# VSS Design for FileKeep System-Image Backup

**Status:** Design phase — not implemented
**Parent:** docs/ROADMAP.md §1

This document designs the Volume Shadow Copy Service integration that
FileKeep needs before bare-metal image backup can be called
production-ready.

---

## 1. Why VSS

A file-by-file copy of a live Windows system disk is not a backup — it
is a race against the operating system. Without a snapshot:

- Files change between the directory scan and the read (torn backup set)
- Registry hives may be mid-write (unbootable restore)
- Databases (including FileKeep's own SQLite catalogs) may be corrupt
- NTFS metadata ($MFT) may not match file contents

VSS solves this by asking Windows to freeze I/O momentarily, flush
buffers, notify registered writers (SQL Server, Exchange, Hyper-V, etc.),
and create a copy-on-write snapshot. FileKeep then reads from the
snapshot — a point-in-time, crash-consistent view — while the live system
continues running.

---

## 2. Consistency levels

FileKeep must distinguish these explicitly in UI and documentation:

| Level | Mechanism | Guarantee | Use case |
|-------|-----------|-----------|----------|
| File backup | Direct file copy | None across files | User documents |
| Crash-consistent image | VSS snapshot, no writer coordination | Filesystem consistent; apps may have lost in-flight data | System disk, bootable restore |
| Application-consistent image | VSS snapshot with writer coordination | Databases and apps in clean state | Servers, mail stores, DB hosts |

The backup job configuration must record which level was requested and
which was actually achieved. If VSS is unavailable, the job must fail
closed — never silently degrade to a live copy and label it an "image."

---

## 3. Options evaluated

### 3a. AlphaVSS (managed .NET wrapper)

- **What:** C#/C++-CLI wrapper around VSS COM interfaces. MIT license.
- **Status:** Original project (alphaleonis/AlphaVSS) is **inactively
  maintained**. A 2025 fork (virbula2025/AlphaVSS) exists but has no
  releases, no stars, and no track record.
- **Pros:** Full VSS API surface; idiomatic .NET.
- **Cons:** Depends on C++/CLI (problematic for self-contained
  single-file publish); unmaintained upstream is a supply-chain risk
  for a backup product.
- **Verdict:** Not recommended as a direct dependency.

### 3b. Direct P/Invoke to VSS COM interfaces

- **What:** Define COM interfaces (`IVssBackupComponents`, etc.) in C#
  and call `CreateVssBackupComponents` via P/Invoke.
- **Pros:** No third-party dependency; full control.
- **Cons:** VSS COM is complex (~15 interfaces, async job pattern,
  careful lifetime management). Reimplementing it is error-prone.
  Requires deep VSS expertise to get right.
- **Verdict:** Possible but high-risk. Only if no better option exists.

### 3c. Isolated native VSS helper (recommended)

- **What:** A small, single-purpose native executable
  (`FileKeepVss.exe`) written in C++ that:
  1. Creates a VSS shadow copy of the target volume
  2. Exposes the snapshot as a drive letter or UNC path
     (`\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN`)
  3. Prints the snapshot device path to stdout
  4. Waits for a signal (or timeout) on stdin
  5. Deletes the shadow copy on exit
- **Why isolated:**
  - VSS requires elevation and COM. Keeping it in a separate process
    means the main backup engine never touches COM, never needs to
    be elevated for VSS, and a VSS crash cannot take down the backup.
  - The helper is small enough to audit (~300 lines of C++).
  - It can be tested independently: run it, verify the snapshot path
    appears, read files from it, signal exit, verify cleanup.
  - If VSS fails, the helper exits non-zero with a clear error. The
    backup job fails closed — no silent live copy.
- **Interface:**
  ```
  FileKeepVss.exe --volume C: --timeout 3600
  → stdout: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3
  → stdin: "done\n" or EOF → deletes snapshot, exits 0
  ```
- **Verdict:** Recommended. Smallest trusted computing base, cleanest
  failure mode, testable in isolation.

### 3d. vshadow.exe (VSS SDK sample)

- **What:** Microsoft's VSS SDK includes a `vshadow.exe` sample that can
  create and expose shadow copies from the command line.
- **Pros:** Written by the VSS team; known-correct.
- **Cons:** Not redistributable as a standalone binary (SDK license);
  would need to be built from source. Still a separate-process model,
  so similar to 3c but with licensing friction.
- **Verdict:** Useful as a reference implementation for 3c, not as a
  shipped dependency.

---

## 4. Recommended architecture

```
┌─────────────────────────────────────────────────┐
│ FileKeep backup engine (managed, unelevated)     │
│                                                  │
│  1. Job requests "crash-consistent image"        │
│  2. Spawns FileKeepVss.exe --volume C:           │
│  3. Reads snapshot path from stdout              │
│  4. Backs up files from snapshot path            │
│     (existing chunking/encryption/pipeline)      │
│  5. Signals "done" on stdin                      │
│  6. Helper deletes snapshot, exits               │
│                                                  │
│  If helper exits non-zero at any point:          │
│  → job fails with "VSS snapshot failed: <reason>"│
│  → NO fallback to live copy                     │
└─────────────────────────────────────────────────┘
```

### Privilege model

- `FileKeepVss.exe` requires Administrator (VSS requirement).
- The backup service already runs as LocalSystem (per MSI config),
  so elevation is not an additional prompt.
- For interactive CLI use, the user must run elevated when requesting
  image backup. The CLI must detect non-elevated execution and fail
  with a clear message, not attempt VSS and produce a confusing COM
  error.

### Snapshot lifetime

- The helper holds the snapshot until signaled or until `--timeout`
  elapses (default: 1 hour, configurable).
- On timeout, the helper deletes the snapshot and exits non-zero.
  The backup job must handle this as a failure (partial backup is
  discarded or marked incomplete — never presented as a valid image).
- Abnormal termination (crash, kill) should still clean up via the
  VSS auto-delete on last-handle-close behavior. This needs testing.

---

## 5. Implementation phases

### Phase A: Native helper (C++)

- [ ] Minimal VSS shadow copy create/expose/delete in C++
- [ ] CLI interface: `--volume`, `--timeout`, stdout path protocol
- [ ] Tested on real Windows: snapshot appears, files readable,
      cleanup on normal and abnormal exit
- [ ] Signed alongside other executables in build script

### Phase B: Engine integration

- [ ] `VssSnapshot` class in `UsenetBackup.Core` that spawns the helper,
      parses the snapshot path, and implements `IDisposable` for cleanup
- [ ] Backup job option: `consistencyLevel` = `file` | `crash-consistent`
      | `application-consistent`
- [ ] Fail-closed: VSS failure → job error, never silent live copy
- [ ] Elevation check in CLI with clear error message

### Phase C: Application-consistent (VSS writers)

- [ ] Pass `VSS_BT_FULL` and wait for writer coordination in the helper
- [ ] Expose writer status (which writers participated, which failed)
- [ ] Job records writer results in the backup manifest
- [ ] UI shows "application-consistent" only when writers confirmed

### Phase D: Documentation and validation

- [ ] User docs explain the three levels and when to use each
- [ ] Bare-metal recovery tested from VSS snapshot via WinPE
- [ ] Failure injection: VSS disabled, insufficient privilege, timeout —
      all fail closed with clear errors

---

## 6. Open questions

1. **C++/CLI vs pure C++ for the helper?** Pure C++ with direct COM
   calls avoids the C++/CLI single-file publish problem entirely.
   The helper is a separate exe, so it does not need to be managed.

2. **Should the helper support persistent shadow copies?** For very
   large volumes, backup may exceed the timeout. Options: longer
   timeout (simpler) vs. persistent snapshots with explicit cleanup
   (more complex, risk of orphaned snapshots).

3. **ReFS support?** VSS works on ReFS, but block-cloning behavior
   differs. Needs testing if FileKeep targets ReFS volumes.

---

## References

- AlphaVSS (inactive upstream): https://github.com/alphaleonis/AlphaVSS
- AlphaVSS 2025 fork: https://github.com/virbula2025/alphavss
- Microsoft VSS documentation: Volume Shadow Copy Service overview
- vshadow.exe: included in Windows SDK VSS samples
