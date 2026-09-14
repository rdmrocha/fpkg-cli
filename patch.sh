#!/bin/sh
# In-repo wrapper around "./fpkg patch". Patching itself now lives in the CLI, so that a
# user who only has the distribution zip can run it with no repo and no .NET SDK; this
# script stays for in-repo use, where the CLI has to be rebuilt from source first.
#
#   ./patch.sh              apply every available patch (Oodle if an Oodle library is
#                           present, ffpfsc)
#   ./patch.sh --oodle      apply only the Oodle patch (fails hard if no Oodle library)
#   ./patch.sh --ffpfsc     apply only the virtual-source patch
#   ./patch.sh --oodle --ffpfsc   same as no flags
#
# The Oodle leg needs a RAD Oodle library for this platform (e.g. liboo2coremac64.2.9.16.dylib
# on macOS; see dist/README.md for the others) from an OodleUE 2.9.16 SDK — in
# fpkg-tools/native/ or, for in-repo work, native/. Nothing is compiled for it:
# PprPfsKrakenTool binds it directly.
#
# See fpkg-cli/PatchCommand.cs for what the patch actually does and why it is safe to
# do it from inside the process that resolves the library being patched.
set -e
here=$(cd "$(dirname "$0")" && pwd)

command -v dotnet >/dev/null 2>&1 || {
    echo "error: the .NET SDK is required to rebuild the CLI (brew install dotnet)" >&2
    echo "  A distribution zip needs neither: run ./fpkg patch directly." >&2
    exit 1; }

# fpkg.csproj compiles the container-source path in unconditionally now, but the CLI still
# has to exist and be current before it can patch anything, and this script is the in-repo
# entry point that guarantees that. publish.sh is deliberately NOT run here: it
# wipes the output tree and regenerates the zip, neither of which "patch the library"
# should do behind the user's back.
echo "building the CLI…" >&2
dotnet build "$here/fpkg-cli/fpkg.csproj" -c Release -v q --nologo \
    -o "$here/fpkg-tools/bin" >/dev/null

exec "$here/fpkg" patch "$@"
