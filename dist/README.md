# fpkg — PS5 package builder for macOS and Linux

Builds PS5 debug FPKG packages — and it does two things the official tooling does not.

**It streams straight from a dump.** Point `--source` at a `.ffpfsc` container or a raw `.exfat` image captured off a PS5 and it builds from the file as it is: no extraction, no unpacking step, no second copy of a 600 GB tree on your disk. A materialised game folder works too, and produces a byte-identical package.

**It runs on macOS and Linux**, arm64 or x64. The packaging engine is **LibProsperoPkg by Drakmor**; this is a command-line front end for it, because the official GUI is Windows-only WinForms and cannot run here.

This is **fpkg 0.6.7-fix1**. The version number tracks the LibProsperoPkg release it is built and tested against, so `fpkg` 0.6.7-fix1 belongs with a LibProsperoPkg 0.6.7 folder. `./fpkg version` prints both, side by side, for whatever you actually have.

Nothing in this zip is Drakmor's work, Sony's, or RAD's. You supply those yourself.

## What you need

- **A LibProsperoPkg release** (0.6.7 or compatible) — unzip it somewhere; that folder is your working directory.
- **The .NET 10 runtime**: `brew install dotnet`.
- macOS or Linux, arm64 or x64. One `fpkg-cli-<version>.zip` runs on all four: it is portable IL, not a per-platform build.
- **Optional but worth it: a RAD Oodle library**, version 2.9.16. It makes builds roughly three times faster. Get it *before* you patch — see "Optional: the native Oodle encoder" below for the exact file names to look for.

## Install

Unzip this archive **into the LibProsperoPkg release folder**, so `fpkg` sits beside `LibProsperoPkg.dll`:

```
your-release-folder/
  LibProsperoPkg.dll          <- Drakmor's, you provide
  LibProsperoPkg.xml
  fpkg                        <- this zip
  fpkg-tools/
    bin/
    native/
```

## Optional: the native Oodle encoder

Faster than the built-in Kraken encoder and nearly as small — on a 679 MB dump, **8.2 s against 22.0 s** for 0.07% more output. Set this up now if you want it: the library has to be in place *before* you patch.

It is not bundled here: `liboo2core*` carries Unreal Engine EULA terms. You supply your own copy, from an **OodleUE 2.9.16 SDK** — searching the web for the file name below, or for `OodleUE 2.9.16`, is how most people find one.

| platform | file to look for | where it sits in the SDK |
|---|---|---|
| macOS | `liboo2coremac64.2.9.16.dylib` | `lib/Mac/` |
| Linux arm64 | `liboo2corelinuxarm64.so.9` | `lib/LinuxArm64/` |
| Linux x64 | `liboo2corelinux64.so.9` | `lib/Linux/` |

Drop the file into `fpkg-tools/native/`. There is no compiler step — the binding is managed code, and the version is checked at load. Anything other than 2.9.16 is refused.

Then build with `--kraken-backend Oodle`. Note its output is a generic Reduced-profile bitstream, not byte-identical to Sony's publisher tooling.

## Patch the library

With the Oodle library in place (or not, if you skipped it), patch once:

```sh
./fpkg patch
```

That rewrites `LibProsperoPkg.dll` into `LibProsperoPkg.patched.dll` and leaves the original untouched. `fpkg` prefers the patched copy and falls back to the stock one if the patch is missing or stale.

Re-run `./fpkg patch` after **every** LibProsperoPkg update, and again if you add the Oodle library later. A patch built against a different release is refused, not silently used.

## Building

```sh
# from an extracted game folder (must contain sce_sys/)
./fpkg build --source ./MyGame --out ./dist

# straight from a container - no unpacking, no temporary copy of the tree
./fpkg build --source ./MyGame.ffpfsc --out ./dist
./fpkg build --source ./MyGame.exfat  --out ./dist
```

Content ID, title and version are read from `sce_sys/param.json` when not given. `./fpkg help` lists every flag; `./fpkg version` shows which library is loaded and which patches are applied.

The output is a debug (FIH) image. Installing it needs a console that accepts one.

## What a build changes in your source, and puts back

A retail dump used as-is produces a package the console refuses to run. Three fix-ups are therefore on by default. **All of them are reverted when the build ends**, including after an error or a Ctrl-C, and a run killed mid-build is repaired by the next one. Nothing is left modified in your dump.

**`applicationDrmType` is set through the library, not by editing your files.** Up to LibProsperoPkg 0.6.5 there was no way to override it and `fpkg` rewrote `param.json` on disk; 0.6.6 added a proper build option, so that no longer happens. The default is `free`, matching the official GUI. Use `--app-drm standard` for a licensed application, or `--ac-drm entitlement` for add-on content that needs an entitlement key — note that `--ac-drm free` also omits `license.dat` and `license.info` entirely, because upstream drives the DRM type and the licence from one switch.

**Stale `sce_sys` files are moved aside for the duration of the build**, then moved back:

