# FileKeep Engineering Roadmap

This document captures the major next-stage engineering problems identified
from external review (2026-10-08). These are not bugs — they are architectural
gaps that must be addressed before the corresponding features are
production-ready.

---

## 1. System-Image Consistency Strategy

**Status:** Open engineering problem
**Blocks:** Bare-metal image restore being called production-ready

### Problem

FileKeep's stated goal includes system-image backup with bare-metal recovery
via WinRE. A file-by-file copy of a live Windows system disk is not
crash-consistent, let alone application-consistent. Without a genuine snapshot
mechanism, the "image" may contain torn writes, inconsistent registry hives,
or corrupt database files.

### Required distinctions

The product must clearly distinguish three different guarantees:

1. **File backup** — individual files copied; no consistency guarantee across
   files. Suitable for user data.
2. **Crash-consistent disk image** — a point-in-time snapshot of the disk.
   Equivalent to pulling the power plug: the filesystem is consistent, but
   applications may have lost in-flight transactions.
3. **Application-consistent disk image** — applications quiesced (via VSS
   writers) before the snapshot. Databases, mail stores, etc. are in a
   clean state.

Today FileKeep does not achieve any of these for a live system disk. It must
not claim to until the mechanism exists.

### Possible approaches

- **Proper Windows VSS implementation** using a well-tested library or
  native helper. This is the correct Windows-native path: VSS coordinates
  with filesystem and application writers to produce a consistent shadow
  copy, which FileKeep then reads.
- **A carefully isolated native VSS component.** VSS requires COM
  interop and elevated privileges. Isolating it in a small native helper
  (rather than embedding COM logic in the managed backup engine) limits
  the blast radius of VSS failures and keeps the core engine testable.
- **Explicit application quiescing** where appropriate. For specific
  workloads (e.g., SQLite catalogs, mail stores), FileKeep could quiesce
  before snapshotting even without full VSS writer coordination.

### Acceptance criteria

- [ ] VSS (or equivalent genuine snapshot mechanism) implemented and tested
  on real Windows
- [ ] Documentation states which consistency level each backup type provides
- [ ] Bare-metal recovery tested from a VSS snapshot, not a live copy
- [ ] Failure mode defined: what happens when VSS is unavailable (fail
  closed with a clear error, not a silent live copy)

---

## 2. Incremental Correctness Model

**Status:** Open engineering problem
**Blocks:** Trust in incremental backup correctness

### Problem

FileKeep's incremental fast path uses size + modification time to skip
unchanged files. This is a useful performance optimization, but it is not
a correctness mechanism. A file whose contents change while its size and
mtime are preserved (or restored) will be incorrectly classified as
unchanged.

Example:
```
file.txt, 10 MB, mtime = 12:00
  → contents change
  → mtime restored to 12:00
  → size + mtime check concludes UNCHANGED (wrong)
```

### Intended architecture

```
metadata fast path (size + mtime)
       ↓
probably unchanged
       ↓
optional content verification
       ↓
SHA-256 comparison
```

The fast path stays for performance. What is missing:

- **Configurable verification mode.** A per-job or global setting:
  `fast` (metadata only), `verify` (hash changed-size-or-mtime files),
  `paranoid` (hash everything). The user chooses the performance /
  correctness tradeoff explicitly.
- **Periodic repository verification.** Even in `fast` mode, a scheduled
  deep verification pass (hash every file against the manifest) catches
  the cases the fast path misses. This already exists as post-backup
  auto-verify; it needs to be extended to a scheduled standalone operation.

### Acceptance criteria

- [ ] Verification mode setting (`fast` / `verify` / `paranoid`) implemented
  and exposed in job configuration
- [ ] Scheduled deep verification independent of backup runs
- [ ] Documentation explains the tradeoff and the failure mode of `fast`
- [ ] Test: file with changed contents but preserved size+mtime is detected
  in `verify` and `paranoid` modes

---

## 3. Usenet Retention Management

**Status:** Open engineering problem — needs to become a first-class subsystem
**Blocks:** The original product vision

### Problem

The original concept was not merely "put a backup on Usenet." It was:

> "Use Usenet's long retention as a backup cloud and keep the backup alive
> as articles approach expiration."

FileKeep has excellent building blocks:

- Upload journal, article IDs, manifest indexes
- Upload dates, expiration information
- NZB generation, remote discovery

But the **retention/republication engine** — the subsystem that keeps
backups alive — does not yet exist as a designed component. Without it,
backups silently expire.

### Intended lifecycle

```
Backup
   │
   ▼
Publish
   │
   ▼
Track article age
   │
   ▼
Determine remaining retention
   │
   ├── Healthy ────────────────┐
   │                           │
   └── Approaching expiry      │
             │                 │
             ▼                 │
        Verify availability    │
             │                 │
             ▼                 │
        Repost missing/aging   │
             │                 │
             ▼                 │
        Record new article IDs │
             │                 │
             └─────────────────┘
```

### Design questions to resolve

- **Retention source of truth.** Provider retention is advertised, not
  guaranteed. The engine needs to determine actual availability (via
  STAT checks or header queries), not just compute
  `upload_date + advertised_retention`.
- **Republication policy.** When an article is approaching expiry: repost
  proactively at N days before expiry? Or verify-then-repost only when
  actually missing? The former is simpler; the latter saves upload
  bandwidth.
- **Article ID tracking.** Reposted articles get new message-IDs. The
  manifest index must be updated atomically so recovery always resolves
  to live articles.
- **Scheduling.** The retention monitor needs its own schedule,
  independent of backup jobs. Daily checks are likely sufficient given
  multi-year retention windows.

### Acceptance criteria

- [ ] Retention monitor subsystem designed and implemented
- [ ] Article availability verified against the provider, not just computed
  from dates
- [ ] Reposting updates manifest indexes atomically
- [ ] Dashboard shows retention health per backup (days remaining, articles
  reposted)
- [ ] Test: simulated expiry triggers repost and index update

---

## Tracking

These problems are tracked as GitHub issues on the FileKeep repository.
Each issue links back to this document for the full context.
