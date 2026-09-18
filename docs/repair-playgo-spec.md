# `fpkg repair-playgo` — implementation spec

Rewrites a package's PlayGo metadata in place, to the shape LibProsperoPkg 0.6.9 produces. The payload is never decoded, recompressed or modified — it is copied through verbatim — so the repair takes seconds instead of the hours a rebuild costs, and needs no extraction. It is not copy-free: the output is assembled in a temporary file beside the target and renamed over it, so it needs free space beside the target roughly equal to the package's own size. The write is two passes over that temp file (the SI's CRC covers the repaired mount image, so those bytes must exist before the SI can be built), which means one repair reads and writes the payload twice.

Everything below marked PROVEN was measured on `Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg` (661,006,510 B, built with 0.6.8) against the same source rebuilt with 0.6.9.

## What the defect is

0.6.8 wrote `initial_chunk_count = 1` while `BuildAutomaticFileChunkIds` spread files across every chunk. PlayGo reads that as "almost nothing is downloaded". 0.6.9 puts all files in chunk 0, makes every chunk initial, and gives each language a small chunk with a real extent.

Detected by `PlayGoInitialChunkProblem` in `Program.cs`. The library's own verifier passes these packages: its check is `total < 1 || total > chunkCount || initial > total`, and `initial = 1` is legal.

## What has to change — exactly three CNT entries

| entry | id | orig | new | |
|---|---|---|---|---|
| `playgo-chunk.dat` | 4097 | 6736 | 5376 | shrinks |
| `playgo-ficm.dat` | 8209 | 62 | 62 | same size |
| `playgo-scenario.json` | 12288 | 1981 | 2293 | **grows 312** |

PROVEN: nothing else differs between a 0.6.8 package and its 0.6.9 rebuild.

## PROVEN: the payload never changes

```
orig outer PFS (596,705,280 B @ 0x10000): sha256 3b7702ffc695b125…
new  outer PFS                          : sha256 3b7702ffc695b125…
```
No digest in the FIH/CNT chain hashes the payload. `pfs_image_digest` is `ComputeSblockDigest` over the single 64 KiB outer superblock; `body_digest` is bounded to the CNT body.

## PROVEN: the new entry bytes are derivable from the original alone

Recover from the original `playgo-chunk.dat`:

| value | where |
|---|---|
| chunk count | u16 @ 10 |
| scenario count | u16 @ 14 |
| default scenario id | u16 @ 20 |
| extent count | u32 @ 32 |
| default language id | u8 @ 36 |
| language mask | u64 @ 56 |
| content id | 36 ASCII @ 64 (48-byte slot) |
| extent section offset | u32 @ 216 |
| scenario label section | u32 @ 240, len u32 @ 244, NUL-separated |

Then sum the extent table (16 B records: u64 offset, u64 length, mask both with `0xFFFFFFFFFFFF`):
`total = sum(all)`, `tail = last extent length`, `dataSize = total - tail`.

```csharp
// Shown as if it compiled. LanguageChunkLayout is an internal nested record, so in practice
// this call and its two properties go through reflection — see "Implementation route".
var layout = ProsperoPlayGo.BuildLanguageChunkLayout(dataSize, chunkCount, langMask);
var built  = ProsperoPlayGo.BuildMultiChunkDat(contentId, layout.MainChunkSizes, tail,
                 publisherNwonly: true, includePublisherLabels: true, languageMask: langMask,
                 scenarioCount: scenarioCount, initialChunkCount: chunkCount,
                 defaultScenarioId: defScenario, scenarioLabels: labels,
                 chunkLanguageMasks: layout.ChunkLanguageMasks, defaultLanguageId: defLangId);
```

PROVEN byte-identical to the rebuild's entry (5376 B). Recovered values on the test package:
`chunks=100 scenarios=1 defScenario=0 defLang=1 extents=101 mask=0xFFFFFFFFFFFFFFFF total=0x23920000 data=0x23830000 tail=0xF0000`, labels `["Scenario #0"]`. Extents go 101 → 33.

FICM: 16-byte header, then 2 bytes per file with the chunk id in the first. Zero every chunk id.
PROVEN byte-identical to the rebuild (23 files, `0,4,8,13…95` → all `0`).

`playgo-scenario.json`: 1981 → 2293 B, the delta being `chunkDefaultLanguage` (string) and
`chunkSupportedLanguages` (array), both derived from the language mask. That is the *shape* of the
difference, not the mechanism: 0.6.9 does not append to the old bytes, it calls
`ProsperoPlayGo.BuildScenarioJson(scenarioCount, languageMask, defaultScenarioId)`, which emits the
two extra fields where 0.6.8's did not. `ProsperoPkgBuilder` copies a source
`playgo-scenario.json` **verbatim** when one is present and only generates when it is absent — so the
1981-byte original is itself generated output, and regenerating is what reproduces the rebuild.

Consequence for the repair: regeneration discards any genuine localized scenario presentation. A
package whose `playgo-scenario.json` is not byte-identical to `BuildScenarioJson` output for its own
recovered counts is carrying presentation this tool would destroy, and must be **refused**, not
repaired. Same rule as `pfsimage.xml`.

## PROVEN: the growth fits

```
body_offset 0x2000  body_size 0x3c9e000  → body end 0x3ca0000
last entry (12288) ends at 0x3C9517D     → SLACK 44,675 bytes
```
`LayOutEntries` pads `body_size` up to 64 KiB for the publisher profile (`EntryKeys.Length == 2944`). Entry placement is free: laid out in `pkg.Entries` order at `Align(num + DataSize, 16)`, and the meta table is sorted by id only afterwards — physical order already differs from id order in this package. +312 fits 140×.

If growth ever exceeds the slack: `body_size` rounds up one 64 KiB step and the whole PFS shifts by 0x10000 — a byte copy (~40 s per 40 GB) plus the same reseal. Only header fields move; the FIH does not depend on `pfs_image_offset`.

## Reseal scope — all inside the CNT

Read off the 0.6.9 decompile of `FinishContainer` / `CalcBodyDigests` / `ComputeGeneralDigests`,
not the 0.6.4 decompile this spec was first drafted from. Where the two disagree, 0.6.9 wins.
The order below is `FinishContainer`'s own order and must be preserved — `GENERAL_DIGESTS` is
sealed *before* the body is written, and the body digests are taken *after*.

1. `GENERAL_DIGESTS` (`ComputeGeneralDigests`), written into the entry payload before `WriteBody`:
   - `HeaderDigest` = `ComputeHeaderDigest(header[0..64), ForceFihRelativeImageOffset(header[1024..1152)))`
   - `ContentDigest` = `ComputeContentDigest(descriptor, gameDigest, new byte[32], includeGame)`
     where `descriptor` is 56 bytes: content id ASCII at 0 (≤36), BE32 `drm_type` at 48, BE32
     `content_type` at 52
   - `GameDigest` = `TargetDigest` = `pfs_image_digest` (both set when `content_type != 34`)
   - `SystemDigest` / `PlaygoDigest` = `ComputeConcatDigest` over `ComputeEntryDigest` of each
     contributing entry, ordered ascending by id at runtime, omitted entirely when nothing
     contributes:

     ```csharp
     SystemMediaIds = [0x1006, 0x100D, 0x1200, 0x1220, 0x1240, 0x1280, 0x12A0, 0x12C0, 0x2040, 0x2060];
     PlaygoIds      = [0x1001, 0x2010, 0x2011, 0x3000];
     ```

     `ComputeConcatOverEntries` filters on `e is GenericEntry { FileData: not null }`, so being
     listed in the meta table is not enough — an entry without a materialised payload contributes
     nothing.
   - `ParamDigest` = `ComputeEntryDigest(entry 8192)`
2. body written (`WriteBody`: each entry at `meta.DataOffset`, encrypted entries re-encrypted)
3. per-entry SHA3-256 into `DIGESTS` (`CalcBodyDigests`, hashing `DataOffset..DataSize`, rounded
   up to 16 for encrypted entries). **The loop starts at index 1** over `pkg.Metas.Metas`, which is
   sorted ascending by id; slot 0 (`DIGESTS` itself) keeps whatever the entry payload already held.
4. `body_digest` over `[body_offset, body_size)` — ~63 MB here
5. `digest_table_hash` = SHA3-256 of the `DIGESTS` payload
6. `sc_entries1_hash` = SHA3-256 over the concatenated payloads of, in this **semantic** order:
   `ENTRY_KEYS`, `IMAGE_KEY` (if present), `GENERAL_DIGESTS`, `METAS`, `DIGESTS` — each read from
   the body at `meta.DataOffset` for `meta.DataSize`. Not the entry-table order, which starts with
   `DIGESTS`.
7. `sc_entries2_hash` = the same list minus its last element (`DIGESTS`), except that `METAS`
   contributes only `sc_entry_count * 0x20` bytes rather than its full `DataSize`.
8. `desc_digest` = SHA3(IMAGE_KEY) || SHA3(IMAGEDIGS_DAT), read at `desc_image_key_offset/size` and
   `desc_mandatory_offset/size`. Only when both sizes are non-zero.
9. header written
10. package digest at 4064 = `ComputePackageDigest(header[0..4064))` — **force BE64 `[1040..1048) = 65536`
    in the copy first**. (`ForceFihRelativeImageOffset` is the same edit expressed over the 0x80-byte
    mount descriptor `CNT[0x400..0x480)`; it is used for the `HeaderDigest` preimage in step 1, and
    cannot be used here because it rejects anything that is not exactly 128 bytes.)
11. `ProsperoPublisherRsa.BuildCntHeaderWrap(header[0..4096))` at 4096 — the **same** BE64 force at
    `[1040..1048)` applies to this preimage too. The original spec omitted that.

`PlaygoIds` is four ids. `8208` (`playgo-hash-table.dat`) does not change, and `12288`
(`playgo-scenario.json`) is easy to overlook, but both are in the preimage.

**`~/Developer/LibProsperoPKG` is wrong here and must not be followed.** That tree has
`PlaygoIds = [0x1001, 0x2010, 0x2011]` — three ids, no `0x3000`. It is the Aug 2026 upstream and
predates the shipped release; the 0.6.9 assembly has four. Checked against the stored
`GENERAL_DIGESTS.PlaygoDigest` in both packages — the four-id set reproduces it in each, the
three-id set reproduces neither:

```
original 0.6.8   stored C0D14B2BE200ECEF16BAD2217C6312E32AB29775D3081DECFFA2DDFD189866DB
oracle   0.6.9   stored 86D9C2FECD721325216AD53A7373ACE3215ACC01B3A97AA3688ACDA9D70DD1E6
```

More generally: use the upstream source for orientation, and confirm anything load-bearing against
the shipped assembly. This is the one place they have been caught disagreeing.

`main_ent_data_size` = sum of the first `sc_entry_count - 1` (= 5) entry lengths, taken in
`pkg.Entries` (physical) order, not id order. Verified 2944+2048+480+864+864 = 7200. Re-derive, do
not copy.

CNT header field offsets, from `PkgWriter.WriteHeader`, all **big-endian**:

| off | field | | off | field |
|---|---|---|---|---|
| 16 | `entry_count` u32 | | 256 | `sc_entries1_hash` 32 B |
| 20 | `sc_entry_count` u16 | | 288 | `sc_entries2_hash` 32 B |
| 22 | `entry_count_2` u16 | | 320 | `digest_table_hash` 32 B |
| 24 | `entry_table_offset` u32 | | 352 | `body_digest` 32 B |
| 28 | `main_ent_data_size` u32 | | 1040 | `pfs_image_offset` u64 |
| 32 | `body_offset` u64 | | 1200 | `cnt_region_offset` u64 |
| 40 | `body_size` u64 | | 1208 | `cnt_region_size` u64 |
| 48 | `mandatory_size` u64 — an OFFSET despite the name | | 1296 | `desc_image_key_offset/size`, `desc_mandatory_offset/size` u32 ×4 |
| 116 | `content_type` u32 | | 1312 | `desc_digest` 64 B |

**`mandatory_size` holds an offset, not a size.** Header[48] is IMAGEDIGS' (1034) `DataOffset`,
which is what `PkgWriter.WriteHeader` stores and what `CntReseal` writes back. The name is the
library's own field name and is kept for that reason; do not "correct" the value to the entry's
size to match it, or fixed point 1 breaks.

`mandatory_size`, `desc_mandatory_offset` and `desc_mandatory_size` all derive from the
`IMAGEDIGS_DAT` (1034) meta entry, and `desc_image_key_offset/size` from `IMAGE_KEY` (32), so all
four move if those entries move. In this package they sit at 8,399,200 and 11,136, both **below**
the PlayGo entries, so none of them move.

Encrypted entries in this package: `0x400, 0x401, 0x402, 0x2020, 0x2021`, all ≤1 KB. `0x3000` is NOT encrypted. Entry AES key/IV derive from the entry's own `MetaEntry` (`Sha3_256(meta.GetBytes() ++ second)`, IV `[0..16)`, key `[16..32)`), so any encrypted entry that moves or resizes must be re-encrypted.

## PROVEN: the SI needs two member swaps, not regeneration

7 members; only 2 differ:

| member | |
|---|---|
| `common/etc/naps_meta_18.dat` (617 KB) | same |
| `common/etc/naps_meta_300/301/302/308.dat` | same |
| `common/etc/playgo-chunk.dat` | verbatim copy of the CNT entry |
| `config/<contentId>/playgo-chunk.crc` | same length (40,304 B) |
| `pfsimage.xml` | **absent in this profile** |

The CRC table is CRC32C per 64 KiB block over the mount image **and the CNT region**. It differs only from entry **9106 of 10076** (9106 × 64 KiB = 596,836,352 ≈ outer PFS end + 0x10000), so only the CNT-region tail is recomputed — ~63 MB, not 40 GB.

Technique reference: Drakmor's `postprocess-sdk279-plaintext.py` does the same class of surgery (`replace_stored_zip_member`, `repair_playgo_crc`, `build_cnt_header_wrap`).

**Guard:** if `pfsimage.xml` IS present, refuse. It encodes the full entry table and every digest, and reproducing it is unverified.

## Implementation route

**Corrected.** The first draft said to reach `LayOutEntries` / `FinishContainer` /
`ComputeGeneralDigests` / `CalcBodyDigests` by reflection. That does not work: `FinishContainer`
takes a populated `Pkg`, a `Pkg` carries a `Header`, and the shipped assembly has **no reader for
`Header`** — only `PkgWriter.WriteHeader(in Header)`. Reflecting into the private statics therefore
also means hand-writing a parser for a binary struct whose only specification is its own writer, and
keeping it correct release to release. That is more surface than the reseal itself.

Instead: **reimplement the reseal over the CNT bytes using the public primitives**, which cover every
step above — `ProsperoImageDigests.Sha3_256` / `ComputeEntryDigest` / `ComputeConcatDigest` /
`ComputeHeaderDigest` / `ComputeContentDigest` / `ForceFihRelativeImageOffset` /
`ComputePackageDigest`, and `ProsperoPublisherRsa.BuildCntHeaderWrap`. No `Header` struct is built:
the original CNT header is patched field by field at the offsets tabulated above. No reflection into
private members at all.

What makes that safe is not review but a **fixed point**: reseal the package with *no* entry
changed and require the output to be byte-identical to the input. If that holds, every digest, every
offset and every header field in the chain provably matches what the library itself produced. It is
a total check, not a partial one — strictly stronger than agreeing with a reflected private method on
one sub-digest.

Public and usable directly: `ProsperoPackageArchive.Split` / `Inspect` / `TryReadCntEntry`,
`ProsperoPkgReader.Read`, `MetaEntry`, `GenericEntry`, `Entry.WriteEncrypted`,
`ProsperoImageDigests.*`, `ProsperoPublisherRsa.BuildCntHeaderWrap`,
`ProsperoSiArchive.BuildMembers` / `WriteZip`, `ProsperoPlayGo.BuildChunkCrc` /
`BuildLanguageChunkLayout` / `BuildMultiChunkDat` / `ValidateLayout`.

Caveat on `Split`: it copies all three regions, and a `Stream.Null` destination discards the writes
but not the reads — it still pulls the whole outer PFS off disk. For the CNT and SI alone, use
`Inspect` for the geometry and read those two ranges directly; that is what `PackageRegions.Load`
does, and on a 90 GB package it is the difference between ~90 GB of I/O and ~64 MB.

Caveat on `ProsperoPlayGo`: `BuildLanguageChunkLayout` is public but returns the **internal** nested
record `LanguageChunkLayout`, and `ReadChunkCounts` / `ReadScenarioMetadata` likewise return internal
types. C# cannot name them, so those three calls and their result properties go through reflection —
the only reflection in the feature, and over public methods, not private ones.

## Padding alternative — NOT to be built

A regenerated `playgo-chunk.dat` can be zero-padded back to the original size with `file_length`
(u32 @ 16) set to the padded length. PROVEN: `ValidateLayout` accepts the padded form identically
(`chunks=100 scenarios=1 extents=33 covered=0x23920000 files=23`). Trailing slack is legal — the
validator only requires sections not to overlap and `file_length >= last section end`.

Kept here as a proven property of the format, **not as a mode to implement.** It was proposed when
the +312 growth looked unfixable; the 44,675-byte slack measurement retired it. Building it would
ship a second, weaker output shape that no package on disk exercises and that the acceptance oracle
cannot cover, because it is deliberately not byte-parity.

The real fallback is the one above: bump `body_size` one 64 KiB step, shift the PFS by 0x10000,
reseal. Still byte-parity, still correct.

Until that fallback exists, a package whose growth exceeds its slack must make `repair-playgo`
**refuse and say so**, never silently degrade. Same rule as `pfsimage.xml`.

## CLI

```
fpkg repair-playgo <pkg> [--passcode <32>] [--out <path>] [--in-place] [--dry-run]
                          [--work-dir <dir>] [--verbose]
```
`--dry-run` is the default: report the recovered values, the new entry sizes, the slack, and every digest that would change, writing nothing.

## The in-place write, journalled

`--out` stages the whole repaired package beside the target and renames over it: correct, but it
needs free space roughly equal to the package's own size, and its two passes over that temp file
mean the payload is read and written twice — **~1.32 GB of I/O and 2× disk**, measured on the test
package (661,006,510 B).

`--in-place` instead writes the repair into the target's own footprint: ~128 MB of writes and ~64 MB
of spare disk for the journal, against `--out`'s ~1.32 GB and a second copy of the whole package. It
is cheaper, not free — see "Cost, measured" below. That it is possible at all rests on two
invariants the repair already enforces:

- **The outer PFS never changes.** No digest in the FIH/CNT chain covers the payload (see "PROVEN:
  the payload never changes" above), so nothing before `CntOffset` is ever touched by a write.
- **The CNT region's length is fixed.** `CntRepair` refuses any relayout that changes `body_size`,
  and refuses a CNT region carrying padding past its body. Only the trailing SI changes length, and
  it is the last thing in the file.

Given both, the repaired CNT drops back into exactly the space the original occupied, and only the
SI at the tail needs to grow or shrink.

### The five-step sequence

In order, and the order is the design, not a preference — `SetLength` is deliberately last:

1. Write the journal (below) and flush it to the device.
2. Seek to `CntOffset` and write the repaired CNT, byte for byte the same length as the region it
   replaces. Flush to the device.
3. Rebuild the SI: recompute the mount CRC table by reading the *target file itself* — after step 2
   it already is the repaired mount image, so no staged copy is read instead.
4. Seek past the CNT, write the new SI, then `SetLength` to the new end of file. Flush to the
   device.
5. Delete the journal.

Measured on the test package: CNT region offset 596,770,816, size 63,569,920 (invariant); SI
665,774 B shrinking to 664,414 B; the file itself shrinks by 1,360 bytes, from 661,006,510 to
661,005,150.

### The journal

A sidecar file, `<package>.repair-playgo.journal` beside the target by default (or under
`--work-dir`), holding the two regions the write is about to overwrite:

```
 0   8   magic "FPKGJRN1"
 8   8   TargetLength    original file length
16   8   CntOffset
24   8   CntLength
32   8   SiLength
40  32   Identity        SHA-256 of the FIH block (file[0, 65536)) + the package's full path
72   .   Cnt             CntLength bytes, the ORIGINAL CNT region
 .   .   Si              SiLength bytes, the ORIGINAL SI region
 .  32   Digest          SHA-256 over everything preceding it
```

~64.2 MB on the test package (63,569,920 + 665,774). It is written and flushed to disk **before**
the package is opened for writing at all, and deleting it *is* step 5 — so a journal found on disk
means one of two things: step 2, 3 or 4 was interrupted, or the repair completed and step 5's
`File.Delete` itself failed. Recovery tells those apart by the file's length; see below. ("A journal
always means an interrupted write" is the wrong reading, and acting on it would undo successful
repairs.)

#### The marker

A second, tiny sidecar: `<package>.repair-playgo.inprogress`, **always beside the package**, never
under `--work-dir`, holding the absolute path of the journal as UTF-8 text. It is created and
flushed *before* the journal is written, and deleted immediately after it.

It exists because the journal's location depends on flags, and the recovering run is not the run
that crashed. `--work-dir /tmp` plus a reboot; a forgotten flag; a different one — in every case a
recovery that recomputed the journal path from *its own* flags would look in the wrong place, find
nothing, and fall through to `PlayGoInitialChunkProblem`, which reads entries 4097 and 8209 out of
the already-**repaired** CNT, finds nothing wrong, prints "Nothing to repair" and exits **0** on a
package with a stale SI. Silent, undetectable data loss, in the exact failure the journal was built
for.

So recovery consults the marker **first** and takes the journal path from it when there is one. And
a marker whose journal is missing or unreadable is a **hard refusal, non-zero**: the package is
named as mid-repair, the expected journal path is printed, and the run stops. It never proceeds and
never reports "Nothing to repair" — that path is additionally gated on the marker's absence, because
"nothing to repair, exit 0" is the single most dangerous sentence the command can print.

**Why the identity hashes the FIH block plus the full path, not the CNT.** A half-finished run has
the *repaired* CNT on disk — that is exactly the state the journal exists to detect — so an
identity computed from the CNT could never match at the moment recovery needs it to. Content alone
cannot discriminate a package from its own repaired form either: they share the payload by design,
that being the whole point of the repair, so a hash of anything both states share would match
either one. The path is the only thing that survives a partial write and still tells the two apart,
because it is the one input that is not part of the package's own bytes. A package renamed after a
crash therefore carries an inapplicable journal — its identity can't match either the renamed
package or, restored the other way round, the original path — which fails safe: the run refuses to
touch either file rather than guessing.

### Recovery: the discriminator, and the trap

On the next run, before any guard, recovery checks for a journal. If one exists and its identity
matches, the question is *which side of the write did the crash land on* — and the answer decides
between overwriting the package (wrong, and it destroys it) and discarding the only copy of its
original CNT and SI (wrong, and it destroys it just the same). The discriminator is the file's
**length** against the journal's stored `TargetLength`:

- `length == TargetLength` → steps 2–4 never finished → **restore** from the journal.
- `length != TargetLength` → the repair ran to completion (`SetLength` is the last act of step 4,
  so a changed length is only possible after it) → the journal is stale, left behind by a
  `File.Delete` that failed at step 5 → **delete it, do not restore.**

`PlayGoInitialChunkProblem` — "does this package still need repairing?" — must **not** be used for
this, even though it looks like the natural check. A file interrupted between steps 2 and 4 has the
*repaired* CNT on disk with a *stale* SI: the detector reads entries 4097 and 8209 out of the
already-repaired CNT, finds nothing to flag, and reports the package as fixed — on precisely the
file that most needs restoring. Using it would delete the journal and leave the user with a broken
package and no way back.

**The SI-growth guard and the discriminator are coupled.** The length discriminator is only sound
because the rebuilt SI never grows past the original SI's length — the in-place writer refuses
outright if it would. If that guard were ever relaxed, a crash partway through writing a longer SI
could push the file past `TargetLength` before `SetLength` is reached, and recovery would read the
half-written result as "completed" and discard the journal, leaving unprotected exactly the file
the discriminator exists to protect. Relaxing the guard makes the discriminator wrong; the two are
not independent decisions.

**The coupling is not total, and the residual is deliberate.** The guard is `>`, not `>=`: an SI
rebuilt to *exactly* the original's length is permitted. Such a repair completes without ever
changing the file's length, so recovery reads it as "interrupted" and restores a repair that had in
fact succeeded. That is annoying, fully recoverable, and the right direction to be wrong in — unlike
the growing case, which destroys the package. Stated here because the guard is the thing a future
editor will want to relax, and the code comment is not where they will look first.

### `--out` and a leftover journal

Recovery runs ahead of every guard and ahead of the mode being consulted, so an `--out` run can find
a journal too. It acts (restore or discard), stops — and therefore produces **no `--out` file**. It
must exit **non-zero**, as the `--dry-run` branch already does: exiting 0 tells
`fpkg repair-playgo … --out FIXED.pkg && install FIXED.pkg` to carry on with a file that was never
written, or a stale one from an earlier run. The message says plainly that no `--out` file was
produced, and, in the restore case, that the **input** package was modified — which is the one
circumstance in which `--out` writes to its input at all.

### Cost, measured

All figures are for the 661 MB test package, and both columns are stated as **I/O**, not as "work
the user can see".

|                    | `--in-place`                                                              | `--out`                                                        |
|--------------------|----------------------------------------------------------------------------|-----------------------------------------------------------------|
| extra disk needed  | ~64 MB — the journal, sized to the CNT + SI it captures, deleted when the repair completes. **Not zero**: no second copy of the package is made, but the journal is a real allocation and the repair cannot start without room for it. | roughly the package's own size (~661 MB), beside the target, until the rename |
| data written       | ~128 MB — the journal (~64.2 MB) **plus** the repaired CNT and SI (~64.2 MB) written back into the file's own existing footprint | ~1.32 GB — two full passes over the ~661 MB staging file |
| data read          | ~661 MB to load the regions, then ~660 MB for the CRC pass over the repaired image ≈ 1.32 GB | ~661 MB to load the regions, ~661 MB copying the payload into the staging file on each of two passes, ~660 MB for the CRC pass ≈ 2.64 GB |

The headline is not "in-place is free" — it is that in-place never copies the ~596 MB payload, so it
writes ~128 MB where `--out` writes ~1.32 GB, and needs ~64 MB of spare disk where `--out` needs
~661 MB.

## Acceptance

**Two fixed points. Both gate. Nothing ships without both.**

1. **The digest chain.** Reseal the 0.6.8 package with no entry changed. The output must be
   byte-identical to the input. This proves the reimplemented reseal reproduces the library's own,
   field for field.
2. **The semantics.** Repair the 0.6.8 package. The output must be byte-identical to the 0.6.9
   rebuild of the same source. This proves the three new entries and the relayout are right.

The oracle for (2) needs no external artifact — the package is its own source:

```sh
PKG=Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg
./fpkg extract "$PKG" out/
./fpkg extract "$PKG" out/ --rebuild-source
cp -R out/inner src/ && cp -R out/source/sce_sys/. src/sce_sys/
./fpkg build --source src --out oracle/
```

**The oracle must be rebuilt with RAD Oodle, and the build must be patched for it.** That means a
full `./fpkg patch` with a RAD library present in `fpkg-tools/native/` — `--ffpfsc` alone is not
enough. Without it the build silently selects the BuiltIn Kraken encoder and produces an oracle
roughly **6.4 MB larger** (measured: 667,365,986 bytes against 661,005,150, with `EmbeddedCntOffset`
603,127,808 rather than 596,770,816). That oracle then fails gate 2 for a reason that has nothing to
do with the repair.

Two cheap ways to catch it before wasting a gate run:

- The build log prints `kraken backend:`. It must say the Oodle encoder, not `BuiltIn`. The line
  `kraken: Auto selected the BuiltIn encoder (no RAD Oodle library in fpkg-tools/native/...)` is the
  failure, stated plainly, at the top of the log.
- `AcceptanceTests`/`OracleTests` assert the oracle's outer PFS is byte-identical to the original
  package's (`3b7702ffc695b125…`). A correctly built oracle reproduces the payload exactly, because
  the repair never touches it — so a payload mismatch is an encoder mismatch, every time.

A regenerated oracle whose three PlayGo entry sizes are 5376 / 62 / 2293 but whose total size differs
is that mismatch, not a repair defect.

`ValidateLayout` passing, `fpkg verify --full` passing and the `PlayGoInitialChunkProblem` warning
disappearing are all worth having as fast feedback during development. **None of them is the gate.**
Only the two byte-compares are.

### Running the gate, and its trap

```sh
dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter Acceptance
```

Both fixed points, plus the full-verify and warning-cleared checks, live in
`fpkg-cli.Tests/AcceptanceTests.cs` as three `[SkippableFact]`s. Each one skips when either of two
**untracked** local artifacts is missing:

- the 661 MB test package, `Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg`, at the repo root;
- the 0.6.9 oracle rebuild, a single `*.pkg` under `.oracle/pkg/` (gitignored).

**Without both, all three acceptance tests skip — and the suite still reports green.** A CI run that
only checks the process exit code, or only counts passes, certifies nothing: it passes identically
whether the gate ran or was silently skipped in its entirety. Anyone wiring this into CI must run the
filtered command above *and* assert that nothing was skipped — for example, parse the test-run
summary (or the TRX) for a nonzero skip count and fail the build on it, rather than trusting a `0`
exit code alone.

If the oracle under `.oracle/pkg/` is missing, rebuild it with the recipe already given above, run
from the repo root:

```sh
PKG=Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg
O=.oracle
mkdir -p $O
./fpkg extract "$PKG" $O/out
./fpkg extract "$PKG" $O/out --rebuild-source
cp -R $O/out/inner $O/src && cp -R $O/out/source/sce_sys/. $O/src/sce_sys/
./fpkg build --source $O/src --out $O/pkg
```

Verify before trusting it — `./fpkg info $O/pkg/*.pkg | grep -E '4097|8209|12288'` should report
`playgo-chunk.dat` 5376, `playgo-ficm.dat` 62 and `playgo-scenario.json` 2293, and the produced file
should be exactly 661,005,150 bytes. If any of those disagree, the oracle is not the package this
spec measured, and the acceptance gate would be comparing against the wrong thing.

## Open

- **Does any package in the wild carry `pfsimage.xml`?** Still open. The guard (see "the SI needs two
  member swaps" above) is implemented and refuses outright rather than attempting to reproduce it;
  no package examined so far carries one.
- **Are `publisherNwonly` / `includePublisherLabels` really `true` for an Application volume?**
  Confirmed for this package: the byte-identical acceptance gate against the 0.6.9 oracle proves both
  flags are right for *this* package's content type. Still unconfirmed for a non-Application volume —
  but that is no longer a live hazard, because the shipped content-type guard now refuses any package
  that is not the PS5 Application profile outright, so the assumption is enforced rather than merely
  hoped true. Closing this properly — widening the repair beyond Application volumes — needs a
  confirmed non-Application oracle, not just removing the guard.
- **A package with fewer than 31 languages exercises a different `BuildLanguageChunkLayout` branch.**
  Still untested, and now unreachable through the shipped guards: such a package's
  `playgo-scenario.json` would not reproduce byte-for-byte under the additive-only comparison (the
  scenario-presentation guard above), so it is refused before the repair ever reaches
  `BuildLanguageChunkLayout`. Closing it needs a package built with fewer than 31 languages whose
  stored `playgo-scenario.json` already passes that guard — i.e. one whose scenario JSON was already
  generic — to exercise the narrower-mask branch under test.
