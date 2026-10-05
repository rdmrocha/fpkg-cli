# Changelog

## fpkg 0.6.11

Tracks LibProsperoPkg 0.6.11. **Re-run `./fpkg patch` after updating**: two IL sites moved. A
patched copy is keyed to the hash of the library it came from, so until you re-patch, the new
release runs unpatched and `fpkg` says so.

### PlayGo: the library keeps a dump's own map, and fpkg stops repairing over it

0.6.11 can keep a source's PlayGo map. Given `playgo-chunk.dat`, `playgo-hash-table.dat` and
`playgo-ficm.dat` that put files in their own chunks — which a dump of a Sony-built package has —
it retains every file's chunk, the labels and the language masks, and rebuilds only the physical
extents from where the files landed in the new image. That is what fpkg's PlayGo repair
approximates, done properly, so when the library will do it fpkg now steps aside:

- those four files are no longer set aside, no fillers are staged, and the post-build
  rederivation does not run;
- the 31 language fillers the dump already carries are kept distinct, block-aligned and last
  (sites 10, 11, 12, 15), which the library does not do — without it all 31 share one stored copy
  and one extent;
- what the library leaves unfinished is completed on top of its tables: the source's own
  `playgo-scenario.json` is restored (the library re-serialises it), the same document is added to
  the SI, and `naps_meta_3xx` and `playgo-chunk.crc` are rebuilt from the library's extents.

fpkg asks the library itself, through its own `ResolvePlayGoConfiguration`, rather than
re-implementing the rules, and says which path a build took. A language subset
(`--playgo-languages`) overrides the source map in the library, so the repair applies there, as it
does for sources without usable tables. `--force-playgo-fix` applies the repair regardless.

The oracle is a round trip: a package built by the repair, extracted and rebuilt through the
library's source-map mode, comes out **byte-identical**.

A source whose `playgo-languages/` is exactly what fpkg would stage is now reused in place.
Previously such a dump could only be built with `--playgo-fillers-force`, which deleted it.

### What the library now does itself

Each of these was measured byte-identical before fpkg's own copy was removed or narrowed.

- **SELF normalisation.** 0.6.11 normalises `eboot.bin`, every `*.prx`, `fakelib/` and
  `sce_module/` in an Application build, exactly as `SelfNormalize` did, and offers no way to turn
  it off. **fpkg's own normaliser is now off by default**: those are the executables a package is
  expected to carry, and a SELF elsewhere in a dump is normally an intermediate file.
  `--extra-self-normalize` normalises the SELFs outside the library's scope as well;
  `--force-self-normalize` additionally re-does the library's scope ahead of it. **`--no-self-normalize`
  is removed** and refused with an explanation, since it can no longer mean what it says.
- **FICM slot order.** `ProsperoPlayGo.BuildFileMap` sorts the hash table and FICM together and
  replaces fpkg's own slot ordering.
- **Source exclusions.** `*.gp4`/`*.gp5`, the root `ampr_emu.index` and the two fake libraries are
  dropped by the library, so the quarantine no longer moves them. 0.6.11 also drops the root
  `entitlements.txt`, `entitlement_key.dat` and `decrypted/`.

### Patcher

Sites 8 and 9 follow `BuildSdkOverriddenSelf` to its replacement, `BuildNormalizedSelf`, whose
copy delegate now captures a rewrite plan. 0.6.11 runs it for every SELF, not only under
`--sdk-version`, so a container source reaches it on ordinary builds. All 24 sites apply; the
single-file build's byte-parity tests pass unchanged.

### Flags

Added: `--force-playgo-fix`, `--extra-self-normalize`, `--force-self-normalize`.
Removed: `--no-self-normalize` (refused, with the reason).

### `fpkg merge`: rebuild a full package from a base and an LIH patch

Backports and other patch-style releases ship as an LIH compact patch (a `.pkg` starting with
`\x7fLIH`) that only carries the blocks that differ from a base package, so `info`, `verify` and
`extract` rejected it as "unknown magic". `fpkg merge <base.pkg> <patch.pkg> <out.pkg>` now rebuilds
the full package, and those commands now say what the file is and point at `merge`.

The merge is a block copy driven by the patch's own `common/update-relocinfo.dat`: one entry per
64 KiB target block naming a base or patch block, followed by the patch's CNT and SI verbatim. On a
34 GB title the output is byte-identical to Publishing Tools' `img_convert --oformat remastered`.
The base is checked against the PFS-area digest the patch records for it, and every offset in the
patch is bounds-checked before anything is written. No library patch is needed.

