# Changelog

## fpkg 0.6.9

Tracks LibProsperoPkg 0.6.9. Re-run `./fpkg patch` after updating.

### If you built anything with 0.6.8, check it

0.6.8 declared only chunk 0 as PlayGo-initial while still spreading the game's files across every chunk. PlayGo reads that as "almost none of this title is downloaded yet", which can make an otherwise fine package misbehave once installed. 0.6.9 fixes it upstream — all files go in chunk zero and every chunk is initial.

The library's own verifier does **not** flag it: an initial count of 1 is structurally legal and passes. So `fpkg verify --quick` now adds its own warning:

```
WARNING: PlayGo: 37 of 43 file mappings sit in chunks at or above index 1 (highest 7),
but the scenario declares only 1 of its 8 chunk(s) as initial. A package installed whole
still reports those files as not downloaded. Rebuild it.
```

It is a warning, not an issue, so the exit code is unchanged. Run it over anything built with 0.6.8. Packages from earlier releases are not affected.

### PlayGo is rebuilt around languages
Chunk zero now holds the game files, each selected language gets a small chunk of its own with a real extent, and every chunk past the language count is empty by design. Scenario names and localisations in a source `playgo-scenario.json` are carried through instead of being replaced with generated ones, including its `chunkSupportedLanguages` and `chunkDefaultLanguage`.

Upstream also dropped the scenario limit from 64 to **5**, matching the SDK's own `SCE_PLAYGO_MAX_SCENARIO`.

### New: `fpkg repair-playgo`
`fpkg repair-playgo <pkg>` rewrites a package's PlayGo metadata in place to the shape LibProsperoPkg 0.6.9 produces, in seconds rather than the hours a rebuild costs. The payload is never decoded, recompressed or modified — it is copied through verbatim. Packages built with 0.6.8 declared only chunk zero initial while spreading files across every chunk, which a console reads as "almost nothing is downloaded". The command regenerates `playgo-chunk.dat`, `playgo-ficm.dat` and `playgo-scenario.json`, relays out the CNT body into its existing slack, reseals the digest chain and rebuilds the SI segment. Output is byte-identical to a 0.6.9 rebuild of the same source.

`--dry-run` is the default; `--out <path>` or `--in-place` is required to write. `--out` refuses to overwrite an existing file, and assembles the repaired package in a temporary file beside the target before renaming over it — free space roughly equal to the package's own size, and ~1.32 GB of I/O on the test package's 661 MB, since the staging file is read and written across two passes.

`--in-place` is now genuinely in place: it writes the repaired CNT back into its own footprint and appends the rebuilt SI, touching only those two regions — ~64 MB on the test package — instead of copying the whole package. It writes ~128 MB in total (the journal, then the two regions) and needs ~64 MB of spare disk for that journal, against `--out`'s ~1.32 GB and a full second copy. It is crash-recoverable: the two regions it is about to overwrite are journalled to a sidecar file and flushed to disk before the package is touched at all, and a run that finds a leftover journal restores the package from it and stops, rather than repairing (or re-repairing) a package it can't yet be sure is whole. Both write paths still produce byte-identical output.

An in-place run also leaves a marker beside the package, `<package>.repair-playgo.inprogress`, recording where its journal went, and prints that path on every run. Recovery consults the marker before anything else, so an interrupted repair is found again even if the next run forgets `--work-dir` or is given a different one. A marker whose journal has vanished — a `--work-dir` under `/tmp` cleared by a reboot is the usual way — is now a loud, non-zero refusal naming the missing journal; previously the run found no journal, inspected the already-repaired CNT, and reported "Nothing to repair" with exit 0 on a package whose SI was stale. Relatedly, an `--out` run that is consumed by journal recovery now exits non-zero: it produces no `--out` file, and exiting 0 let `… --out FIXED.pkg && install FIXED.pkg` proceed on a file that was never written.

`--work-dir <dir>` relocates the write path's working file: the staging copy for `--out`, or the recovery journal for `--in-place`. It is created if missing. Pointing it at a different device from `--out`'s target weakens the staged rename into a non-atomic copy — an interruption mid-copy can leave a partial file at the output path — so the command warns when that looks likely; the check is best-effort on macOS, because `Path.GetFullPath` does not expand APFS firmlinks, so two paths on one physical volume can appear to sit on different ones.

`--verbose` prints the recovered PlayGo values, per-entry digest work and the SI member list. Every run — including a dry run — now announces the package and reports its stages as it goes, rather than sitting silent through the slow reads before printing anything.

