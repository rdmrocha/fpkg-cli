#!/bin/sh
# Kept for compatibility: patching is now unified into "./fpkg patch", which applies every
# available patch (Oodle, virtual source) to one LibProsperoPkg.patched.dll instead of two
# independently patched copies that would fight if both were loaded.
#
# This script keeps its own original precondition — failing loudly, the way it always did,
# when there is no Oodle library to bind — before delegating anything. Once that
# precondition holds, it hands off to "./patch.sh --oodle", which applies only the Oodle
# patch: a script named for one patch should not silently also apply another one it was not
# asked for.
#
# What the precondition checks changed with the shim: there is no libfpkgoodle.dylib to
# build any more. PprPfsKrakenTool binds the user's own RAD Oodle library directly, so what
# has to be present is that library.
set -e

here=$(cd "$(dirname "$0")" && pwd)

[ -f "$here/LibProsperoPkg.dll" ] || {
    echo "error: LibProsperoPkg.dll not found in $here" >&2; exit 1; }

command -v dotnet >/dev/null 2>&1 || {
    echo "error: the .NET SDK is required (brew install dotnet)" >&2; exit 1; }

# Same file names PprPfsKrakenTool.OodleLibrary looks for, picked by platform so this
# precondition does not just fail unconditionally off macOS.
case "$(uname -s)" in
    Darwin) names="liboo2coremac64.2.9.16.dylib liboo2coremac64.dylib
                    liboo2extmac64.2.9.16.dylib liboo2extmac64.dylib" ;;
    Linux)
        case "$(uname -m)" in
            arm64|aarch64)
                names="liboo2corelinuxarm64.so.9 liboo2corelinuxarm64.so
                        liboo2extlinuxarm64.so.9 liboo2extlinuxarm64.so" ;;
            *)
                names="liboo2corelinux64.so.9 liboo2corelinux64.so
                        liboo2extlinux64.so.9 liboo2extlinux64.so" ;;
        esac
        ;;
    *) names="" ;;
esac

found=""
for d in "$here/fpkg-tools/native" "$here/native"; do
    for n in $names; do
        [ -f "$d/$n" ] && found="$d/$n" && break 2
    done
done
[ -n "$found" ] || {
    echo "error: no RAD Oodle library found in fpkg-tools/native/ or native/." >&2
    echo "  Copy the Oodle Core library for this platform out of an OodleUE 2.9.16 SDK" >&2
    echo "  into fpkg-tools/native/ (see dist/README.md for the exact file name)." >&2
    echo "  Nothing needs to be compiled." >&2
    exit 1; }

echo "note: patch-oodle.sh now delegates to ./patch.sh --oodle (Oodle only)." >&2
exec "$here/patch.sh" --oodle
