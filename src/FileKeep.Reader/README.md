# FileKeep portable reader (Stealth Folders slice 3, part 3)

Runs from a stick, no install, no registry, no host writes except
the host apps' own copies. It opens an offline-folder export on a
foreign machine and serves its files over its own loopback WebDAV
share (memory-only), with a decrypt-to-folder fallback.

## Two secrets, prompted every time

- The **export passphrase** opens the package.
- The **vault secret** (typed passphrase or generated secret — the
  same secret that unlocks the card on the main machine) derives
  the file-content key (`OfflineChunkStore.DeriveContentKey`).

Neither secret is stored, logged, or written to the stick, and both
are zeroed on lock/exit (the content key bytes; passphrase strings
are transient locals — strings cannot be zeroed). The stick never
carries a secret, and the package never carries credentials.

Hostile host is out of scope: a typed passphrase can be keylogged
and opened files pass through host RAM. The unlock screen says so
and requires an explicit `YES` before proceeding.

## On-stick layout (`FileKeep.Core.OfflineStickLayout`)

```
FileKeepReader\manifest.filekeep-export.json   (encrypted package)
FileKeepReader\chunks\<hh>\<rest>              (full exports only)
```

Chunk blobs are exactly as a plain `LocalBlobStore` stores them
(chunk ID as filename, encrypted bytes as stored) — no new chunk
format. Discovery order: the reader folder's manifest, then any
`*.filekeep-export.json` in the reader folder, then any at the
stick root. A manifest-only export is just the package file; a
full export is produced by the dashboard's "Export full to
folder…" (manifest + chunks into `destination\FileKeepReader\`).

Disguised locations: the reader folder is explicitly a FileKeep
folder, not camouflage, so it stays findable. Tucking a manifest
into a disguised cover folder is NOT implemented — there is no
disguised manifest filename the main app writes, and inventing one
for the reader to guess at would be fake.

## Chunk sources, per chunk, in order

1. Stick first.
2. Original location: USB by volume serial, or SMB share with
   credentials re-entered on the spot (never stored, never in the
   package). Disguise layouts apply on the original location.
3. A file with any chunk missing from both is reported incomplete
   per file — never streamed garbled or partial.

## The share

Own loopback DAV, ported from the main app's custom handler (no
third-party DAV library, no WinForms/WebView2, no Native AOT for
v1 — the reader is a console/Kestrel app, trimmed single-file):
OPTIONS, PROPFIND (Depth 0/1/infinity), GET, PUT, DELETE, LOCK,
UNLOCK. MKCOL is refused (folders are manifest entries).

- GET streams chunk-by-chunk from the composite store, aborting
  on lock, after a pre-flight existence check per chunk.
- PUT-back writes new chunks to the **stick** store and
  re-encrypts the stick package under the same export passphrase.
  The original location and the main catalog are untouched; the
  stick becomes the edited copy.
- DELETE removes the manifest entry and re-encrypts the package;
  stick chunks stay, orphaned (like forget).

Commands: `list` | `open <n>` (prints the loopback URL) |
`decrypt-to <n> <folder>` (plaintext copy, one-way, untracked —
a partial file is deleted on failure) | `lock` (keys zeroed,
re-unlock or exit) | `exit`.

## Recovery USB

The WinRE builder offers the reader as a user-chosen step
(manifest-only default, full opt-in): it copies the reader exe
(and the export, if chosen) to the stick/image root under
`FileKeepReader\` for manual launch on a running Windows machine.
No autorun. The files are inert in booted WinPE — running the
reader there is NOT promised. The WinRE boot test is on hold.

## Publish

```
dotnet publish src/FileKeep.Reader/FileKeep.Reader.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\reader /p:PublishSingleFile=true
```

The laptop build script (`install/run-vm-build.ps1`) publishes
and signs the reader to `C:\ub\publish\reader\FileKeepReader.exe`.
The reader is NOT packaged into the MSI — it is portable, not
installed.

## Owed (not claimed)

- YubiKey touch unlock in the reader: v1 is passphrase /
  typed-secret only. `IYubiKeyProvider` lives behind Service
  dependencies, so touch support needs a Core-safe unwrap path.
- GUI click-proof of the per-file export UI, the full-export UI,
  and the WinRE reader step; reader run on a second machine/VM.
- Laptop Windows suite for this change.