Reading a package no longer costs a pass over its payload. The load step used to hand the whole file to `ProsperoPackageArchive.Split` and discard the outer PFS into `Stream.Null`, which throws the bytes away but still reads them — so every run, `--dry-run` included, pulled the entire package off disk to use ~64 MB of it. It now asks for the region geometry and reads only the CNT and the SI. On the 661 MB test package the whole dry run takes 0.4s rather than seconds; on a 90 GB package it is the difference between ~90 GB of reading and ~64 MB. Stage percentages also count the bytes actually read rather than the file position, so a stage no longer jumps to 99% on its first seek and freezes there, and a stage that reports progress now ends at 100% instead of stopping at 98 or 99 when the throttle swallows its last step. A stage over in well under a second prints no percentage at all.

It refuses rather than degrading, on six numbered guards plus two structural preconditions. The guards: a passcode that does not match the package; `pfsimage.xml` present in the SI; a relayout that would change `body_size`; a `playgo-scenario.json` declaring a member regeneration would not reproduce; a package that is not the PS5 publisher profile; and a package that is not an Application volume. The preconditions: a package with no SI segment, and a CNT region carrying padding past the end of its body, which the reseal cannot reproduce. All eight run before a single byte is written.

Requires LibProsperoPkg 0.6.7 or newer; refuses with a version message on anything older.

### Removed
The zero-length-chunk guard. It existed because chunk extents used to be an even division of the image, so a chunk count the image could not fill produced empty extents. Extents are no longer computed that way and empty chunks are now intentional, so the guard would reject ordinary builds.

## fpkg 0.6.8

Tracks LibProsperoPkg 0.6.8. Re-run `./fpkg patch` after updating.

### Builds are now verified properly
Every build already ended with a structural look at the finished file. It now also runs the library's quick verifier: segment ranges, CNT and entry digests, **PlayGo layout**, the outer superblock ICV, the NAPS layout, inner inode metadata and the SI directory. About a second on a 650 MB package, and a failure fails the build. `--no-verify` skips both.

This is worth having because the PlayGo map is exactly the thing 0.6.8 fixed, and the official GUI still runs only the structural half after a build — its own quick and full verifiers are manual buttons on the Extract tab. A package either tool calls "verified" today has not had its PlayGo layout checked; now ours has.

**Packages built with earlier versions may fail this check.** The final PlayGo extent was sized against the wrong base, overrunning the mount image by however far the CNT body ran past 64 KiB — so a package with a full `sce_sys` is affected and a minimal one is not. `fpkg verify <pkg> --quick` reports it as `PlayGo extents cover 0x… bytes; the package image before CNT requires 0x…`. Rebuilding on 0.6.8 fixes it.

### Changed defaults
- `--app-drm` now defaults to **standard**, following the GUI.
- `--ac-drm` now defaults to **entitlement**. This also corrects a mistake: the GUI has defaulted add-on content to Entitlement all along, and `fpkg` shipped `free`.
- `--sdk-version` still defaults to `1`, but it now **also sets `requiredSystemSoftwareVersion`** to the same version. That is the point of the release — a dump demanding newer firmware than you have is brought down to the SDK you pick. A default build therefore declares firmware 1.00; `--sdk-version keep` leaves the source's own value untouched.

### Also in the library
PlayGo maps now mark only chunk 0 as initially required, so progressive install is actually progressive, and a GP5 that declares `chunk_info` without any scenario gets a conventional play-mode scenario filled in.

## fpkg 0.6.7-fix1

Fixes one regression in 0.6.7. Re-run `./fpkg patch` after updating — this release changes what the patch does.

**Container sources failed when an SDK was stamped.** 0.6.7 gave `--sdk-version` a default of `1`, which made the library rewrite the `.sceversion` trailer in every executable on every build. That path opens the original executable by filename, and a `.ffpfsc` source has no filename to open, so any container holding a `.prx`, `.self`, `.elf` or `eboot.bin` died with `Could not find a part of the path '...ffpfsc:<handle>:/...'`. Two further IL patch sites cover it; `fpkg patch` now reports nine instead of seven. Folder sources were never affected, and neither were containers built with `--sdk-version keep`.

A container carrying executables and the equivalent folder now produce byte-identical inner files, with the SDK correctly restamped.

## fpkg 0.6.7

Tracks LibProsperoPkg 0.6.7.

### New commands
- `fpkg template <pkg> <dir>` — export a rebuildable add-on template: the sce_sys inputs plus a `.gp5` carrying the content id, passcode, entitlement key and PlayGo counts recovered from the package. Data-bearing AC packages only; the payload is not included.
- `fpkg verify <pkg> --quick | --full` — the library's own verifiers. Quick checks structure, digests, the outer superblock ICV and the SI directory; full also decodes every block, chunk and inner file. Exits 1 on any issue.
- `fpkg extract <pkg> <dir> --rebuild-source` — just the sce_sys inputs a rebuild needs. Never opens the inner filesystem, so it finishes in seconds.

