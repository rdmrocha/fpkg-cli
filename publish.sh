#!/bin/sh
# Builds a distributable zip: the launcher plus a compiled, self-sufficient CLI.
#
#   ./publish.sh                        framework-dependent (small, needs `brew install dotnet`)
#   ./publish.sh --standalone           self-contained for the host RID (large, needs nothing installed)
#   ./publish.sh --standalone <rid>     self-contained for a named RID:
#                                        osx-arm64, osx-x64, linux-arm64, linux-x64
#
# The zip unpacks into any LibProsperoPkg release folder as:
#
#   <release folder>/
#     fpkg                 launcher
#     fpkg-tools/bin/      CLI + dnlib + the two patchers + FpkgVirtualSource +
#                          PprPfsKrakenTool
#     fpkg-tools/native/   empty; where the user drops their own RAD Oodle library
#
# Everything needed to run ./fpkg patch is in there, so a user needs neither this repo nor
# the .NET SDK. The release assemblies are never bundled; the CLI resolves LibProsperoPkg
# from whichever folder it is dropped into, so one zip works with any release.
#
# The framework-dependent build carries no RuntimeIdentifier: it publishes portable IL that
# runs on macOS or Linux, arm64 or x64, with the .NET 10 runtime, so one fpkg-cli.zip covers all
# four. --standalone is inherently per-RID (it bundles a runtime built for one platform), so
# it is named after the RID it was built for, not after "mac".
set -e

here=$(cd "$(dirname "$0")" && pwd)
cli="$here/fpkg-cli"
tools="$here/fpkg-tools"

# The CLI's version, and the only place it is written down: fpkg.csproj reads the same file
# for <Version>, so the zip name and `fpkg version` cannot drift apart. It tracks the
# LibProsperoPkg release this CLI is built against.
version=$(tr -d ' \t\r\n' < "$here/VERSION")
[ -n "$version" ] || { echo "error: VERSION is empty or missing" >&2; exit 1; }

# The host's RID, used as the default when --standalone is given with no RID of its own.
# Derived from uname rather than `dotnet --info` so it never depends on the SDK's own text
# format.
host_rid() {
    os=$(uname -s) arch=$(uname -m)
    case "$os" in
        Darwin) os=osx ;;
        Linux)  os=linux ;;
        *) echo "error: cannot infer a RID for host OS '$os'; pass one explicitly:" >&2
           echo "  $0 --standalone <osx-arm64|osx-x64|linux-arm64|linux-x64>" >&2
           exit 2 ;;
    esac
    case "$arch" in
        arm64|aarch64) arch=arm64 ;;
        x86_64|amd64)  arch=x64 ;;
        *) echo "error: cannot infer a RID for host architecture '$arch'; pass one explicitly:" >&2
           echo "  $0 --standalone <osx-arm64|osx-x64|linux-arm64|linux-x64>" >&2
           exit 2 ;;
    esac
    printf '%s-%s\n' "$os" "$arch"
}

sc="false"; rid=""; out="$here/fpkg-cli-$version.zip"
case "$1" in
  --standalone)
    sc="true"
    rid="${2:-$(host_rid)}"
    case "$rid" in
        osx-arm64|osx-x64|linux-arm64|linux-x64) ;;
        *) echo "usage: $0 --standalone [osx-arm64|osx-x64|linux-arm64|linux-x64]" >&2; exit 2 ;;
    esac
    out="$here/fpkg-cli-standalone-$version-$rid.zip"
    ;;
  "") ;;
  *) echo "usage: $0 [--standalone [rid]]" >&2; exit 2 ;;
esac

[ -f "$here/LibProsperoPkg.dll" ] || {
    echo "error: LibProsperoPkg.dll not found in $here" >&2
    echo "  The CLI compiles against Drakmor's release, which is not vendored here." >&2
    echo "  Unzip a LibProsperoPkg release into this folder and run this again." >&2
    exit 1; }

command -v dotnet >/dev/null 2>&1 || {
    echo "error: the .NET SDK is required to publish (brew install dotnet)" >&2; exit 1; }

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

