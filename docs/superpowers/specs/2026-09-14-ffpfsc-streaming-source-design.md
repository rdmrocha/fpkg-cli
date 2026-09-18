# Streaming `.ffpfsc` / `.exfat` sources into LibProsperoPkg

Design, 2026-09-14. Status: approved in outline, not yet implemented.

## Problem

Building a PKG from a `.ffpfsc` container today means unpacking it to a full file
tree first — 18.82 GiB on disk for a 6.57 GiB container — and then building from
that tree. The unpack is pure waste: the builder reads each file once, sequentially.

The goal is to build straight from the container, on the Oodle model: **the patch is
optional; with it applied container sources work, without it they do not.**

## What the engine already gives us

Reading a container needs no patching at all. These are public:

- `LibProsperoPkg.Util.IMemoryReader` — `void Read(long pos, byte[] buf, int offset, int count)`
- `LibProsperoPkg.Util.StreamReader(Stream)` — a file-backed `IMemoryReader`
- `LibProsperoPkg.PFS.PfsReader(IMemoryReader, ...)`, `.GetAllFiles()`, `File.GetView()`
- `LibProsperoPkg.PFS.PFSCReader(IMemoryReader)`, `LibProsperoPkg.PFS.InodeFlags`

A `.ffpfsc` is PFS → PFSC → exFAT → files. The first two layers are supported API.
Only the exFAT layer is ours to write.

## What the engine does NOT give us

`FSFile` exposes a public `Action<Stream> Write` and a public
`FSFile(Action<Stream>, name, size)` constructor, but the inner-image pipeline does
not use them for bulk data:

```csharp
if (fSFile.SourcePath != null) { /* stream from the path */ }
else {
    if (fSFile.Size > Array.MaxLength)
        throw new IOException($"Generated inner file '{text2}' is {fSFile.Size:N0} bytes and has no "
                            + "file-backed source. Large generated streams cannot be buffered safely.");
```

Any `FSFile` without a real `SourcePath` is **buffered whole into a `byte[]`** and
hard-fails past ~2.1 GB. The streaming contract is **path-based, not stream-based**.
Hence the virtual-path scheme below rather than a delegate.

## Architecture

### Components

**`FpkgVirtualSource.dll`** (new):

| type | role |
|---|---|
| `ContainerRegistry` | process-wide handle → open container; populated by the CLI, read by the patched IL |
| `ExfatReader` | ported from `~/Developer/MkPFS/mkpfs/exfat.py`, layered on `IMemoryReader` |
| `VirtualSource` | the static surface the patched IL calls |

`VirtualSource` API:

```csharp
static bool   IsVirtual(string? path);
static Stream Open(string path);              // seekable, read-only
static Stream OpenOrFile(string path);        // virtual, else a real FileStream
static IEnumerable<Entry> Enumerate(string virtualRoot);   // name, relative path, isDir, size
```

### Data flow

```
foo.ffpfsc
  └─ PfsReader                     → the single inner file
      └─ PFSCReader                → IMemoryReader over decompressed bytes
          └─ ExfatReader           → directory tree + per-file extents
              ├─ staged to temp:  sce_sys/**
              └─ virtual paths:   everything else
                    ffpfsc:<handle>:/Media/resources.resource
```

A raw `.exfat` enters at the third layer, an uncompressed `.ffpfsc` at the second.
Detection is by header — PFS superblock v2 (magic `0x1332A0B`), then the exFAT boot
signature — with the file extension as a hint only.

### Concurrency

**One shared reader behind a lock.** Measured on a real 599,457,792-byte `.ffpfsc`
built from `/tmp/out2`:

| model | throughput |
|---|---|
| 1 reader, sequential | 692 MiB/s |
| 10 independent readers | 3,364 MiB/s |
| 10 workers, 1 shared reader + lock | 701 MiB/s |

The hungriest consumer is BuiltIn level 6 at 118 MiB/s, so the simplest model has 6x
headroom. Promoting to independent readers is a local change if profiling ever asks
for it. Cold-cache reads 599 MB off disk where an unpacked tree reads 668 MB, so cold
I/O is strictly lower than the alternative.

### What `SourceFolder` is

This is the join between the two halves, and getting it wrong breaks the staging
story. `ProsperoBuildOptions.SourceFolder` is **a real directory**: a staging root at
`<temp>/fpkg-vsrc-<handle>/` containing

- the extracted `sce_sys/**`, and
- a marker file `.fpkg-virtual-source` holding the container handle.

Every unpatched path that resolves something under the source folder therefore sees a
real directory with a real `sce_sys`. Patched `Populate` (site 3) checks for the
marker at the root: when present it emits the real on-disk `sce_sys` subtree as usual
and overlays every non-`sce_sys` entry from the container as a virtual `FSFile`. When
absent it behaves exactly as today, which is what keeps folder sources untouched.

The staging root is created and removed by the CLI, inside the temp directory, never
inside the container's own location.

### Why only `sce_sys` is staged

Staging `sce_sys/**` (tens of MB) means every unpatched metadata path — 
`ResolveSceSysFiles`, `ReadParamJsonInfo`, `EnsureParamJson`, `CollectMediaEntries`,
region hints — reads real files and needs no patch. It also means the existing DRM
patch and sce_sys quarantine keep working unchanged, on the staged copy, and for
container sources they need not restore anything afterwards.

