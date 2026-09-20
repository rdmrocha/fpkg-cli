# Running this on macOS

`LibProsperoPkg.Gui.exe` **cannot run on macOS.** It is a WinForms app, so its `runtimeconfig.json` requires the `Microsoft.WindowsDesktop.App` framework, which Microsoft only ever ships for Windows:

```
$ DOTNET_ROLL_FORWARD=LatestMajor dotnet LibProsperoPkg.Gui.dll
Framework: 'Microsoft.WindowsDesktop.App', version '9.0.0' (arm64)
No frameworks were found.
```

`LibProsperoPkg.dll` itself, however, targets plain `net9.0` with no Windows dependencies, and the bundled natives include `runtimes/osx-arm64`. So the library runs fine here — only the GUI shell doesn't.

`fpkg-cli/` is a CLI over that library, exposing what the GUI did (source folder + metadata -> build -> verify) plus package inspection. It runs on macOS and on Linux — see "Linux" below.

The binaries in this folder are **0.6.4**. (Treat 0.6.5 as non-existent: its zip is bit-for-bit 0.6.4 — almost certainly a bad repack — and it is ignored throughout this document.) That release changed no output format — 0.1 and 0.2 produce byte-identical packages — but it added file-backed streaming for payloads past the `byte[]` limit, a cancellable build, Kraken tuning and a configurable temp directory. See "Upstream 0.2" below.

## Install

Unzip into the release folder (the one holding `LibProsperoPkg.dll`) and run `./fpkg`. The zip unpacks to:

```
<release folder>/
  fpkg                 the launcher
  fpkg-tools/bin/      the CLI, dnlib, both patchers, FpkgVirtualSource,
                       PprPfsKrakenTool
  fpkg-tools/native/   empty; where YOU drop your own RAD Oodle library
```

The CLI's version lives in one file, `VERSION` (currently `0.6.4`). `fpkg.csproj` reads it for `<Version>`, and `publish.sh` reads the same file for the zip name, so `fpkg version` and the file a user downloaded cannot disagree. The number deliberately tracks the LibProsperoPkg release the CLI is built and tested against rather than counting its own releases: the two folders sit side by side on a user's disk, and "do these match" is the only question the number has to answer.

Two kinds of prebuilt distribution:

| zip | size | requires |
|---|---|---|
| `fpkg-cli-<version>.zip` | ~530 KB | `brew install dotnet` (runtime only) |
| `fpkg-cli-standalone-<version>-<rid>.zip` (one per RID) | ~29 MB | nothing |

`fpkg-cli-<version>.zip` is framework-dependent and carries no `RuntimeIdentifier`: it is portable IL that runs on macOS or Linux, arm64 or x64, wherever the .NET 10 runtime is installed. `fpkg-cli-standalone-<version>-<rid>.zip` is self-contained and inherently per-RID (it bundles a runtime built for one platform), so it is built and named per RID — `osx-arm64`, `osx-x64`, `linux-arm64`, `linux-x64` — by `publish.sh --standalone [rid]`.

The release assemblies are deliberately **not** bundled. `LibraryResolver` walks up from the binary, finds the folder holding `LibProsperoPkg.dll`, and binds there at runtime — so one build works with any release folder and never pins a library version. `<Private>false</Private>` on the references keeps them out of the output, and `publish.sh` fails if one ever leaks in.

If `fpkg-tools/bin/` is absent *and* `fpkg-cli/` sources are present (an in-repo checkout), the launcher builds from source instead, which needs the .NET SDK 10+.

### The zip is self-sufficient

**`./fpkg patch` works from the zip alone** — no checkout of this repository, no .NET SDK, no compiler, for either patch:

```sh
./fpkg patch                 # every available patch: oodle (if you supplied an
                             # Oodle library) and ffpfsc
./fpkg patch --ffpfsc        # only the virtual-source patch
./fpkg patch --oodle         # only the Oodle patch; FAILS if no Oodle library
```

This used to need the repo, the SDK and two `dotnet run` invocations through `patch.sh`. Both dnlib patchers now ship compiled in `fpkg-tools/bin/` and are invoked in-process, and so do the two assemblies a patched library calls into (`FpkgVirtualSource.dll`, `PprPfsKrakenTool.dll`).

The one thing you must supply is the RAD Oodle library itself, and only if you want the Oodle encoder — see "Native Oodle" below. Drop it in `fpkg-tools/native/`. Without it `./fpkg patch` applies the ffpfsc leg, says it skipped Oodle, and exits 0.

`patch.sh` and `patch-oodle.sh` remain as thin in-repo wrappers: they rebuild the CLI from source and delegate to `./fpkg patch`.

## Rebuild the distribution

`README.md` at the repository root is both the project README and the one bundled at the top level of the zip — that is the file an end user reads, so keep it current when flags or layout change. `NOTES.md` (this file) is the maintainer's record and is deliberately not shipped.


```sh
./publish.sh                              # framework-dependent, portable
./publish.sh --standalone                 # self-contained, for the host RID
./publish.sh --standalone linux-x64       # self-contained, for a named RID
```

The framework-dependent build carries no RID at all. `--standalone` defaults to whichever RID the build host reports through `uname`, or takes one of `osx-arm64`, `osx-x64`, `linux-arm64`, `linux-x64` explicitly. Nothing native of ours is in any of them — see "Native Oodle" — so the managed payload itself was always platform-agnostic; it was only the launcher, this script and the hard-coded `RuntimeIdentifier` that used to be macOS-only, and none of the three still are.

## Use

```sh
./fpkg help                     # builds on first run, then just runs
./fpkg keys                     # is the publishing key material present?
./fpkg info some.pkg            # container type + CNT header and entry table
./fpkg verify some.pkg          # structural FIH check (as the GUI did post-build)
./fpkg api ProsperoBuildOptions # reflect the library's public surface
./fpkg version                  # which LibProsperoPkg is loaded, from where, and its SHA-256

# Content ID, version and title come from sce_sys/param.json when not given:
./fpkg build --source ./MyGame --out ./dist

# Tuning (0.2): temp location, Kraken level and worker count.
./fpkg build --source ./MyGame --out ./dist \
  --temp-dir /Volumes/Scratch/fpkg --kraken-level 9 --kraken-threads 8
```

Ctrl+C cancels a running build via the library's `CancellationToken`; the CLI exits 130 and leaves no partial PKG behind.

### What a build changes in the source folder, and puts back

Three fix-ups are on by default, because a retail dump used as a source otherwise produces a package the console refuses to run. All are reverted in a `finally` block, and a run killed mid-build is repaired on the next one.

- **`applicationDrmType` comes from a build option, not an edit.** Until 0.6.5 the library had no override — no `ApplicationDrmTypeOverride`, no `ForceStandardApplicationDrm` — so this CLI rewrote `param.json` textually and restored it. 0.6.6 added `ApplicationDrmTypeOverride`, 0.6.7 added `AdditionalContentDrmTypeOverride`, and the whole `DrmTypePatch` class is gone along with `--retain-param-json`. `--app-drm` defaults to `free`, matching the 0.6.7 GUI's combo. One library predicate drives three outputs at once:

  ```csharp
  bool flag3 = (VolumeType == Application && drm == "free")
            || (VolumeType == AdditionalContentData && AdditionalContentDrmTypeOverride == Free);
  drm_type    = flag3 ? 0u : 16u;
  omitLicense = flag3;                  // and `!omitLicense &&` now guards the generated licence
  ```

  so `--ac-drm free` emits no `license.dat`/`license.info` at all, where 0.6.4 and 0.6.6 always generated one for additional content.
- **Stale `sce_sys` files are set aside.** `license.*`, `playgo-*`, `origin-param.json` and `target-param.json` are moved into the temp dir for the duration of the build and moved back after. A dump's `license.dat`/`.info` suppress the generated debug licence; its `playgo-*` describe the old image layout and win over the ones regenerated for the new PFS; `origin-param.json` and `target-param.json` are not CNT entries at all and ride into the inner PFS as loose files. `--retain-sce-sys` opts out.

  The one source tree where this is *wrong* is an AC template exported by `fpkg template`: that export deliberately keeps `license.info` (the entitlement key) and `playgo-chunk.dat`/`playgo-scenario.json` (the counts and language mask) as rebuild inputs. `LooksLikeExportedTemplate` detects the shape and refuses, rather than silently changing behaviour based on folder contents.
- **Corrupt `sce_sys` PNGs are regenerated from their `.dds`.** `icon0.png`, `pic0.png`, `pic1.png` and `pic2.png` are validated structurally — signature, IHDR first, the whole chunk chain with declared lengths and CRC-32, `IEND`, no trailing bytes — and any that fail are moved aside and rebuilt. `--no-media-repair` opts out; a failure with no usable `.dds` stops the build. See "sce_sys PNG repair" below for why this cannot be left to the library.

### Why `playgo-*` in the source defeats `--playgo`

PlayGo is Sony's progressive-install map. Three CNT entries describe it, and the builder generates all three from the image it just laid out:

| Entry | Id | Generated from |
|---|---|---|
| `playgo-chunk.dat` | `0x1001` | `BuildChunkDat(contentId, mchunk0Size, mchunk1Size, …, chunkCount)` — the chunk table, with the main extent split `chunkCount` ways |
| `playgo-hash-table.dat` | `0x2010` | `BuildHashTable(playgoPaths)` — one entry per inner file |
| `playgo-ficm.dat` | `0x2011` | `BuildFicm(BuildAutomaticFileChunkIds(fileCount/2, chunkCount))` — which file belongs to which chunk |

`--playgo N` feeds `chunkCount`, but not directly:

```
chunkCount = min(PlayGoChunkCount, max(1, playgoFileCount / 2), max(1, mchunk0Size / 65536))
```

so a small image silently clamps it (the build logs "requested N automatic chunks, using M because the inner image has fewer files"). On the non-publisher paths (`MetadataContainer`, and anything with `UsePublisherPprNaps = false`) it is hard-coded to 1 and `--playgo` does nothing at all.