| file | why it has to go |
|---|---|
| `license.dat`, `license.info` | the retail licence suppresses the debug licence `fpkg` generates, and the package will not launch |
| `playgo-chunk.dat`, `playgo-hash-table.dat`, `playgo-ficm.dat` | they describe the *old* image layout; if present they win over the ones regenerated for your new PFS, including over `--playgo` |
| `origin-param.json`, `target-param.json` | not package entries at all — left in place they ride into the inner filesystem as junk files |

`--retain-sce-sys` opts out. You need it when building an **exported add-on template** (see below), whose licence and PlayGo files are deliberate inputs rather than stale ones; `fpkg` detects that case and tells you rather than quietly deleting them.

**Corrupt `sce_sys` images are rebuilt from their `.dds`.** Dumps really do contain damaged artwork — one we tested has a `pic2.png` that is 532 bytes of random data with no PNG header at all, sitting next to a perfectly good `pic2.dds`. Upstream never checks: a present file is packed exactly as found, and its own "restore a missing pic1/pic2" path is Windows-only because it encodes through a native library that does not ship for macOS or Linux. So `fpkg` validates `icon0`, `pic0`, `pic1` and `pic2` `.png` properly — signature, chunk chain, CRC-32, `IEND`, no trailing bytes — and regenerates any that fail from the matching `.dds`, in managed code that works everywhere. A damaged image with no usable `.dds` stops the build instead of shipping. `--no-media-repair` opts out.

Keep the opt-outs in mind only if you know why you want them: with any of them on, the package may build fine and then fail on the console.

## Reading and checking existing packages

```sh
# what is in it, and is it sound
./fpkg verify GAME.pkg --quick          # structure, digests, superblock ICV, SI directory
./fpkg verify GAME.pkg --full           # also decodes every block, chunk and inner file

# pull it apart
./fpkg extract GAME.pkg out/            # the inner filesystem
./fpkg extract GAME.pkg out/ --rebuild-source
                                        # just the sce_sys inputs a rebuild needs

# turn an add-on package back into something you can rebuild
./fpkg template DLC.pkg template-dir/
```

`--rebuild-source` and `template` read package entries only and never touch the inner filesystem, so they finish in seconds even on a large package.

A template is the `sce_sys` inputs plus a `.gp5` project carrying the content id, passcode, entitlement key and PlayGo counts recovered from the package. **The payload is not included** — copy your own data tree in beside it, then:

```sh
./fpkg build --source template-dir --out out/ --gp5 template-dir/*.gp5 --retain-sce-sys
```

Add-on packages that carry data are the only kind this works on, because they are the only kind whose whole `sce_sys` lives in the package entries.

## Compression levels

`--kraken-level` defaults to what the backend is actually good at: **7 for Oodle, 6 for BuiltIn**. That divergence is deliberate. From 0.6.2 the built-in encoder builds a suffix trie and runs a full dynamic-programming parse at level 7 and above — 22.0 s against 5.6 s at level 6, for 1.06% smaller output. Oodle bypasses that machinery entirely, so 7 costs it almost nothing and is its optimum; 8 and 9 are slower *and* larger there.

## When something goes wrong

**`--source is a file, which needs the virtual-source patch`** — run `./fpkg patch`. If you already did, check `./fpkg version` reports `patches: … ffpfsc`; `./fpkg patch --oodle` on its own leaves container support off.

**`shape check failed: …`** — the LibProsperoPkg release is newer than this build of `fpkg`, and its internals moved. The patcher refuses rather than producing a subtly wrong package. Get a matching `fpkg`.

**`Oodle library … version mismatch`** — the library in `fpkg-tools/native/` is not 2.9.16. The ABI is pinned to that version.

**Slow builds** — you are probably on BuiltIn at level 7. Use `--kraken-backend Oodle`, or `--kraken-level 6`.

## Thanks

`fpkg` is a thin front end. Essentially all of the hard work — understanding the PS5 package format, the outer and inner PFS, NAPS, PlayGo, the CNT entry tables and the signing and digest chains — belongs to other people.

**[Drakmor](https://github.com/drakmor/) and SvenGDK**, for LibProsperoPkg and its GUI. This tool would not exist without it and does not replace it: it drives their library unchanged, on the platforms their GUI cannot reach. Every packaging decision here is theirs; the bugs are ours. The GUI is the reference implementation, and when the two disagree, believe the GUI.

**The wider PS5 and PS4 homebrew scene**, whose accumulated reverse engineering every one of these tools stands on — the people who worked out the PFS and PKG layouts, the PlayGo chunk tables, the SELF and `.sceversion` structures and the key derivations, and then wrote it all down in public. Most of that work is unsigned, or signed with a handle in a forum post or a commit message. It is no less load-bearing for that.

**[PSBrew's MkPFS](https://github.com/PSBrew/MkPFS)**, used here as an independent second implementation while developing the container reader. Having two readers disagree is how several bugs on our side were found.