Known issue, probably resolved: LibProsperoPkg 0.6.9 could not read the inner filesystem of a
merged package — `verify --quick` reported a Kraken decode failure and `extract` failed, though the
package itself is sound (Sony's `img_verify` integrity check passes). It located NAPS spans by their
encryption tweak, which relocation leaves pointing one block off. 0.6.11 locates them by physical
offset instead, which is exactly that fix, but it has not been re-checked on a merged package.

### A build needs about half the free space it used to

The inner `pfs_image.dat` is now written directly into the package at `0x10000` and hashed and
encrypted in place, instead of being spooled to `--temp-dir` at full size and then copied in. Peak
free space needed falls from roughly **2x** the finished package to **1x**, and `--temp-dir` holds
only the CNT and the NAPS sidecar. Measured on the test fixture: peak temp usage 65,641 bytes
against a 19,720,394-byte package — 0.3%, where it used to be about 100%.

This is a pure space optimisation and is asserted to be one: the suite builds the same source both
ways and requires byte-identical packages, on a plain layout, on a relocating layout, and on the
fallback path. `--spool` restores the old behaviour and is that oracle.

When the layout optimiser selects a moving plan, the permutation is replayed inside the package
rather than through a second file. If the displaced bytes would not fit in memory (512 MB by
default) the build falls back to a spool, says so, and produces the same package.

When the layout relocation runs, it costs two extra sequential reads of the inner image. That only
happens on layouts the optimiser chooses to move, which the default build never produces.

Needs `./fpkg patch`: this adds IL sites 17-24, recorded as a new `inplace` patch leg. **A library
patched by an earlier fpkg release needs re-patching** — it reports `ffpfsc` but has none of these
sites, and the build detects that, spools, and tells you so rather than promising a single-file
build it cannot do.


## fpkg 0.6.9-fix1

Fixes several defects in 0.6.9 against the same LibProsperoPkg 0.6.9. **Re-run `./fpkg patch` after
updating** — this release adds four IL patch legs and will not behave correctly without them.

### Titles that loaded to a black screen now boot

A large multi-executable title built with every earlier release loaded and then black-screened. It
now runs. Several independent defects contributed; each is described below, and any of them can
affect other titles.

Rebuild anything you care about. Packages from earlier releases are not repaired in place.

### PlayGo layout is derived from the package, not computed

The library sized language-chunk extents arithmetically while mapping every file to chunk 0, and
nothing reconciled the two. On a large title that left real payload inside extents belonging to
chunks the file map declares empty — in one case 84% of a boot executable.

Extents are now derived by coalescing the measured layout, and the 31 one-mebibyte zero fillers the
Windows SDK ships are staged so no real file can land in a language chunk. The fillers are placed
last in the inner image, the final language extent covers its whole block, and the region past the
last file becomes its own trailing extent — the same 33-extent shape the Windows toolkit produces.

**The file→chunk map was also indexed wrongly.** `playgo-ficm.dat` is indexed by a file's slot in
`playgo-hash-table.dat`, which is ordered by hash and not by file order. We wrote it in package
order, so most files carried another file's chunk id — on one 52-file package, 45 of them,
including the metadata an IL2CPP title cannot start without. Zero-length files now get a map entry too, one per
file as the Windows output has.

`playgo-scenario.json` is generated in the Windows toolkit's canonical form and written to both the
CNT entry and the SI, instead of two different documents.

### Executables are no longer modified

`--sdk-version` now defaults to **`keep`**. The Windows toolkit does not restamp executables, and
ours did: on a binary whose `.sceversion` table the library cannot locate, the restamp appended a
duplicate record rather than rewriting one, corrupting the boot executable. When a version *is*
requested, an executable that already carries its own record has it replaced in place.

Separately, SELF binaries are now normalised the way the SDK's own generator does: where a
binary's `.sceversion` trailer starts earlier than its header declares, it is moved onto the
declared boundary, and a legacy PS4 magic is rewritten. `--no-self-normalize` opts out.

### Source selection matches the SDK's generator

File exclusion is applied to the whole tree with the SDK's own scoping rather than to `sce_sys`
alone — `.gp4`, `.gp5` and `.esbak` at any depth, the generated `sce_sys` files at their own level,
the `about/` subtree, root-only directories and exact paths. `keystone`, `pfs-version.dat` and the
source `.dds` are deliberate exceptions and are documented as such.

Directories are now walked **case-insensitively**, which is what decides the order files are laid
out in; ordinal order put `Media/` ahead of `eboot.bin` and the SDK does not. The two generated
`sce_sys` files lead the image in the SDK's order.

The localised `sce_sys/icon0_NN.dds` the SDK generates from a dump's `icon0_NN.png` are now
generated too — 13 missing package entries on a title that ships them. `--no-localised-icons` opts out.

`sce_sys/about/right.sprx` is no longer synthesised. No Windows-built package contains it.
`--keep-right-sprx` restores it.

### Licence entries are stored in plaintext

`license.dat`, `license.info`, `nptitle.dat`, `uds/npbind.dat` and `trophy2/npbind.dat` are stored
unencrypted, as the Windows toolkit stores them. No build option reached this, so it is an IL patch.
`--encrypt-license-entries` restores the old behaviour.

### Faster

Repairing PlayGo used to rehash every 64 KiB block of the package to rebuild the chunk CRC table —
84 GiB of reads on a 90 GB title. It now reuses the entries below the CNT, which cannot have
changed, and re-verifies a window of them before trusting any. Byte-identical output; on
a 660 MB package, 61 MiB hashed instead of 631 MiB.

### New IL patch legs

`./fpkg patch` now applies sites 13–16 alongside the existing ones:

- **13** — the five licence CNT entries, stored plaintext
- **14** — suppress the synthesised `sce_sys/about/right.sprx`
- **15** — place the language fillers last in the inner image, and lead with the two generated
  `sce_sys` files in the SDK's order
- **16** — walk directories case-insensitively

### Flags

Added: `--no-self-normalize`, `--no-localised-icons`, `--keep-right-sprx`,
`--encrypt-license-entries`.

Changed: `--sdk-version` defaults to `keep` (was `1`), which also stops a build lowering the
source's `requiredSystemSoftwareVersion`.

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