**The conflict:** `CollectMediaEntries` runs *first* and emits every `sce_sys` file whose name is in the entry table. `playgo-chunk.dat`, `playgo-hash-table.dat`, `playgo-ficm.dat` and `playgo-scenario.json` are all in that table, and the only id the loose-file loop skips is `PARAM_SFO` (`0x1000`) — `GeneratedEntryIds` is literally `{ 4096 }`. Then the generator adds its four entries guarded by `if (!pkg.Entries.Any(e => e.Id == id))`.

So if the dump carries them, **the dump's copies win and the generated ones are never added**. The package ships a chunk map describing the *original* image against a freshly laid-out PFS with different extents and a different file order, and `--playgo N` is silently ignored — which is exactly why deleting them was the only thing that made your test work. The build even tells you, if you read the line: "PlayGo input: complete prepared set found; its layout will be preserved." That is a feature for someone rebuilding a package they themselves prepared, and a trap for anyone rebuilding a dump.

`playgo-scenario.json` (`0x3000`) is the one genuine *input* of the four — it is copied through verbatim, not regenerated — but it names scenarios over chunk ids that the new map no longer has, so it is swept with the rest. If you ever want to keep it, `--retain-sce-sys` keeps all four.

The source folder must contain `sce_sys/`; a minimal `param.json` is generated if absent (pass `--no-param-json` to opt out). `--format` picks the container: `DebugImage` (default, the only installable form), `MetadataContainer` (CNT, metadata only), or `RetailImage` (needs a trusted finalization provider the CLI does not supply).

## Measured on this machine

Apple M2 Pro, macOS 15, .NET 10.0.11 (Homebrew), arm64 — no Rosetta, no Wine:

- `keys` -> `KeysAvailable: True`
- an Application/DebugImage build completed in 0.45 s, 923,318 bytes
- `info` read it back as `FullDebug`, 11 CNT entries, correct content id
- `--deterministic` rebuilds are byte-identical (same SHA-256)

## Differences from the Windows GUI (0.4)

