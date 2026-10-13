# FileKeep v1 Wants

Future work pruned from the v2/v3 branches, parked here for when v1 is ready.
Nothing here is committed to a timeline — it's a wishlist, not a roadmap.

## From v2

### Selective/merge restore — DONE (cherry-picked 2026-10-13)
- `RestoreOptions`: Include/Exclude globs, `MergePolicy` (Overwrite/SkipDiffering/KeepNewer)
- CLI `--include`/`--exclude`/`--merge-policy`, recovery wizard policy picker,
  dashboard restore-card dropdown. Commit `cf60c65`.

### VSS raw-disk FileStream workaround — EVALUATE
- v2 has an uncommitted fix for a .NET 10 bug where path-based `FileStream`
  throws `UnauthorizedAccessException` on `\\.\PhysicalDriveN` even when elevated.
  Workaround uses `CreateFileW` P/Invoke (`RawDiskWriter.OpenDeviceStream`).
  If v1 hits this in `RestoreDiskImage`, port the fix.

## From v3

### Content-defined chunking (CDC) — PARKED
- Phase 1 built and sandbox-tested on the `v3` branch (uncommitted):
  `CdcChunker` (Gear rolling hash, fixed SplitMix64 table), repo-level
  `chunking_scheme` in repo.json (`init --chunking cdc`, default `fixed`),
  additive manifest `chunking` field, 11 `CdcChunkingTests` passing.
- Open decisions: opt-in vs default for new repos; phase 2 = USN-journal
  change tracking to skip full tree scans.
- Why it matters: insertion-stability for big files (a byte inserted at the
  start doesn't re-chunk the whole file), better dedup on large binaries.
- Port when: v1 needs better incremental performance on large files.

### USN-journal change tracking — PARKED (depends on CDC or standalone)
- Phase 2 of v3 goal #4: use the NTFS USN journal to skip full tree scans.
- Port when: backup scan time becomes the bottleneck.
