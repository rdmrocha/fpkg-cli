# `native/` — the in-repo fallback for your RAD Oodle library

Nothing is built here any more.

`libfpkgoodle.dylib` used to live here: a small C shim that statically linked
`liboo2coremac64.a`, which meant it carried Unreal Engine EULA terms, could not be
shipped, and had to be compiled per platform against an Oodle SDK. It is gone.
`PprPfsKrakenTool` now binds the Oodle entry points it needs straight out of the user's
own Oodle dynamic library with `NativeLibrary` / unmanaged function pointers, so there is
no native code of ours anywhere and no compiler in any workflow.

What remains is a *location*. `PprPfsKrakenTool.OodleLibrary` searches, in order:

1. `fpkg-tools/native/` — the documented drop location for a distribution install
2. `fpkg-tools/bin/` (the CLI's own directory)
3. **this directory** — the in-repo fallback, so a checkout can be developed against an
   Oodle library without a `fpkg-tools/` tree

Drop `liboo2coremac64.2.9.16.dylib` (from an OodleUE 2.9.16 SDK, `lib/Mac/`) here and
`./patch.sh` will apply the Oodle leg. It is gitignored and must never be committed.

The ABI is pinned to 2.9.16: `OodleLZ_CompressOptions` is transcribed from `oodle2.h`
into `NativeOodle.CompressOptions`, and `Oodle_CheckVersion` is called immediately after
load to refuse any library whose layouts differ, rather than let it corrupt output.