# Publish to a staging dir: passing -o inside bin/ leaves the intermediate
# bin/Release/<tfm>/ tree behind and doubles the size of the zip.
rm -rf "$tools/bin" "$cli/bin" "$cli/obj"
if [ -n "$rid" ]; then
    dotnet publish "$cli/fpkg.csproj" -c Release --nologo -o "$stage" \
        -r "$rid" --self-contained "$sc" -p:PublishSingleFile=false >/dev/null
else
    dotnet publish "$cli/fpkg.csproj" -c Release --nologo -o "$stage" \
        -p:SelfContained="$sc" -p:PublishSingleFile=false >/dev/null
fi
# publish also runs a build, which writes its own tree into bin/; drop it now.
rm -rf "$cli/bin" "$cli/obj"
mkdir -p "$tools/bin" "$tools/native"
cp -R "$stage"/. "$tools/bin/"
rm -f "$tools/bin"/*.pdb

# The four assemblies the distribution has to carry, over and above the CLI itself. They
# arrive as ProjectReferences, so this is an assertion that the publish really produced
# them rather than a copy step: a zip missing any of them cannot run ./fpkg patch, and the
# failure would only show up in a user's release folder.
for required in FpkgVirtualSource.dll PprPfsKrakenTool.dll \
                oodle-patcher.dll virtual-source-patcher.dll dnlib.dll; do
    [ -f "$tools/bin/$required" ] || {
        echo "error: $required is missing from the publish output; ./fpkg patch would not" >&2
        echo "  work in a release folder. Check the ProjectReferences in fpkg.csproj." >&2
        exit 1; }
done

# Nothing native of ours ships any more: PprPfsKrakenTool binds the user's own RAD Oodle
# build directly, and fpkg-tools/native/ is where they put it. Explain that in the empty
# directory, which also keeps it in the zip.
cat > "$tools/native/README.txt" <<'NATIVE'
Drop your own RAD Oodle library here to enable the native Oodle encoder.

    liboo2coremac64.2.9.16.dylib      macOS   (from an OodleUE 2.9.16 SDK, lib/Mac/)
    liboo2corelinuxarm64.so.9         Linux arm64            (lib/LinuxArm64/)
    liboo2corelinux64.so.9            Linux x64              (lib/Linux/)

It is not shipped here: liboo2core* carries Unreal Engine EULA terms. Nothing else is
needed - there is no compiler step and no shim to build.

Then run:  ./fpkg patch        (it will report "patches: oodle, ffpfsc")

Without it ./fpkg patch applies only the ffpfsc leg and says so, and builds use the
managed BuiltIn Kraken encoder.
NATIVE

# A bundled LibProsperoPkg would pin the CLI to one release. Assert it is absent.
if find "$tools" -name 'LibProsperoPkg.dll' | grep -q .; then
    echo "error: LibProsperoPkg.dll was copied into the output;" \
         "check <Private>false</Private> in fpkg.csproj" >&2
    exit 1
fi

rm -f "$out"
# fpkg-tools/native/ is where the USER drops their own RAD Oodle library, so on any machine
# that has actually used the Oodle backend it is not empty - and a plain `zip -r` of it
# redistributes liboo2core*, which carries Unreal Engine EULA terms. Only README.txt is ever
# shipped from that directory, and it is named explicitly rather than filtered by pattern: an
# allowlist cannot be defeated by a file name nobody predicted.
(cd "$here" && zip -qr "$out" README.md fpkg fpkg-tools/bin \
     && zip -q "$out" fpkg-tools/native/README.txt)

# Belt and braces: prove nothing licence-encumbered made it in, whatever is on disk.
if unzip -l "$out" | grep -qiE 'oo2core|oo2net|oo2tex|libScePubTools|LibProsperoPkg'; then
    echo "error: $out contains third-party binaries that must not be redistributed:" >&2
    unzip -l "$out" | grep -iE 'oo2core|oo2net|oo2tex|libScePubTools|LibProsperoPkg' >&2
    rm -f "$out"
    exit 1
fi
printf '%s  %s  (%s files)\n' "$(basename "$out")" \
    "$(du -h "$out" | cut -f1)" "$(unzip -l "$out" | tail -1 | awk '{print $2}')"