Everything below matches `MainForm.TryCreateBuildOptions` unless noted: Kraken level 7, Kraken threads 0 (auto), deterministic on, `DebugImage`, `UsePublisherPprNaps`, `GenerateParamJsonIfMissing`, OS temp dir, 32-zero passcode, version 01.00, `TitleId = ContentId[7..16]`, `PrimaryId = ContentId`, and no `SdkVersionOverride` (the GUI's override checkbox defaults off).

...including `PublisherImageMode`, which defaults to `PlaintextNoAuth` exactly as the GUI does. That output needs the A53 PPR read selector installed on the console (see `drakmor/ppr-patch`) and is **not** publisher-compatible; pass `--image-mode Native` for the stock encrypted image.

One deliberate divergence:

| | GUI 0.4 | CLI | Why |
|---|---|---|---|
| `KrakenBackend` | `PublishingToolsRequired` | `Auto` | `Auto` is a CLI-level mode with no library equivalent: it uses the native Oodle encoder when it is actually reachable and the managed `BuiltIn` encoder when it is not, and prints which it chose. It never stores uncompressed. |

The CLI additionally exposes `--format` (the GUI hardcodes `DebugImage`) and `--mode AdditionalContentNoData` (absent from the GUI's combo). Those are extra reach, not different defaults.

The GUI's Kraken default fails fast rather than degrading, which is correct **on an unpatched release folder**:

```
$ fpkg build --kraken-backend Oodle ...
error: Original Publishing Tools Oodle Reduced compression was required,
       but the Reduced Oodle backend requires 64-bit Windows.
```

Once `./fpkg patch` has been run with an Oodle library present, this same command succeeds instead, using the native RAD encoder in place of `libScePubTools.dll` — see "Native Oodle on macOS" below.

`--playgo` now inherits the library default (64 in 0.4) instead of forcing 1. Note the tension in upstream's own docs: 64 is the default, yet one chunk is still described as "the verified publisher nwonly profile" and anything above one as "an experimental single-scenario layout". Pass `--playgo 1` for the verified profile.

Not yet exposed by the CLI: `SdkVersionOverride`, `PublishingToolsLibraryPath`.

## Upstream 0.6.9

Release notes: generation of language files from playgo-scenario.json; PlayGo configuration validation improved. Both true, and both understate it — this release rebuilds PlayGo. Library +289 lines net across 29 hunks; the GUI changed by three lines plus strings.

### The 0.6.8 regression it quietly reverts

One call site, three releases:

```csharp
// 0.6.7   initialChunkCount = chunkCount
// 0.6.8   initialChunkCount = 1
// 0.6.9   initialChunkCount = chunkCount
```

0.6.8 kept `BuildFicm(BuildAutomaticFileChunkIds(fileCount, chunkCount))`, which spreads every file evenly across chunks 0..N-1, while declaring only chunk zero initial. Publishing Tools documents `--initial_chunk_count` as "the number of initial chunks" and its validator accepts `1 <= initial <= len(sequence)`, so the shape is legal — but it means the leading run of the download order that must be present before the title starts is chunk zero alone. Everything in a later chunk is, to PlayGo, not downloaded.

0.6.9 fixes it from both ends: `initialChunkCount = chunkCount`, and `BuildFicm(new byte[fileCount])` so every file belongs to chunk zero. `BuildAutomaticFileChunkIds` survives but is no longer called.

The same design appears independently in the SDK-driven toolkit's own GP5 generator: *"Game files remain in chunk zero; each language declared by playgo-scenario.json gets a small generated, uncompressed payload"* and *"every scenario contains all 100 chunks and marks all of them as initial/required."* Two implementations converging is the strongest evidence available without a console.

**The library's verifier does not catch it.** Its scenario check is `total < 1 || total > chunkCount || initial > total`, so `initial = 1` passes. This CLI adds `PlayGoInitialChunkProblem`, which cross-references the FICM file-to-chunk map against the smallest initial count and warns. Measured on one source built twice: the 0.6.8 build reports "PlayGo layout is valid" from the library and "37 of 43 file mappings sit in chunks at or above index 1" from ours; the 0.6.9 build is clean.

### Language chunks

New `BuildLanguageChunkLayout(totalSize, chunkCount, languageMask)`: chunk zero keeps the bulk, each selected language gets up to 1 MiB (64 KiB-aligned) carved out of it, and per-chunk language masks are assigned round-robin so none is empty. Chunks past the language count get a full mask and a zero extent, which Publishing Tools treats as informational ("Chunk #%03d does not contain any files.").

`SplitMainExtent` is gone. That retires this CLI's zero-length-chunk guard, which existed only because extents used to be an even division of the image.

### Scenario presentation

`ResolvePlayGoCounts` became `ResolvePlayGoConfiguration`, returning scenario labels, default scenario id, default language, and the source `playgo-scenario.json` itself, which is now emitted verbatim when valid. Two new fields are read from it: `chunkSupportedLanguages` and `chunkDefaultLanguage`. `MaxScenarioCount` dropped 64 -> **5**, matching the SDK toolkit's `MAX_PLAYGO_SCENARIO_COUNT = 5` ("Runtime API limit in the supported Prospero SDK (SCE_PLAYGO_MAX_SCENARIO)").

### What the official binaries say

Reverse-engineered from `libScePubTools.dll`, `prospero-pub-cmd.exe` and `ric.exe`. Constraints this CLI's output was checked against and satisfies:

| rule | where |
|---|---|
| `scenario_type` must be `0x21` (playmode); it is the only type PS5 GP5 accepts | validator; `--type [playmode]` is the only option in the CLI help |
| first chunk reference of every scenario must be chunk 0 | error `0x80001055` |
| every scenario must list every chunk, no duplicates | errors `0x80001054`, `0x80001053` |
| `1 <= initial_chunk_count <= len(sequence)`, stored u16 at scenario `+0x14` | GP5 parser |
| `default_language` is a u8 index at header `0x24`, must be a set bit of the mask | error "inconsistent Supported Languages(%016llx)/Default Language(%u)" |
| per-chunk language mask at chunk-attr `+0x10`; zero is legal only for a fully unused chunk | error `0x80001056` "Chunk #%03d is never downloaded because no languages are assigned to it." |
| content id occupies 48 bytes at `0x40` (a 36-char id is NUL-padded) | dumper reads `%.48s` |
| extent offset and length are 48-bit; bits 48..51 of the offset are an image index | payload calculator masks with `0xFFFFFFFFFFFF` |
| limits: 1000 chunks, 65535 mchunks, 5 scenarios | validator |
| language id N occupies bit `1 << (63 - N)`; ids 0..30 are the standard set | two independent call sites |

One thing worth watching: a header language mask of `0xFFFFFFFFFFFFFFFF` is a **sentinel meaning "PlayGo Languages are not supported"** in the official tool, not "every language". LibProsperoPkg uses `ulong.MaxValue` as its all-languages default and `BuildLanguageChunkLayout` iterates it as a real mask. Not acted on here, because changing it would diverge from the GUI, but it is a candidate for the next surprise.

### Patcher

All nine IL sites resolve on 0.6.9 with the same ordinals. No change needed.

## Upstream 0.6.8

Release notes: improved image checks including PlayGo ("please verify your images"); fixed an issue with PlayGo map creation; default DRM Standard; automatic downgrading of the required software version to the SDK-specified version. All four confirmed below. Library +552 lines, GUI +3; only `LibProsperoPkg.*` and the GUI changed.

### The PlayGo map was wrong, and the new checker proves it

Two changes in `BuildContainer`:

```csharp
// initial chunk count: was every chunk, now just chunk 0
BuildChunkDat(…, chunkCount, counts.ScenarioCount, chunkCount, ulong.MaxValue)   // 0.6.7
BuildChunkDat(…, chunkCount, counts.ScenarioCount, 1,          ulong.MaxValue)   // 0.6.8

// final extent size
num7 = pkg3.Header.pfs_image_size + pkg3.Header.pfs_image_offset;   // 0.6.7
num7 = pkg3.Header.pfs_image_size + 65536;                          // 0.6.8
```

The doc line "with every chunk required before starting each scenario" went with the first.

The second is the real defect. `LayOutEntries` runs earlier in `BuildContainer` and sets `pfs_image_offset = body_offset + body_size` — the CNT's own extent, not the package-absolute outer-PFS start of `0x10000`. So the last PlayGo extent was sized from the wrong base, and the manifest claimed more of the mount image than exists.

It only bites when the CNT body runs past 64 KiB, which is why it survived: the same source built twice, differing only in how much `sce_sys` it carried, gives

| CNT body | result under 0.6.8's checker |
|---|---|
| 12 entries, `8192 + 57344 = 65536` | passes — the two bases coincide |
| 17 entries, `8192 + 122880 = 131072` | `PlayGo extents cover 0x880000 bytes; the package image before CNT requires 0x870000` |

`0x10000` over, exactly the gap. Rebuilt from the identical source on 0.6.8: clean. So any package this CLI produced before 0.6.8 with a full `sce_sys` carries an overlong final extent. A real 658 MB retail-derived package tested here happens to pass, so it is not universal.

### requiredSystemSoftwareVersion, fourth revision in four releases

```csharp
if (sdkVersionOverride.HasValue) {
    value = ProsperoSdkVersions.ToPackageVersion(valueOrDefault);
    jsonObject["requiredSystemSoftwareVersion"] = VersionText(value);   // new in 0.6.8
} else {
    value = HexVersion(jsonObject["sdkVersion"]);
    jsonObject["requiredSystemSoftwareVersion"] ??= "0x0000000000000000";
}
jsonObject["sdkVersion"] = VersionText(value);
```

```
0.6.4   Max(Min(required, 0x0900…), sdk)   ceiling 9.00, floor sdk
0.6.6   Max(required, sdk)                 no ceiling, floor sdk
0.6.7   passthrough; 0x0 when absent       decoupled entirely
0.6.8   override set -> required = sdk     the SDK drives it
```

This is the downgrade lever that did not exist in 0.6.6 or 0.6.7. Measured: a source declaring `requiredSystemSoftwareVersion 0x1200000000000000` and `sdkVersion 0x0500000000000000` builds, with `--sdk-version 1`, to `0x0100000000000000` in both fields, source untouched.

### DRM defaults

`applicationDrmCombo.SelectedIndex` moved 0 -> 1, and the constant `StandardApplicationDrmSelection = 1` appeared. `applicationDrmSelection` is now initialised to 1 as well.

Worth recording separately: `additionalContentDrmSelection = 1` is a plain field initializer in **0.6.7 too**, and index 1 for an AC volume maps to `Entitlement`. This CLI shipped `--ac-drm free` in the 0.6.7 alignment, which was simply wrong about the GUI rather than a deliberate divergence. Corrected in 0.6.8.

### The new checker

`ProsperoPlayGo.ValidateLayout(chunkData, ficmData, hashTableData, scenarioJson, expectedContentId, expectedMountSize)` returning `ProsperoPlayGoLayoutInfo(ChunkCount, ScenarioCount, ExtentCount, CoveredBytes, FileCount)`. It checks section overlap, table sizing, chunk->extent and scenario->chunk reference bounds, extent coverage against the mount size, FICM assignments, the FLT table, scenario JSON, default-scenario id range, default language presence in the header mask, and content id as printable ASCII matching the CNT.

It is called from `VerifyPackageQuick`, and `VerifyPackageFull` calls `VerifyPackageQuick` first before adding its three payload passes — so the PlayGo finding is identical between the two, and quick is sufficient for it. Timings on a 658 MB package: quick 1.3 s, full 2.4 s.

The GUI wires `VerifyPackageQuick`/`Full` to two buttons on its Extract tab (renamed "Extract and verify") and **nowhere else**. Its post-build step is still its own `VerifyOutput` — container type, signed byte, outer-PFS mode, marker, optional SHA-256. That is why the release note asks users to verify by hand, and why this CLI now runs the quick pass at the end of a build instead.

### Minor

`BuildTemplateChunkInfo` masks a recovered language mask with `KnownMask` and falls back to all-languages if that empties it. `Gp5Scenario` gained `ShouldSerializeLabel()`. `Gp5Project.ReadFrom`/`WriteTo` call `EnsureDefaultPlayGoScenario`, which fills in `Id 0`, `Type "playmode"`, `InitialChunkCount 1`, `Chunks "0-N"` when `chunk_info` exists with no scenarios.

### Patcher

All nine IL sites resolve on 0.6.8 with the same ordinals as 0.6.7. No change needed.

## Upstream 0.6.6 / 0.6.7

0.6.6 was pulled by Drakmor shortly after release. 0.6.7's notes read: fixed DRM and PlayGo issues; fixed the packaging of uncompressed chunks; added unpacking, image verification and DLC template export. Independent diff of all three trees below; the decompiles were `ilspycmd` over each release's `LibProsperoPkg.dll`.

### The uncompressed-chunk bug, and why it predates 0.6.6

Two coupled defects in how the NAPS `Meta18` table describes the inner-PFS **metadata** region when that metadata does not compress.

`BuildInnerBlocks` tagged every metadata block as Kraken-compressed:

```csharp
// 0.6.4 and 0.6.6
uint flag4 = ((second != 0) ? 0x40450000u : 0x40050000u);
// 0.6.7
bool stored = compressedSize == uncompressedSize;
uint flag4 = (stored ? 0x40090000u : ((second != 0) ? 0x40450000u : 0x40050000u));
```

`0x40090000` is the stored flag. The **data**-block path has carried it since 0.6.4 (`placement.StoreRaw ? 0x40090000u : …`); the metadata path never did.

Worse, when *every* metadata block stays raw, `CompressPayload` returns `compressedFile == null`. 0.6.4 and 0.6.6 then set `metaBlocks = Array.Empty<>()`, and `BuildInnerBlocks` fell through to a fallback that **re-Kraken-packed the metadata at level 7** purely to synthesise a block table — describing compressed sizes and offsets for bytes that were never written, because the raw metadata had already gone to disk. 0.6.7 builds the table from the raw layout instead.

Both bugs are in 0.6.4. They are data-dependent, which is why they shipped.

### requiredSystemSoftwareVersion: three behaviours in three releases

```
0.6.4   Math.Max(Math.Min(required, 0x0900…), sdkVersion)    ceiling 9.00, floor sdkVersion
0.6.6   Math.Max(required, sdkVersion)                       no ceiling, floor sdkVersion
0.6.7   required passthrough; "0x0000000000000000" if absent no ceiling, no floor
```

These fields are **BCD**, not plain hex: `0x1270000000000000` is firmware 12.70, not 18.x. The 0.6.4 clamp constant `648518346341351424` is `0x0900…`, i.e. 9.00.

The 0.6.6 shape was actively harmful on a backported dump. Measured on one whose `param.json` required firmware 12.70 while its `eboot.bin` process-param had been patched down to SDK 10.00.00.40: 0.6.4 with SDK 10 selected produced a package requiring firmware 10.00; 0.6.6 produced 12.70 *regardless of the SDK chosen*, because the floor could only ever raise it. 0.6.7 decouples them entirely — the SDK no longer moves the firmware requirement in either direction, and nothing in the library can lower it below what `param.json` declares.

The GUI's SDK combo went index 1 (SDK 1.00) -> Auto in 0.6.6 -> back to index 1 in 0.6.7. `--sdk-version` defaults to `1` to match; `--sdk-version keep` leaves the source's own values alone.

### PlayGo

- New public `ProsperoPlayGo.ReadChunkCounts(ReadOnlySpan<byte>)`, an in-memory `playgo-chunk.dat` reader (validates `plgx`, version 4096, length at `0x10`; 1..255 chunks, 1..64 scenarios).
- `ResolvePlayGoCounts` gained a **third** precedence rung. Was: source files, then the fallback count. Now: source files -> the GP5 project's `<chunk_info>` -> the fallback. `PlayGoStatus()` reports which rung a build will land on.
- `PlayGoChunkCount` default moved 64 -> 100 in 0.6.6, and the GUI spinner maximum 64 -> 255. `--playgo` now accepts 1..255.
- `SplitMainExtent` lost its guard. 0.6.4 had:

  ```csharp
  if (blocks < chunkCount)
      throw new ArgumentOutOfRangeException("chunkCount",
          $"The PlayGo main extent has {blocks} blocks and cannot be split into {chunkCount} non-empty chunks.");
  ```

  0.6.6 deleted it; **0.6.7 did not restore it**. On 0.6.7 a 3 MB source (45 blocks of 64 KiB) with the new default of 100 chunks builds clean and silently emits 55 zero-length chunk extents. `PlayGoChunkCountProblem()` re-checks this before the build, since the CLI knows the source size.

### sce_sys PNG repair

Not an upstream feature — ours. 0.6.6 added "restoration of full-screen PNG images if they are missing from the dump", but the restore sits in the `else` of a `TryGetValue`: it fires only when the PNG is **absent**, and only for `pic1.png` and `pic2.png`. A present-but-corrupt file is read with `File.ReadAllBytes` and packed unchecked, in every release through 0.6.7. One dump examined here has a `pic2.png` that is 532 bytes of high-entropy data with no PNG signature, beside a valid `pic2.dds`. The same dump has `origin-param.json` containing a PNG, `target-param.json` byte-identical to `pic2.dds`, and `playgo-ficm.dat` byte-identical to `param.json` — all three already caught by the quarantine's name globs, which is a useful independent check on that list.

The repair is done entirely in our own code, including for pic1/pic2, because `ProsperoDdsEncoder.DecodeDdsToPng` decodes the DDS with BCnEncoder (managed) but encodes the PNG with **Magick.NET**, whose native half ships only as `runtimes/win-x64/native/Magick.Native-Q8-x64.dll`. Off Windows its type initializer throws, so the library's own restore path turns a *missing* pic1/pic2 into a failed build rather than a restored one. `PngCodec` therefore does the encoding (zlib plus three chunks) and `MediaRepair` fills in missing pic1/pic2 as well as corrupt ones, so the library never reaches that call.

### Additional-content DRM

New `ProsperoAdditionalContentDrmType { Free, Entitlement }` and `AdditionalContentDrmTypeOverride`. The `drm_type` test was also inverted: 0.6.6 keyed off `!= "free"`, 0.6.7 off a single `flag3` covering both volume types. A `!omitLicense &&` guard was added to the generated debug licence, so free AC now emits none — 0.6.4 and 0.6.6 always did. `param.json` also drops `applicationDrmType` for non-Application volumes now.

### Extraction and verification

The bulk of the release (+2,079 library lines) is a read-side subsystem, and it is **streaming** — no temp files: `ProsperoNapsImage.OpenRead`, `LogicalReadStream`, `DecryptedOuterPfsReadStream`, `PprPfsKraken.Unpack`. `ExtractInnerFiles` / `ExtractCntEntries` / `ExtractSiEntries` gained cancellation, progress and per-file callbacks; new are `ExtractRebuildSourceFiles`, `ExportAdditionalContentTemplate`, `TryReadCntEntry`, `AnalyzeInnerFiles`, `VerifyCntEntryDigests` and parallel `VerifyPackageQuick` / `VerifyPackageFull`. `PfsReader.File.CopyTo` now checks the PFSC magic and streams version 2 through `PprPfsKraken.Unpack`.

One thing got *looser*: `ProsperoNapsImage.ReadRange` lost its completeness assertion (`"NAPS range [0x…,+0x…) has only 0x… decoded bytes."`). A partially covered range now returns zero-filled gaps instead of throwing.

### DLC template export

`ExportAdditionalContentTemplate` refuses anything but `ContentType == 33` (data-bearing AC) and an empty output folder. It runs `ExtractRebuildSourceFiles` — **CNT entries only, the inner PFS is never opened** — then synthesises a GP5.

`RebuildSourcePath` maps entry -> `sce_sys/<name>`, forces id 8192 to `sce_sys/param.json`, and drops 16 ids: the container-generated ones (1 `DIGESTS`, 16 `ENTRY_KEYS`, 32 `IMAGE_KEY`, 128 `GENERAL_DIGESTS`, 256 `METAS`, 512 `ENTRY_NAMES`), the derived ones (1034 `IMAGEDIGS_DAT`, 4098 `PLAYGO_CHUNK_SHA`, 4099 `PLAYGO_MANIFEST_XML`, 4105/4106 `APP__*`, 8208 `PLAYGO_HASH_TABLE_DAT`, 8209 `PLAYGO_FICM_DAT`) and the superseded ones (4096 `PARAM_SFO`, 4103 `PUBTOOLINFO_DAT`, 4104 `APP__PLAYGO_CHUNK_DAT`).

It deliberately **keeps** `license.dat`/`license.info` and `playgo-chunk.dat`, because the GP5 synthesis reads them back:

- `entitlement_key` = hex of `license.info[48..63]`
- `chunk_info` from `TryReadCntEntry(4097) ?? TryReadCntEntry(4104)` via `ReadChunkCounts`; the language mask is the u64 at **offset 56** of `playgo-chunk.dat`, expanded into `supported_languages` + `default_language`
- `c_date` from `param.json -> pubtools.creationDate`
- chunk and scenario labels are placeholders (`"Chunk #N"`, `"Scenario #N"`)

Round-tripped on a package built with 0.6.7: entitlement key `000102030405060708090A0B0C0D0E0F` recovered exactly, and a rebuild from the template reports 0 issues from both `VerifyPackageQuick` and `VerifyPackageFull`. Note that `ProsperoPackageBuilder.Build` does **not** read the GP5's own `content_id`, `passcode` or `entitlement_key` — passing only `SourceMode=Gp5Project` throws `ArgumentException: Content ID is not in the format …`. The host lifts them; the GUI does this too.

### Emulator-ish files in a dump

Exactly three filenames are special, all in `BuildInnerTree` after the source walk:

| file | 0.6.4 | 0.6.6 / 0.6.7 |
|---|---|---|
| `ampr_emu.index` (root only) | kept | **dropped**, logged |
| `fakelib/libSceAmpr.sprx` | **dropped**, silently | same |
| `fakelib/libScePlayGo.sprx` | **dropped**, silently | same |

`FilterFakeLibraryDirectory` also normalises the directory name to lowercase `fakelib`. **`dlc_emu` has no handling whatsoever** — no string match anywhere in the library or the GUI — so it rides into the inner PFS verbatim. Building with decoys shows `ampr/ampr_data.bin`, `ampr_emu.cfg`, `fakelib/libSceSomethingElse.sprx` and both `dlc_emu/*` files surviving; the three above do not. The `RemoveAll` is on the root dir only, so a nested `sub/ampr_emu.index` would survive.

Both filters run *after* the `Populate` our TreeOverlay patch hooks, so container (`.ffpfsc`) builds get them too.

### Our patcher needed no change

The original seven IL sites resolve on 0.6.7 with the same ordinals as 0.6.6 (`Populate|39_17`, `IsLooseElf|39_34`, `IsSelf|39_35`, `GetApplicationSceVersion|39_33`), and site 4 reports identically on 0.6.4, 0.6.6 and 0.6.7. Pattern-based lookup earned its keep: the ordinals moved 0.6.4 -> 0.6.6 and held 0.6.6 -> 0.6.7.

**Two more were needed, and they are not a 0.6.7 change.** Giving `--sdk-version` a default (to match the GUI) made `ConvertLooseElfExecutables` run on every build, and it is a no-op without an SDK override — so a whole code path that had never executed on a container source suddenly did. `g__BuildSdkOverriddenSelf` opens the ORIGINAL executable by `SourcePath` twice: once through `g__OpenExecutableProbe` to locate the `.sceversion` offset, and once in the Write delegate it hands to the replacement `FSFile`, to copy everything before that offset. For a container both are the `ffpfsc:<handle>:/...` scheme, so the build died with:

```
Source tree scan: complete. 0 files, 0 bytes.
error: Could not find a part of the path '.../ffpfsc:724dc787:/Media/Modules/Il2cppUserAssemblies.prx'.
```

Site 8 is `VirtualiseProbe` applied to `BuildSdkOverriddenSelf` — the existing rewrite, unchanged, because `OpenExecutableProbe` is a single chokepoint that the other three probe sites already went through. Site 9 is the Write delegate's `BuildFileIO.Open`, which has the same six-argument `(String, FileMode, FileAccess, FileShare, Int32, FileOptions)` shape as the `newobj FileStream` that site 2 already rewrites, so both now share `RedirectSixArgOpen`.

This was invisible until now because **`--sdk-version` had no default and the container fixtures contained no executables**. The acceptance fixture is a tree of ordinary data files; `ConvertLooseElfExecutables` never had anything to convert. Any container fixture used to check this path has to carry at least one `.prx`, `.self`, `.elf` or `eboot.bin`, and the build has to set an SDK.

Reverting sites 8 and 9 reproduces the error above exactly. With them, a container carrying executables and the equivalent folder produce byte-identical inner files, and `sce_module/libc.prx` is correctly restamped from `.sceversion` `0x0500003300000001` (SDK 5.00.00.33) to `0x0100005000000001` (canonical SDK 1.00.00.50).

## Upstream 0.6.2

An optimisation release, not a correctness fix. Measured on a 679 MB dump: 654,649,338 -> 644,747,390, **-1.51%**. Inner file count identical (23), CNT identical except the NAPS layout descriptor `entry-0000040a.bin` (expected: block coalescing and dedup changed the layout), SI identical. Nothing is dropped, so **rebuilding is optional** — unlike the 0.4 artwork loss below.

- `Automatic` no longer stores blocks uncompressed; it falls back to the managed Kraken encoder.
- Built-in Kraken gained a suffix-trie matcher and sub-chunk forms, breaking the old level plateau and overtaking native Oodle on size.
- New build options: `SourceMode` / `ProjectFilePath` (build from a GP5 manifest), `PfsCompressionFormat` (PFSv2/v3), `KrakenCompressionBlockSize` (128-256 KiB, default 256), `EnableOuterBlockCoalescing` (on by default), shuffle analysis and region hints for PFSv3, and `EntitlementKey` for AC/AL licences.
- Overlapped read/compress/write pipeline, block dedup and an in-build compression cache; builds are slower per level but produce smaller output.

All of these are exposed: `--gp5`, `--pfs-format`, `--no-coalescing`, `--no-relocation-align`, `--shuffle-analysis`, `--entitlement-key`.

## Upstream 0.6.3 / 0.6.4

### First, about 0.6.5: it does not exist

The 0.6.5 zip is **bit-for-bit 0.6.4** — `diff -r` reports identical trees, every DLL hash matches, and `LibProsperoPkg.Gui.dll` is still stamped `0.6.4.0`. Almost certainly a bad repack. Its release note ("forces the DRM mode to be redefined as standard") is not in the binaries either: the 0.6.2 -> 0.6.4 lib diff contains zero DRM-related lines, the GUI has no `applicationDrmType` string at all, and a build with the param.json rewrite disabled, from an `upgradable` source, came out `upgradable`.

Through 0.6.5 the library only ever *reads* the field. `BuildContainer` stamps `drm_type = 16` and content flag `0x08000000` when it equals `"upgradable"`, and `CollectMediaEntries` only generates a debug licence when it equals `"standard"`. The one place it writes `"standard"` is a `??=` fill-in for a *missing* key. So this CLI forced the value itself, as did every other third-party front end (PSVIETHOA FPKG Builder shipped "engine from fpkg-gui 0.6.5 **+ DRM fix**", patching `param.json` and restoring it byte-for-byte, exactly as here).

**Resolved upstream in 0.6.6.** `ApplicationDrmTypeOverride` made the override a real build option; 0.6.7 extended it to additional content and inverted the `drm_type` test to key off `"free"` rather than `"upgradable"`. The `param.json` rewrite is deleted from this CLI as of 0.6.7 — see "What a build changes in the source folder".

### 0.6.3 — disk-full retry

`ProsperoDiskSpaceRecovery.BeginScope(Func<ProsperoDiskFullInfo, bool>, CancellationToken)` installs a build-scoped, opt-in callback. When a write fails with `IOException(ENOSPC)`, the library serialises on a lock, hands the callback the offending path, and re-issues the write if it returns `true` (a generation counter collapses the stampede of parallel writers that all hit the wall at once); `false` cancels the build. The GUI uses it for a "free space and Retry" message box.

**Nothing is registered by default**, so a headless build fails on a full disk exactly as it did on 0.6.2. Nothing to wire up — but if you want the CLI to sit and wait rather than die on a full drive, this is the hook to use.

### 0.6.4 — the ~50% temp saving is real, and it matters most on exFAT

Traced every file in `--temp-dir` through an identical 679 MB build:

| | 0.6.2 | 0.6.4 |
|---|---|---|
| `…pfs_image.dat` | 595,394,560 | 595,394,560 |
| `…naps_pkg_layout.dat` | 100,312 | 100,312 |
| `…cnt.tmp` | **644,022,272** | **47,775,744** |
| **peak temp, logical** | **1.154 GiB** | **0.599 GiB** (**-48%**) |

0.6.2 preallocated the CNT temp file to the *final package size* and filled it late; 0.6.4 streams it, so it never holds more than a working window. That is `Util.BuildFileIO` (buffered, coalesced sequential writes), `Util.RecoverableFileStream` and `Util.BoundedBuildCache` — the whole of the 0.6.2 -> 0.6.4 lib diff, plus the recovery types above. No new `ProsperoBuildOptions` properties.

One caveat on how to read that table. Measured by **allocated blocks** (`du`), both releases peak at ~0.61 GiB on APFS, because 0.6.2's oversized `cnt.tmp` is *sparse*: 644,022,272 bytes logical against 64 KiB allocated for the whole sampled window. **exFAT has no sparse files**, so on the drives this matters on the 0.6.2 reservation was charged in full and the saving is the real 48%. On APFS you mostly get it back as a smaller free-space requirement rather than smaller live usage.

Output is unchanged: the same dump builds to 644,747,678 bytes on 0.6.2, 0.6.3 and 0.6.4, in 25s / 25s / 24s. **No rebuild needed.**

### PFS v3 is not what it looks like

The GUI's "PFS v3" combo entry on its own is a **no-op** — measured byte-identical to v2 on both a real dump and synthetic texture data. The size win comes only from the *shuffle analysis* checkbox, and **the GUI leaves that unticked by default**, unticking and disabling it whenever the format is not v3 or the Kraken backend is `Uncompressed`.

So "v3 works for other people" and "v3 fails to load here" are consistent: they are shipping v3-without-shuffle, which is v2 in a v3 wrapper. `--v3` is therefore a plain alias for `--pfs-format v3`, matching the GUI. `--shuffle-analysis` stays a separate, explicitly experimental opt-in — it is worth ~2.7% on a real dump and ~5% on texture-heavy data, for ~4.3x the build time, and it produced an image a console would not load.

### 0.6.4 GUI defaults, in full

| Control | Default | Maps to |
|---|---|---|
| Package type | Application (APP) | `Mode = Application` |
| Image mode | PLAINTEXT_NOAUTH | `PublisherImageMode = PlaintextNoAuth` |
| Kraken backend | Automatic | `KrakenBackend = Automatic` |
| Compression level | 7 (Optimal3) | `KrakenCompressionLevel = 7` |
| Compression threads | 0 (= CPU count) | `KrakenMaxDegreeOfParallelism = 0` |
| PlayGo chunks | 64 | `PlayGoChunkCount = 64` |
| SDK version | SDK 1.00 (index 1, *not* "Auto") | `SdkVersionOverride` |
| PFS format | PFS v2 | `PfsCompressionFormat = Version2` |
| Prediction level | Auto | `ShufflePredictionCompressionLevel = null` |
| Source mode | Folder | `SourceMode = Folder` |
| Deterministic | on | `DeterministicBuild = true` |
| Outer block coalescing | on | `EnableOuterBlockCoalescing = true` |
| Relocation alignment | on | `EnableRelocationAlignmentAdjustment = true` |
| Shuffle analysis | **off** | `EnableShufflePatternAnalysis = false` |
| Skip PFS input check | off | `SkipPfsInputDataAllowedCheck = false` |
| Final SHA-256 | off | verification only |

Hardcoded, not surfaced anywhere: `OutputFormat = DebugImage`, `UsePublisherPprNaps = true`, `KrakenCompressionBlockSize = 262144`, `PreCompressionShufflePattern = None`, `GenerateParamJsonIfMissing = true`.

**What moved by 0.6.7** (the table above is the 0.6.4 record, kept as-is):

| Control | 0.6.4 | 0.6.6 | 0.6.7 |
|---|---|---|---|
| PlayGo chunks | 64 | **100** | 100 |
| PlayGo chunks, spinner max | 64 | **255** | 255 |
| PlayGo languages | n/a | **all** (`ulong.MaxValue`), new dialog | all |
| SDK version | SDK 1.00 (index 1) | **Auto** (index 0) | **SDK 1.00** (index 1) |
| Application DRM | n/a (no override existed) | **Free** (new combo) | Free |
| Additional-content DRM | n/a | n/a | **Entitlement** (combo shared with the above) |

0.6.8 then moves Application DRM to **Standard** and leaves Additional-content at Entitlement. See "Upstream 0.6.8".

Everything else in the table is unchanged in 0.6.7: level 7, block 256 KiB, PFS v2, `UsePublisherPprNaps`, coalescing, relocation alignment, deterministic, and the `MinimumLayoutSavings` pair (1 MiB / 0.1%).

The CLI's own defaults differ from the GUI in two deliberate places: `--kraken-backend` is `Auto` (a CLI-level mode that resolves to Oodle when present and BuiltIn otherwise, never Uncompressed), and `--kraken-level` follows whichever backend that picks — 7 for Oodle, 6 for BuiltIn. See "Level 7 is not worth it".

Only four combinations change anything, and all of them are narrowing:

1. **PFS format -> v2** unchecks *and* disables shuffle analysis and skip-PFS-input, and disables the prediction level.
2. **Kraken backend -> Uncompressed** disables the level, threads, prediction, shuffle and skip-PFS controls — but does **not** uncheck them, and the stale level and prediction values are still read at build time. Shuffle analysis is separately forced off in `TryCreateBuildOptions`.
3. **Kraken backend -> BuiltIn or Uncompressed** disables the publishing-tools DLL path and nulls `PublishingToolsLibraryPath`.
4. **Package type -> AC** reveals the entitlement-key field.

Nothing sets a *different* default; there is no hidden profile switching.

## Upstream 0.5

**0.4 silently dropped artwork.** On a real dump whose `sce_sys` carried `pic1.dds` and `pic2.dds` with no matching `.png`, 0.4 wrote neither into the CNT — the package was 16,581,620 bytes smaller, exactly two missing 8,294,548 byte files, with no warning. 0.4's entry table has `PIC0_DDS/PNG` and `PIC1_DDS/PNG` but no `PIC2_*` at all. 0.5 adds them and handles the dds-only case. **Rebuild any package whose `sce_sys` has `pic2.*`, or a `pic1.dds` / `pic2.dds` without a matching `.png`** — that is the "startup splash screens" fix.

Also new:

- **SDK version in executables.** `TryBuildSelfSdkVersionPatch` rewrites the `.sceversion` trailer inside a SELF; 0.4 could only change `param.json`. Exposed as `--sdk-version <major|0xHEX>`. A bare major resolves through `ProsperoSdkVersions` to that release's full executable identifier (`4` -> 4.00.00.31, exec `0x0400003100000211`, package `0x0400000000000000`); a hex value is applied verbatim. The two differ in the resulting package, so pass the major unless you specifically want the package-metadata form.
- **`ProsperoContentVersion`** formalises `NN.NNN.NNN`. `--version` now accepts it (legacy `NN.NN` still works). The `-A`/`-V` filename field is `$"{Major:00}{Minor:00}"` — never truncated, so a minor above 99 makes it longer than four digits.

Everything else is unchanged from 0.4: no new build options, the GUI sets the same values, and a source without dds-only artwork builds byte-identically.

## Upstream 0.4

0.4 rewrote the finalization path. Measured here: a 1.5 GB source went from **126.1 s to 16.4 s (7.7x)**.

- **Fused integrity pass.** `ProsperoNapsPhysicalIntegrityCollector` accumulates the OBCC table while blocks pass through the outer-PFS writer, and `ProsperoInnerPlaintextIntegrityBlock` caches per-block products. The three separate full passes are gone, and `ReadPlaintext` now keeps one cached `FileStream` instead of opening one per 64 KiB block.
- **No image rereads.** `ProsperoChunkCrcAccumulator` builds the PlayGo CRC during the FIH copy, and the CNT is prepositioned so no outer-image copy runs.
- **Progress output.** `ProsperoProgressReporter` — the finalizer no longer sits silent for minutes.
- **SHA3 acceleration upstream.** 0.4 has its own `OpenSslSha3` with the same platform -> OpenSSL -> managed tiering, so the local fork patch is retired.
- **New options:** `KrakenBackend`, `PublishingToolsLibraryPath`, `SdkVersionOverride`. Nothing was removed; 0.4 is a superset of 0.2.

### Kraken backend

> **0.6.2 removed the uncompressed hazard.** `Automatic` now falls back to the managed Kraken encoder instead of storing blocks raw, so the 3.75x case below applies only to 0.4 and 0.5. The CLI's `Auto` mode is consequently mostly redundant on 0.6.2 — it still reports which backend it chose.
>
> **On 0.6.2, prefer `BuiltIn` when size matters:** native Oodle now produces *larger* output than the managed encoder (see "Native Oodle on macOS").

#### The 0.4/0.5 behaviour, for reference

`KrakenBackend.Automatic` (the library default) uses the Publishing Tools Oodle encoder from `libScePubTools.dll` and, when it cannot load, **silently stores every kernel-facing block uncompressed**. That DLL is PE32+ x86-64 Windows native, so **on an unpatched release folder** `Automatic` always degrades. Measured on one fixture:

| backend | output |
|---|---|
| `BuiltIn` | 858,226 bytes |
| `Automatic` | 3,218,930 bytes (3.75x, no warning) |

Once `./fpkg patch` has run with an Oodle library present, `Automatic` no longer degrades: it calls into the same `ProsperoReducedKrakenEncoder` entry points as `PublishingToolsRequired`, so a patched folder routes both to the native RAD encoder and produces identical output (confirmed here: both backends wrote the same 5,650,342-byte package on the same source). See "Native Oodle on macOS" below.

The CLI defaults `--kraken-backend` to **`Auto`**, never to `Automatic`. The two are not the same thing and the difference is the whole point:

| value | when Oodle is reachable | when it is not |
|---|---|---|
| `Auto` (CLI default) | native Oodle | `BuiltIn`, with a line saying so |
| `Oodle` | native Oodle | throws; builds nothing |
| `Automatic` (library mode) | native Oodle | **stores every block UNCOMPRESSED, silently** |

Measured here on the same 11 MB source: `Auto` produced 5,650,342 bytes on a patched folder and 5,912,646 bytes on an unpatched one, announcing the choice both times. `Automatic` on an unpatched folder is the 3.75x case in the table above.

`Oodle` is this CLI's name for the library's `PublishingToolsRequired`. The older spelling is still accepted so existing scripts keep working, but it is no longer advertised. `BuiltIn` is the managed encoder; the *validated* encoder is Publishing Tools', so `BuiltIn` output is not guaranteed publisher-identical.

### 0.4 output differs from 0.2

Same source, same size, but different bytes — the first difference is at FIH+0x30, the SHA3-256 of the plaintext outer superblock. Packages built with 0.2 will not reproduce under 0.4. Which one is correct has not been established here.

## Kraken level: the cliff is at 7, and the default sits on the wrong side of it

**`--kraken-level 6` is the setting you want on 0.6.x.** It is faster than 0.5 *and* smaller than 0.5. The default of 7 costs 3.7x the build time for 1.05%.

Same 679 MB dump, `--kraken-backend BuiltIn`, wall clock:

| level | 0.5 | | 0.6.4 | |
|---|---|---|---|---|
| | time | output | time | output |
| 4 | 7.4 s | 654,649,898 | 6.2 s | 651,567,550 |
| 6 | 8.0 s | 654,649,898 | 6.1 s | 651,567,550 |
| **7** (default) | 7.3 s | 654,649,898 | **22.7 s** | **644,747,678** |
| 8 | 6.9 s | 654,649,898 | 31.8 s | 644,616,518 |

0.5 is flat because `ChainWalkLimit` saturates at 128 from level 4 up — levels 4-9 are byte-identical there. 0.6.4 is flat up to 6 and then falls off a cliff.

### Why level 7 is the cliff

Two things switch on together at `ActiveCompressionLevel >= 7`, in `EncodeBlockWithWorkspace`:

```csharp
MatchTable = (!HasActiveCompressionLevel || ActiveCompressionLevel >= 7)
    ? BuildMatchTable(data)     // KrakenSuffixTrieMatcher — does not exist in 0.5
    : null;
```

1. **A compacted suffix trie is built over every block.** `BuildMatchTable` rents `data.Length * 8` ints, clears them, and runs `KrakenSuffixTrieMatcher` to fill four (length, offset) pairs for every position. Measured by calling it directly at the real 256 KiB block size: **24.3 MiB/s** on bulk payload (`resources.resource`), **14.1 MiB/s** on code (`eboot.bin`). The class is absent from 0.5 entirely.

2. **The parser switches from greedy to full DP.** `EncodeChunk` gates the optimal parse on `MatchTable != null`:

   ```csharp
   bool num2 = MatchTable != null;
   bool flag2 = (num2 & allowOptimal) && ProductionWindowedOptimal
                && size > ProductionOptimalMaxBlock && size <= ProductionOptimalWindowedMaxBlock;
   if (num2 && (UseOptimalParse | flag | flag2)) list = ParseOptimal(...); else list = Parse(...);
   ```

   `ProductionWindowedOptimal` (new in 0.6.x) is `true` and `ProductionOptimalWindowedMaxBlock` is `262144`, so **every real block** takes `ParseOptimal` -> `ForwardDp`. In 0.5 there is no windowed variant and `ProductionOptimalMaxBlock` is `4096`, so a 128 KiB block never qualified — the greedy `Parse` ran at every level, which is exactly why 0.5 is flat.

The split, by total CPU time rather than wall clock (`/usr/bin/time`, same build):

| | level 6 | level 7 |
|---|---|---|
| real | 6.04 s | 24.39 s |
| **user** | **29.26 s** | **137.95 s** |

**+108.7 CPU-seconds.** The trie accounts for roughly 26 of them (635 MiB of payload at 24.3 MiB/s); the other ~83 are the DP parse consuming it. So it is not one expensive helper — level 7 buys a different algorithm, and pays for it twice.

### The five-way comparison, and why the answer is Oodle

End-to-end `./fpkg build` on the same 679 MB dump, best of 2 runs each, one at a time:

| config | wall clock | package size | vs smallest |
|---|---|---|---|
| 0.5, L7, BuiltIn | 6.55s | 654,649,898 | +1.536% |
| 0.6.4, L6, BuiltIn | 5.60s | 651,567,550 | +1.058% |
| 0.6.4, L7, BuiltIn | 22.01s | **644,747,678** | — |
| 0.6.4, L6, Oodle | 6.82s | 646,452,646 | +0.264% |
| **0.6.4, L7, Oodle** | **8.16s** | 645,206,714 | **+0.071%** |

Sweeping the level with Oodle shows 7 is not a compromise but the actual optimum — 8 and 9 are **strictly dominated**, slower *and* larger, and 9 is byte-identical to 8 (the Reduced-profile level mapping saturates at 8):

| Oodle level | wall clock | package size |
|---|---|---|
| 6 | 7.02s | 646,452,646 |
| **7** | **7.88s** | **645,206,714** |
| 8 | 10.50s | 645,337,858 |
| 9 | 10.80s | 645,337,858 |

**This is now the CLI default.** `--kraken-level` follows the backend that actually resolves: 7 for Oodle, 6 for BuiltIn (and 7 on pre-0.6.2 libraries, which have no cliff). An explicit `--kraken-level` always wins, and the CLI prints which default it chose unless `--quiet`. So `Auto` landing on BuiltIn because the shim is missing now also drops to level 6 rather than falling into the worst cell in the table above. This is the CLI's second deliberate divergence from the GUI, which is always 7. It comes within 459,036 bytes of the smallest package this tool can build, in 37% of the time.

The reason is the level-7 finding below, seen from the other side: the native shim replaces `ProsperoReducedKrakenEncoder.EncodeBlock`, so `OodleKrakenEncoder` is never entered. The suffix trie, the three greedy seed parses, the DP and the four entropy emits all disappear. With Oodle, L6 -> L7 costs 1.34s and buys 1,245,932 bytes; with BuiltIn it costs 16.4s and buys 6,819,872.

The Pareto frontier is BuiltIn L6 -> Oodle L6 -> Oodle L7 -> BuiltIn L7. **BuiltIn L7 is off the useful part of it**: 13.85s more than Oodle L7 for 0.071%. If you cannot use Oodle (no RAD Oodle library in `fpkg-tools/native/`, or you need a publisher-identical bitstream), `--kraken-level 6` is the fallback and BuiltIn L7 is still not worth its price.

### We tried to make level 7 cheaper. It is not worth it.

Profiled with `dotnet-trace`, then A/B'd with a dnlib rewrite of `ParseOptimal` across synthetic data, out2 and a 15.6 GiB real packfile. Recording the dead ends so nobody repeats them.

Per sub-chunk, level 7 runs **three throwaway greedy parses, a suffix trie, a DP, and four full entropy-coded emits**, and keeps whichever emitted smallest. Level 6 runs one greedy parse and one emit. Self time at level 7: `ForwardDp` 19%, `KrakenSuffixTrieMatcher.Run` 18%, `FindMatchExact` 12%, `FindParetoPairs` 10%, `CommonPrefixLength` 10%, the four emits 13%.

Measured and rejected:

- **GC tuning** — 5% at most. (`Gen2GcCallback.Finalize` looks like 18% of samples; it is an idle finalizer thread, not work.)
- **More threads** — 5.3x on 10 cores (6P+4E), already at the ceiling.
- **SIMD `CommonPrefixLength`** — it is already 8-byte XOR + `TrailingZeroCount`.
- **The encoder's 19 mutable `internal static` knobs**, all reachable by reflection without patching anything (`UseFaef0`, `UseCtmfFinder`, `UseSuffixTrieFinder`, …). Swept every one: best is 6%, and `UseSuffixTrieFinder=false` is slower *and* bigger.

The one patch that worked — drop the two extra greedy seeds and let the DP win whenever it emits validly — is **−19% time for +0.09–0.26% size**, checked with `VerifyBlocks` (every block decoded and byte-compared). Still not enough, because the baseline it improves is so far from level 6:

| 15.60 GiB real packfile | wall clock | output | L7 gain kept |
|---|---|---|---|
| level 6 | **1m 54s** | 10,006,661,309 | 0% |
| level 7 | **14m 11s** | 9,846,503,078 | 100% |
| level 7 + patch | **11m 29s** | 9,862,120,814 | 90.2% |

One rule worth remembering: **the patch's size cost is absolute, not proportional to the gain**, so the less a title gains from level 7, the more of that gain any such shortcut eats — 85.9% kept on out2 (0.639% gain), 90.2% on the packfile (1.601%), 95.8% on synthetic (6.229%).

## Native Oodle

The real RAD encoder is obtainable: the OodleUE SDK (WorkingRobot mirror) ships `liboo2coremac64.2.9.16.dylib` as a universal `x86_64` + `arm64` binary, and stock Oodle does ship `OodleLZ_Profile_Reduced` — it is not a Sony-only build. This is no longer theoretical; it is wired in and measured.

### What you supply, and where

**Nothing native of this project ships or is built any more.** There used to be a C shim, `native/libfpkgoodle.dylib`, which statically linked `liboo2coremac64.a` — which made the shim itself EULA-encumbered, unshippable, and a per-platform Mach-O artefact needing a compiler and an `OODLE_SDK`. `PprPfsKrakenTool` now binds the Oodle entry points it needs directly out of your Oodle *dynamic* library with `NativeLibrary` and unmanaged function pointers. So:

* there is **no compiler step** in any workflow;
* the managed DLLs are the whole distribution;
* the only file you supply is Oodle's own, which is never redistributed here.

Copy it out of an OodleUE 2.9.16 SDK into `fpkg-tools/native/`:

| platform | SDK path | file |
|---|---|---|
| macOS | `lib/Mac/` | `liboo2coremac64.2.9.16.dylib` |
| Linux arm64 | `lib/LinuxArm64/` | `liboo2corelinuxarm64.so.9` |
| Linux x64 | `lib/Linux/` | `liboo2corelinux64.so.9` |

The unversioned spellings and the `ext` supersets are accepted too. Searched, in order: `fpkg-tools/native/`, `fpkg-tools/bin/native/`, `fpkg-tools/bin/`, and the release folder's `native/` (the in-repo fallback). `FPKG_OODLE_DYLIB` overrides the search with an absolute path. The file is loaded by explicit absolute path — never by loader search, because the macOS dylib's `install_name` (`@executable_path/liboo2coremacarm64.2.9.16.dylib`) names a *different* file than the one on disk.

macOS and Linux are both tested here; nothing in the managed path is macOS-specific. See "Linux" below.

### The ABI is pinned to 2.9.16

Four entry points are called (`OodleLZ_Compress`, `OodleLZ_CompressOptions_GetDefault`, `OodleKraken_Decode_Headerless`, `OodleCore_Plugins_SetPrintf`). Everything else comes from `oodle2.h` and is transcribed into managed code: the compressor/profile/jobify enums and, the risky one, the `OodleLZ_CompressOptions` layout — 84 bytes, byte-*packed* (`OOSTRUCT` is `struct __attribute__((__packed__))` on clang/gcc), so `jobifyUserPtr` sits at the unaligned offset 52. A wrong layout would mis-encode silently rather than crash.

So a fifth export, `Oodle_CheckVersion`, is resolved and called immediately after load, with the compiled-in `OODLE_HEADER_VERSION` (`0x2E091030`). That constant folds a format tag, the major and minor version **and** `sizeof(OodleLZ_SeekTable)` into one word, which makes it a real ABI test rather than a version-string comparison. A library that fails it is refused with a message naming both versions, and the marshalled size of the options struct is asserted to be 84 as well. A wrong-version Oodle is reported as exactly that, distinctly from "no Oodle library present".

### Applying the patch

`./fpkg patch` writes a *patched copy* of the release assembly, `LibProsperoPkg.patched.dll`, whose three `ProsperoReducedKrakenEncoder` entry points call `PprPfsKrakenTool` instead of `libScePubTools.dll`. `LibProsperoPkg.dll` itself is never modified. The patch is keyed to a SHA-256 of the stock DLL, recorded in `LibProsperoPkg.patched.stamp`; `LibraryResolver` refuses a stale patch (falling back to the stock, BuiltIn-degrading path with a warning) rather than loading it silently. **Re-run `./fpkg patch` after every upstream `LibProsperoPkg.dll` update.**

Once patched, `--kraken-backend Oodle` — and the default `Auto`, which selects it automatically — build successfully on macOS with no Windows and no `libScePubTools.dll` involved. The output is RAD's **generic Reduced** profile, **not** Sony's Publishing Tools bitstream, and is not guaranteed publisher-identical — `OodleBackend.Describe` states this every time it is asked, and the build log carries the same caveat on every run.

Measured on 11 MB of real binary payload (content id `UP9000-CUSA00001_00-0000000000000000`):

| backend | output |
|---|---|
| `BuiltIn` | 5,912,646 bytes |
| `Oodle` (native, and what `Auto` selects when patched) | 5,650,342 bytes |

**4.44% smaller, 262,304 bytes saved** — but that was measured against 0.5. This project also once estimated **~1.2%** from testing the raw encoder in isolation (official Oodle at 16.66% vs `BuiltIn`'s 16.86%, on a 512 MiB slice of a real game pak) before it was wired into the build and measured end-to-end.

> **0.6.2 reverses this.** On a 679 MB real dump, level 7:
>
> | backend | output | time |
> |---|---|---|
> | `BuiltIn` | 644,747,390 | 39 s |
> | `Oodle` | 645,206,426 | 13 s |
>
> Oodle is now **459,036 bytes larger (+0.07%)**. Drakmor's suffix-trie matcher and sub-chunk forms closed the gap and passed it. Oodle's remaining advantage is **speed — about 3x** — so `Auto`, which prefers Oodle, now picks the larger output. Pass `--kraken-backend BuiltIn` when size matters, `Oodle` when build time does. The earlier 4.44% and 1.2% figures stand only against 0.4/0.5.

**These two numbers are not directly comparable and neither supersedes the other:** the 1.2% figure is a *raw encoder compression ratio* on a 512 MiB *game-pak-like* corpus; the 4.44% figure is *final package size* on an 11 MB corpus of *Windows DLLs*. Both the corpus and the denominator differ. The 4.44% figure is what this specific 11 MB corpus measured — it says nothing about how the encoder behaves on pak-like input, where the older 1.2% raw-ratio estimate may still hold. Full acceptance details — counters (zero halves stored raw, zero blocks rejected on this corpus), two byte-identical `--deterministic` builds, and a passing `fpkg verify` — are in `docs/superpowers/plans/2026-09-12-native-oodle-results.md`.

`BuiltIn` remains the CLI's default. Native Oodle is opt-in and only available once `./fpkg patch` has been run against the release folder in use, with an Oodle library present.

### Linux

The managed path is platform-agnostic — `NativeLibrary` and unmanaged function pointers work everywhere, `OodleLibrary` already selects Linux file names, and the SDK ships `liboo2corelinux64.so.9` / `liboo2corelinuxarm64.so.9`. The CLI has been run on Linux. The items that used to block it structurally are addressed:

| where | what |
|---|---|
| `fpkg-cli/fpkg.csproj` | no longer pins a RID; the framework-dependent build is portable IL. `publish.sh --standalone` still needs a RID, per platform, since a self-contained build inherently is one |
| `publish.sh` | builds `--standalone` for any of `osx-arm64` / `osx-x64` / `linux-arm64` / `linux-x64` (defaulting to the host's, via `uname`), and names the zip after the RID. `du -h` output still differs cosmetically between BSD and GNU — it only affects the size printed in the build log |
| `patch-oodle.sh` | used to search only for the macOS Oodle file names; now picks the platform's names the same way `OodleLibrary` does |
| `fpkg` (launcher), `patch.sh` | POSIX `sh` throughout, audited again for this pass; no bashisms found. The `brew install dotnet` messages are not actually Linux-specific — Homebrew supports Linux |
| naming | fixed: `fpkg-cli/`, `fpkg-cli.zip`, `NOTES.md` no longer say macOS anywhere but this section's own historical framing |
| `LibProsperoPkg` itself | runs off macOS; `libScePubTools.dll` is a Windows DLL and is not used on any platform |

No shell script in this repo uses `stat -f%z` or `shasum` — that was already true before this pass and remains so.

`--standalone` for a non-host RID remains an untested code path — cross-publishing a self-contained build needs the target runtime's NuGet packages, which this machine has never fetched.

## Building from a .ffpfsc or .exfat container

`--source` normally has to be a materialised folder — a full copy of the dump on real disk. `./fpkg patch` (with no flags, or explicitly `--ffpfsc`) applies a second, independent patch — alongside and separate from the Oodle patch above — that lets `--source` point at a **file** instead: a `.ffpfsc` container (or a raw `.exfat` image) captured straight off a PS5, with nothing extracted first.

```sh
./fpkg patch             # both patches: Oodle (if an Oodle library is present)
                         # and ffpfsc
./fpkg patch --ffpfsc    # only the virtual-source patch
./fpkg patch --oodle     # only the Oodle patch
```

Like the Oodle patch, this never modifies `LibProsperoPkg.dll` itself; it writes a patched copy (`LibProsperoPkg.patched.dll`) that `LibraryResolver` prefers when present, falling back to the stock DLL. Two independently patched copies can't coexist, so requesting both patches (the default) produces one copy carrying both.

### Checking what actually got applied

There is no longer a rebuild step after patching. `fpkg.csproj` used to set its `HAS_VIRTUAL_SOURCE` compile constant from `FpkgVirtualSource.dll` *existing at compile time*, and that file only appeared once `patch.sh` had built it — so an `fpkg` binary compiled before the first patch had the whole container code path compiled out and kept refusing file sources however many times you re-ran the patch. `FpkgVirtualSource` is a `ProjectReference` now and always ships in `fpkg-tools/bin/`, so the constant is unconditional and that skew is gone. The run-time gate below is what decides whether container sources work.

Check what was applied with:

```
$ ./fpkg version
LibProsperoPkg: release …
  loaded from:  …/LibProsperoPkg.patched.dll
  patches:      oodle, ffpfsc
```

`patches:` is read from `LibraryResolver`'s own selection — the file it actually loaded — not from whichever stamp happens to be lying around.

### `./fpkg patch --oodle` disarms container builds

A bare `--oodle` (or a bare `--ffpfsc`) means *exactly that one patch*. Every run restarts from the stock DLL, so `./fpkg patch --oodle` after a full run **replaces** a both-legs `LibProsperoPkg.patched.dll` with an Oodle-only one. Container builds stop working. It says so on the way out:

```
$ ./fpkg patch --oodle
patched LibProsperoPkg.patched.dll: oodle
note:  this LibProsperoPkg.patched.dll carries NO ffpfsc leg, so container
       sources (--source <file.ffpfsc>) will be REFUSED at build time.
       Run ./fpkg patch with no flags to apply every patch again.
```

and the next container build refuses rather than running:

```
$ ./fpkg build --source /tmp/out2.ffpfsc --out /tmp/out …
error: --source is a file, which needs the virtual-source patch: /tmp/out2.ffpfsc.
Run ./fpkg patch to enable .ffpfsc and .exfat sources.
```

**This refusal is load-bearing, not a convenience.** Before it existed, the build went ahead with an unpatched `Populate`: `TreeOverlay` never ran, nothing populated the tree, and the result was an **empty package that passed `fpkg verify`** — the structural check inspects the FIH and outer superblock, which are perfectly well-formed on an image containing no files. Verification passing therefore proved nothing about the content, and the only symptom was `extracted inner files: 0`.

The check asks the **loaded module**, not a stamp file: the patcher imports the shim's entry points, so a `LibProsperoPkg` carrying the ffpfsc leg necessarily holds an assembly reference to `FpkgVirtualSource` and one without it necessarily doesn't. (Same technique the Oodle path uses on `PprPfsKrakenTool`.) That covers all three ways the two can come apart: a bare `--oodle` run, an upstream `LibProsperoPkg.dll` update making the stamp stale so the resolver falls back to stock, and simply deleting the patched DLL.

Without any patch at all, the refusal is the same message, raised at compile-time-gate level instead:

```
error: --source is a file, which needs the virtual-source patch: /path/to/dump.ffpfsc.
Run ./fpkg patch to enable .ffpfsc and .exfat sources.
```

### What actually touches disk

`sce_sys/` is small (tens of MB: icons, param.json, trophy and UDS data) and several build stages need it as real files — `DrmTypePatch` rewrites `param.json` in place and restores it after the build, exactly as it does for a folder source. So on `--source some.ffpfsc`, the CLI opens the container, finds its single app root (a directory holding `sce_sys/param.json` — more than one, or none, is a hard error naming every candidate), and **stages only `sce_sys/**` into a temp subfolder** (`<temp-dir>/fpkg-vsrc-<handle>/sce_sys/`). Everything else — the multi-hundred-MB `eboot.bin`, `Media/`, `sce_module/`, whatever the title ships — is never copied. It streams straight out of the container's exFAT reader as the builder asks for each block, through a marker file (`.fpkg-virtual-source`) that a patched `Populate` in LibProsperoPkg recognises and reads via `FpkgVirtualSource.TreeOverlay`. The staging root is deleted on every exit path, success or failure (`using`-scoped disposal), so a build that fails after staging doesn't leave a copy behind.

`TreeOverlay.Populate` returning `true` makes the patched `Populate` prologue (`if (TreeOverlay.Populate(node, path)) return;`) skip the library's *own* filesystem walk of the staging root entirely — so `TreeOverlay` itself has to add the staged `sce_sys/` back into the tree, as an ordinary, file-backed subtree (real absolute paths, not the `ffpfsc:` virtual scheme used for everything else), exactly what the library's own folder walk would produce for a real `sce_sys` directory. An earlier version of this patch skipped `sce_sys/**` on both sides — real and virtual — on the mistaken assumption that "the caller's own walk already added it"; it hadn't, and that dropped `sce_sys/keystone` (crypto material) and `sce_sys/pfs-version.dat` (structural) from every container build, verifying and booting-console-risk notwithstanding. The walk also reproduces one narrow exclusion the library's own folder walk applies (a root-level `sce_sys/ext_info.dat` is skipped, an `ext_info.dat` elsewhere is kept) — see the acceptance criterion below for the full history and the current, fully-passing result.

### Known difference: empty directories are dropped

A directory that exists in the container but holds **no files at any depth** does not appear in a container build, where a folder build of the same tree would emit it.

This is structural, not an oversight. `ExfatReader.EnumerateFiles()` yields **files only** — it recurses into directories and returns their contents, and never returns a directory entry itself. `VirtualSource.Enumerate` then *synthesises* the directory entries the tree needs by splitting each file's relative path on `/` and emitting each distinct prefix once. A directory with no files under it contributes no path prefix to split, so nothing synthesises it. The library's own folder walk, by contrast, uses `DirectoryInfo.EnumerateFileSystemInfos()` and sees the empty directory directly.

Nothing observed in a PS5 title's tree depends on an empty directory (the acceptance fixture has none, which is why the two builds come out byte-identical), and every *non*-empty directory is reproduced exactly. But if you ever hit a source that needs one, this is the reason, and the fix would be to have `ExfatReader` surface directory entries as well as files rather than to patch around it downstream.

### Measured read throughput

Against the exFAT reader directly, on this machine:

| access pattern | throughput |
|---|---|
| one shared reader | 692 MiB/s |
| ten independent readers | 3,364 MiB/s |
| the hungriest actual consumer (Kraken encode) | 118 MiB/s |

Both figures sit far above what any single build stage asks for, so streaming from the container is not the bottleneck for a build — Kraken encoding is.

### Wall clock: folder vs. container

Same content (the fixture below, 19 non-`sce_sys` files), `--kraken-backend BuiltIn --kraken-level 6`, measured with `/usr/bin/time -p` including the build's own post-build verification:

| source | wall clock |
|---|---|
| folder (`/tmp/out2`, junk-free) | 6.80 s |
| container (`/tmp/out2.ffpfsc`) | 8.19 s |

The container build costs about 1.4 s more here — staging `sce_sys` plus exFAT directory/FAT overhead — on a build that otherwise runs in single-digit seconds. Not free, but not a scaling concern either: `sce_sys` staging is bounded by `sce_sys`'s own size, not the title's.

### Acceptance criterion

The whole feature reduces to one question: does a container build produce the same package as a folder build of the same content? The test (`FpkgVirtualSource.Tests/AcceptanceTests.cs`, `ContainerAndFolderBuildsShareEveryInnerFileByteForByte`) asserts, in this order:

1. the container build and the folder build contain the **same set** of inner files;
2. **every** inner file is byte-identical between the two;
3. the container-built package **passes `fpkg verify`**;
4. the size delta between the two packages is reported (before any assertion below could fail on it);
5. **the two whole package files are byte-identical** — same length, same SHA-256 — asserted **last**, deliberately after 1-3, so that if this ever fails on some future source tree, the failure message can say plainly that every inner file already matched and the divergence is in *physical layout*, not content, and point straight at enumeration order as the first thing to check (see the history below — that diagnosis took three rounds to arrive at here, the next person shouldn't have to repeat that).

Run it against a real fixture with:

```sh
dotnet test FpkgVirtualSource.Tests -v n --filter ContainerAndFolderBuildsShareEveryInnerFileByteForByte
```

It **skips cleanly** when `/tmp/out2.ffpfsc` or `/tmp/out2` is absent — which means the branch's central safety net silently does nothing on a machine without the fixture. Build it before trusting a green run:

```sh
# /tmp/out2 is an extracted PS5 dump (a folder holding sce_sys/param.json).
cd ~/Developer/MkPFS && /tmp/mkvenv/bin/python -m mkpfs pack folder /tmp/out2 /tmp/out2.ffpfsc
```

That takes ~2.3 s and produces a 599,457,792-byte container. Then:

```sh
cd ~/Developer/fpkg-gui
dotnet test FpkgVirtualSource.Tests -v n --filter ContainerAndFolderBuildsShareEveryInnerFileByteForByte
```

A run that reports the test as *skipped* has checked nothing.

**History, since it's instructive.** This test went through three rounds before it passed cleanly, and each round found something real:

1. **First pass** (before the fix above): `sce_sys/keystone` (crypto material), `sce_sys/pfs-version.dat` (structural) and `sce_sys/about/right.sprx` were silently missing from every container build — present in the container, staged correctly to disk, and then never added to the tree at all, because `TreeOverlay.Populate` returning `true` skips the library's own filesystem walk of that directory entirely (see above). The package still verified cleanly; only a console would have noticed. **Critical**, fixed by walking the staged `sce_sys/` for real.
2. **Second pass** (after that fix): the same walk, being unconditional, also added `sce_sys/ext_info.dat`, which LibProsperoPkg's own folder walk excludes from generic inner content — but only a top-level `sce_sys/ext_info.dat`, by a narrow, specific rule (skip `ext_info.dat` when its parent directory is named `sce_sys` and that directory is a direct child of the tree root — an `ext_info.dat` anywhere else is kept). `TreeOverlay.AddRealTree` now reproduces that exact rule rather than guessing at one.
3. **Third pass, after both fixes**: the container and folder builds of this fixture come out **byte-for-byte identical**:
   ```
   folder:    be40425d06bfecd2cf53e241703fad17a97e7df5c2b94cbd881d344c49b17eb1, 651,566,990 bytes
   container: be40425d06bfecd2cf53e241703fad17a97e7df5c2b94cbd881d344c49b17eb1, 651,566,990 bytes  (cmp: identical)
   ```
   The originally observed 132,104-byte delta and differing SHA-256 (the `d640cb41...` hash in earlier drafts of this doc) turned out to be entirely a symptom of the missing-files bug in round 1 — three fewer files shifted every subsequent AFID assignment. It was **not** evidence of a structural sort-order-vs-filesystem-order limitation, as this doc previously claimed, and the test now asserts this byte-identity (item 5 above) rather than merely observing it.

**This is still not a guarantee for arbitrary source trees**, and item 5 is asserted last precisely because of that. `TreeOverlay`'s container-overlay loop enumerates strictly `(RelativePath, Ordinal)`-sorted; the library's own folder walk uses `DirectoryInfo.EnumerateFileSystemInfos()`, i.e. whatever order the filesystem returns, which is not specified to match Ordinal sort. On this machine, for this fixture, the two orders happen to coincide. A source tree whose filesystem enumeration order differs from Ordinal sort could still produce a different (but equally valid, equally verifying, equally content-complete) physical layout — items 1-3 would still pass, and only item 5 would fail, with a failure message that says exactly that and names enumeration order as the first thing to check, rather than presenting as an opaque hash mismatch.

The lesson for whoever touches `TreeOverlay.cs` next: **the test result is the finding.** The first "green" this test achieved was on the fully correct implementation, not on a weakened assertion — every red run above was a real bug, found in order of severity, and fixed by reading what LibProsperoPkg's own decompiled `Populate` actually does rather than by adjusting the test. If item 5 ever goes red on a different source tree while items 1-3 stay green, that is expected per the paragraph above and is not, by itself, a new bug — but treat every other combination (any of 1-3 failing) exactly as seriously as the first two rounds here.

## Notes

- Requires the .NET **10** SDK (`brew install dotnet`). The project targets `net10.0` and references the `net9.0` library directly, so the .NET 9 runtime is *not* needed.
- `--compress` may report `stored, ratio 100.0 %` — incompressible input falls back to the raw wrapper by design.
- **Retail images are out of reach for both front ends.** The GUI hardcodes `OutputFormat = ProsperoOutputFormat.DebugImage` and never touches `RetailFinalizationProvider`. `--format RetailImage` is wired up here, but the library refuses to emit a structural-only 0x80 image without a trusted provider returning the protected 0x300-byte FIH material, which neither front end can supply.
- If you need the real GUI, use CrossOver or a Windows 11 ARM VM; nothing about this app can be made to draw WinForms natively on macOS.