Executables are **not** staged. Genuine ELF/SELF files are pulled whole through
`ReadNode` → `FSFile.Write`, which site 2 routes through the container. eboot.bin at
36 MB in memory is what a folder source already does today.

## The patch: seven sites

| # | target | edit | risk |
|---|---|---|---|
| 1 | `ProsperoPs5InnerFile::OpenRead` → `Stream` | prepend: virtual path ⇒ `VirtualSource.Open` | low |
| 2 | `FSFile::.ctor(String, Int64)` (internal) | skip `Path.GetFullPath` for virtual paths; `Write` routes through `VirtualSource.Open` | low |
| 3 | `ProsperoPkgBuilder::<BuildInnerTree>g__Populate\|39_16` (void, 8 params) | root carries `.fpkg-virtual-source` ⇒ overlay container entries onto the real `sce_sys` subtree instead of `DirectoryInfo.EnumerateFileSystemInfos()` alone | medium |
| 4 | `ProsperoNapsMeta.PlaintextBlockReader.currentStream` : `FileStream` | field type → `Stream`; `new FileStream(...)` → `VirtualSource.OpenOrFile(...)` | **high** |
| 5 | `<BuildInnerTree>g__IsLooseElf\|39_33` → `Boolean` | virtual ⇒ probe magic through `VirtualSource` | low |
| 6 | `<BuildInnerTree>g__IsSelf\|39_34` → `Boolean` | as above | low |
| 7 | `<BuildInnerTree>g__GetApplicationSceVersion\|39_32` → `Byte[]` | as above | low |

Sites 5–7 exist because `ConvertFile` probes **every file in the tree** for ELF magic,
not just executables — including a 16.7 GB packfile — and its no-path fallback
materialises the whole file. Patching these three (all ordinary body rewrites) is
preferred over patching `g__OpenExecutableProbe|39_39`, which returns `FileStream` and
would need signature surgery like site 4.

**Compiler-generated ordinals (`|39_16`, `|39_33`) are not stable across releases.**
The patcher must locate these by pattern — declaring type, name prefix before `|`,
return type and parameter shape — never by full name. Every site carries an
`Expect(...)` assertion in the `OodlePatcher` style; a library whose IL has moved
fails at patch time rather than producing a subtly wrong package.

Site 4 is the one that can bite. Its use sites are all `Stream` members (`Read`,
`Position`, `Dispose`), so the edit is mechanical, and the acceptance test below
catches any error because NAPS integrity digests are computed from exactly these
reads.

## Patch composition

`patch-oodle.sh` writes `LibProsperoPkg.oodle.dll` + stamp. Two independently patched
copies cannot both load. Refactor to a single `patch.sh` that applies whichever
patches are available in sequence to one module and emits
`LibProsperoPkg.patched.dll` + a stamp naming the patches applied. `LibraryResolver`
prefers it; `fpkg version` reports `patched: oodle, ffpfsc`. `patch-oodle.sh` stays as
a thin wrapper.

## CLI surface

`--source` accepts a file as well as a directory. `Program.cs:307-309`'s
`Directory.Exists` + `sce_sys` check branches: a file goes to container detection, and
the equivalent validation is "the container has exactly one app root" — a directory
holding `sce_sys/param.json`, with an error listing candidates if ambiguous.

Without the patch, `--source foo.ffpfsc` fails the way the Oodle path does: what is
missing and how to enable it. Decompress-and-mount stays documented in `NOTES.md` as
the no-patch workaround, not wired in.

## Error handling

Container open failure, missing or ambiguous app root, and exFAT parse errors all
fail before the build starts, naming the container path. Mid-build read failures
propagate as `IOException`, so existing cancellation and cleanup paths apply
unchanged.

## Acceptance test

One unambiguous criterion: **a build from the container and a build from the unpacked
tree produce a byte-identical PKG.** Same source, same flags, equal SHA-256. That
exercises the reader, the extent maths, the staging split and site 4 together.

Fixture: `python -m mkpfs pack folder /tmp/out2 out2.ffpfsc` (2.3s, 599,457,792
bytes) against a `/tmp/out2` folder build.

Secondary: peak temp stays at staged `sce_sys` plus the build's own; wall clock is no
worse than unpack-then-build.

## Milestones

1. Port `ExfatReader` to C# with unit tests against the MkPFS fixture.
2. `FpkgVirtualSource.dll` with the registry and `VirtualSource` surface.
3. Patcher: sites 1, 2, 5, 6, 7 (the low-risk five) + shape assertions.
4. Site 3 (`Populate`), then site 4 (`PlaintextBlockReader`).
5. CLI wiring, staging, `patch.sh` composition refactor.
6. Acceptance test; `NOTES.md` and memory updates.

## Risks

- **Site 4** — the only signature change. Mitigated by shape assertions and the
  byte-identical acceptance test.
- **Per-release maintenance** — seven sites to re-derive versus Oodle's three, and
  three of them are compiler-generated names that shift. Pattern matching plus loud
  failure, never silent.
- **Licence** — MkPFS is GPL-3.0. The author can relicense; decide before the port
  lands in the CLI.
- **Read throughput** — measured and closed, 6x headroom.