### New build options
- `--app-drm free|standard` (default **free**, matching the official GUI) and `--ac-drm free|entitlement`. These replace the old param.json rewrite — 0.6.6 added a real build option, so `fpkg` no longer edits your files to set the DRM type. Note `--ac-drm free` also omits `license.dat`/`license.info` entirely.
- `--playgo-languages` — comma-separated language codes, or `all`.
- `--no-media-repair` — opt out of the PNG repair below.

### Corrupt artwork is now repaired
`icon0`, `pic0`, `pic1` and `pic2` `.png` are validated properly (signature, chunk chain, CRC-32, IEND, no trailing bytes) and any that fail are rebuilt from the matching `.dds`. Dumps really do ship damaged artwork, and upstream packs a present file unchecked. Its own "restore a missing pic1/pic2" path is Windows-only, so this is done in managed code that works everywhere. A damaged image with no usable `.dds` stops the build instead of shipping.

### Changed
- `--playgo` now accepts **1–255** (was 1–64). The library default moved 64 → **100**.
- `--sdk-version` now defaults to **1**, matching the 0.6.7 GUI. Use `--sdk-version keep` to leave the source's own values alone.
- Since 0.6.7 the SDK no longer moves `requiredSystemSoftwareVersion` in either direction — upstream decoupled them.
- Removed `--retain-param-json`; there is nothing left to retain.

### Guards upstream dropped
- **Zero-length PlayGo chunks.** 0.6.4 refused a chunk count the image can't fill; 0.6.6 removed that check and 0.6.7 didn't restore it. With the default now 100, a small source silently gets empty chunks. `fpkg` refuses and tells you what fits.
- **Exported templates.** The default sce_sys sweep would delete a template's licence and PlayGo files, which are deliberate inputs. `fpkg` detects the shape and points you at `--retain-sce-sys`.

---

## fpkg 0.6.4 — first release

A command-line front end for **LibProsperoPkg by Drakmor**, so PS5 packages can be built on **macOS and Linux**. The official GUI is WinForms and cannot run there; this drives the same library unchanged. One zip covers macOS and Linux, arm64 and x64.

### Building
`fpkg build` with the options the GUI exposes: `--mode`, `--image-mode`, `--format`, `--content-id`, `--version`, `--title`, `--title-id`, `--passcode`, `--playgo`, `--sdk-version`, `--entitlement-key`, `--gp5`, `--pfs-format`, `--compress`, `--temp-dir`, plus `--no-deterministic`, `--no-param-json`, `--no-verify`, `--no-coalescing`, `--no-relocation-align` and `--shuffle-analysis`. Content id, version and title are read from `sce_sys/param.json` when not given, and the finished package is verified structurally unless you skip it.

### Build straight from a container
`--source` accepts a `.ffpfsc` or `.exfat` image as well as a folder. The container is read in place — no unpacking, no temporary copy of the tree. It produces byte-identical output to building from the equivalent extracted folder.

### Native Oodle encoder
`--kraken-backend Oodle` binds a RAD Oodle library you supply yourself (2.9.16) for roughly 3× faster builds. Nothing is compiled for it and nothing is bundled. `Auto` picks Oodle when it is available and the managed BuiltIn encoder when it is not, and says which it chose.

### Compression level follows the backend
`--kraken-level` defaults to **7 for Oodle, 6 for BuiltIn**. From 0.6.2 the built-in encoder builds a suffix trie and runs a full dynamic-programming parse at level 7 and above — 22.0 s against 5.6 s at level 6, for 1.06% smaller output on a 679 MB dump. Oodle bypasses that machinery, so 7 costs it almost nothing; 8 and 9 are slower *and* larger there.

### Fix-ups a retail dump needs, reverted afterwards
A dump used as-is builds a package the console refuses to run, so two fix-ups are on by default and both are undone when the build ends, including after an error or Ctrl-C: `applicationDrmType` is forced to `"standard"`, and stale `sce_sys` files (`license.*`, `playgo-*`, `origin-param.json`, `target-param.json`) are set aside. `--retain-param-json` and `--retain-sce-sys` opt out. Nothing is left modified in your source, and a run killed mid-build is repaired by the next one.

### Other commands
`fpkg version`, `fpkg keys`, `fpkg info`, `fpkg verify`, `fpkg extract`, `fpkg api`, and `fpkg patch` — which applies the container-source and Oodle patches in place, from the zip alone, with no repo and no .NET SDK.
