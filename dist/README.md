# fpkg — PS5 package builder for macOS and Linux

Builds PS5 debug FPKG packages — and it does two things the official tooling does not.

**It streams straight from a dump.** Point `--source` at a `.ffpfsc` container or a raw
`.exfat` image captured off a PS5 and it builds from the file as it is: no extraction, no
unpacking step, no second copy of a 600 GB tree on your disk. A materialised game folder
works too, and produces a byte-identical package.

**It runs on macOS and Linux**, arm64 or x64. The packaging engine is **LibProsperoPkg by
Drakmor**; this is a command-line front end for it, because the official GUI is
Windows-only WinForms and cannot run here.

This is **fpkg 0.6.4**. The version number tracks the LibProsperoPkg release it is built
and tested against, so `fpkg` 0.6.4 belongs with a LibProsperoPkg 0.6.4 folder. `./fpkg
version` prints both, side by side, for whatever you actually have.

Nothing in this zip is Drakmor's work, Sony's, or RAD's. You supply those yourself.

## What you need

- **A LibProsperoPkg release** (0.6.4 or compatible) — unzip it somewhere; that folder
  is your working directory.
- **The .NET 10 runtime**: `brew install dotnet`. The `fpkg-cli-standalone-<version>-<rid>.zip` variant
  carries its own runtime and needs nothing installed.
- macOS or Linux, arm64 or x64. This one `fpkg-cli-<version>.zip` runs on any of the four; the
  self-contained builds are one zip per platform (see "Standalone builds" below).
- **Optional but worth it: a RAD Oodle library**, version 2.9.16. It makes builds roughly
  three times faster. Get it *before* you patch — see "Optional: the native Oodle
  encoder" below for the exact file names to look for.

## Install

Unzip this archive **into the LibProsperoPkg release folder**, so `fpkg` sits beside
`LibProsperoPkg.dll`:

```
your-release-folder/
  LibProsperoPkg.dll          <- Drakmor's, you provide
  LibProsperoPkg.xml
  fpkg                        <- this zip
  fpkg-tools/
    bin/
    native/
```

### Standalone builds

`fpkg-cli-<version>.zip` is framework-dependent: portable IL that runs on macOS or Linux, arm64 or
x64, as long as the .NET 10 runtime is installed. If you would rather not install
anything, grab the self-contained zip for your platform instead — it bundles its own
runtime and is named after the RID it was built for:

- `fpkg-cli-standalone-0.6.4-osx-arm64.zip`
- `fpkg-cli-standalone-0.6.4-osx-x64.zip`
- `fpkg-cli-standalone-0.6.4-linux-arm64.zip`
- `fpkg-cli-standalone-0.6.4-linux-x64.zip`

Unpack whichever one matches your machine the same way, into the release folder.

## Optional: the native Oodle encoder

Faster than the built-in Kraken encoder and nearly as small — on a 679 MB dump, **8.2 s
against 22.0 s** for 0.07% more output. Set this up now if you want it: the library has
to be in place *before* you patch.

It is not bundled here: `liboo2core*` carries Unreal Engine EULA terms. You supply your
own copy, from an **OodleUE 2.9.16 SDK** — searching the web for the file name below, or
for `OodleUE 2.9.16`, is how most people find one.

| platform | file to look for | where it sits in the SDK |
|---|---|---|
| macOS | `liboo2coremac64.2.9.16.dylib` | `lib/Mac/` |
| Linux arm64 | `liboo2corelinuxarm64.so.9` | `lib/LinuxArm64/` |
| Linux x64 | `liboo2corelinux64.so.9` | `lib/Linux/` |

Drop the file into `fpkg-tools/native/`. There is no compiler step — the binding is
managed code, and the version is checked at load. Anything other than 2.9.16 is refused.

Then build with `--kraken-backend Oodle`. Note its output is a generic Reduced-profile
bitstream, not byte-identical to Sony's publisher tooling.

## Patch the library

With the Oodle library in place (or not, if you skipped it), patch once:

```sh
./fpkg patch
```

That rewrites `LibProsperoPkg.dll` into `LibProsperoPkg.patched.dll` and leaves the
original untouched. `fpkg` prefers the patched copy and falls back to the stock one if
the patch is missing or stale.

Re-run `./fpkg patch` after **every** LibProsperoPkg update, and again if you add the
Oodle library later. A patch built against a different release is refused, not silently
used.

## Building

```sh
# from an extracted game folder (must contain sce_sys/)
./fpkg build --source ./MyGame --out ./dist

# straight from a container - no unpacking, no temporary copy of the tree
./fpkg build --source ./MyGame.ffpfsc --out ./dist
./fpkg build --source ./MyGame.exfat  --out ./dist
```

Content ID, title and version are read from `sce_sys/param.json` when not given.
`./fpkg help` lists every flag; `./fpkg version` shows which library is loaded and which
patches are applied.

The output is a debug (FIH) image. Installing it needs a console that accepts one.

## What a build changes in your source, and puts back

A retail dump used as-is produces a package the console refuses to run. Two fix-ups are
therefore on by default. **Both are reverted when the build ends**, including after an
error or a Ctrl-C, and a run killed mid-build is repaired by the next one. Nothing is
left modified in your dump.

**`applicationDrmType` is forced to `"standard"`.** Left at the `"upgradable"` a retail
dump carries, the packaging library stamps a DRM type into the package header and skips
generating the debug licence — that is the lock, and the package will not run. `fpkg`
edits that one value in `sce_sys/param.json` textually, so key order and every other byte
survive, and restores the original afterwards. The backup is kept in a temp directory,
never beside your files. `--retain-param-json` opts out.

**Stale `sce_sys` files are moved aside for the duration of the build**, then moved back:

| file | why it has to go |
|---|---|
| `license.dat`, `license.info` | the retail licence suppresses the debug licence `fpkg` generates, and the package will not launch |
| `playgo-chunk.dat`, `playgo-hash-table.dat`, `playgo-ficm.dat` | they describe the *old* image layout; if present they win over the ones regenerated for your new PFS, including over `--playgo` |
| `origin-param.json`, `target-param.json` | not package entries at all — left in place they ride into the inner filesystem as junk files |

`--retain-sce-sys` opts out. Keep both opt-outs in mind only if you know why you want
them: with either one on, the package builds fine and then fails on the console.

## Compression levels

`--kraken-level` defaults to what the backend is actually good at: **7 for Oodle, 6 for
BuiltIn**. That divergence is deliberate. From 0.6.2 the built-in encoder builds a suffix
trie and runs a full dynamic-programming parse at level 7 and above — 22.0 s against
5.6 s at level 6, for 1.06% smaller output. Oodle bypasses that machinery entirely, so 7
costs it almost nothing and is its optimum; 8 and 9 are slower *and* larger there.

## When something goes wrong

**`--source is a file, which needs the virtual-source patch`** — run `./fpkg patch`. If
you already did, check `./fpkg version` reports `patches: … ffpfsc`; `./fpkg patch
--oodle` on its own leaves container support off.

**`shape check failed: …`** — the LibProsperoPkg release is newer than this build of
`fpkg`, and its internals moved. The patcher refuses rather than producing a subtly wrong
package. Get a matching `fpkg`.

**`Oodle library … version mismatch`** — the library in `fpkg-tools/native/` is not
2.9.16. The ABI is pinned to that version.

**Slow builds** — you are probably on BuiltIn at level 7. Use `--kraken-backend Oodle`,
or `--kraken-level 6`.
