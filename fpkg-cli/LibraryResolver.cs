using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Fpkg.Cli;

/// <summary>
/// Binds LibProsperoPkg and its dependencies to the release folder the CLI was dropped into,
/// rather than to copies shipped alongside the CLI. This keeps one compiled binary usable with
/// any release: the assemblies are deliberately not copied into our output, so the default
/// resolver misses them and this hook supplies them from the folder that owns them.
///
/// Preference order for the assembly actually loaded as "LibProsperoPkg":
///   1. LibProsperoPkg.patched.dll (./fpkg patch's unified output) when its stamp's recorded
///      source hash still matches the stock LibProsperoPkg.dll beside it.
///   2. LibProsperoPkg.oodle.dll (patch-oodle.sh's legacy, Oodle-only output — left over from
///      before this pipeline unified, or produced by running the Oodle patcher directly) when
///      its own stamp still matches.
///   3. The stock LibProsperoPkg.dll.
/// A patched copy whose stamp is missing, unreadable, or stale is ignored loudly (one warning
/// to stderr) rather than used silently, and resolution falls through to the next candidate.
///
/// There is no native-library hook here any more. The Oodle encoder used to be reached
/// through a C shim (libfpkgoodle.dylib) that needed one; PprPfsKrakenTool now binds the
/// user's RAD Oodle build directly with NativeLibrary, and finds it itself through
/// PprPfsKrakenTool.OodleLibrary.
///
/// The choice made here is published as <see cref="Selection"/> so that callers can ask which
/// patch legs the loaded library actually carries without re-reading (or re-validating) any
/// stamp. That matters beyond diagnostics: the ffpfsc leg decides whether a container build
/// produces a real package or a silently EMPTY one, so <see cref="PatchSelection.FfpfscApplied"/>
/// gates the container-source path in Program.cs.
/// </summary>
internal static class LibraryResolver
{
    /// <summary>
    /// Which LibProsperoPkg this process resolved, and the patches its stamp records. Stays
    /// <see cref="PatchSelection.Stock"/> when no release folder was found or nothing patched
    /// was usable.
    /// </summary>
    internal static PatchSelection Selection { get; private set; } = PatchSelection.Stock;

    [ModuleInitializer]
    internal static void Install()
    {
        string? releaseFolder = FindReleaseFolder();
        if (releaseFolder is null) return;

        Selection = PatchStamp.Select(releaseFolder, Console.Error.WriteLine);
        string? patched = Selection.Path;

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            if (patched is not null && name.Name == "LibProsperoPkg")
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(patched);

            // The release folder first — its assemblies are the ones deliberately not copied
            // into our output — then the CLI's own directory, which is where the distribution
            // keeps the shims (FpkgVirtualSource, PprPfsKrakenTool) that a patched
            // LibProsperoPkg references but a release folder has no copy of.
            foreach (string candidate in new[]
                     {
                         Path.Combine(releaseFolder, name.Name + ".dll"),
                         Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"),
                     })
                if (File.Exists(candidate))
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            return null;
        };
    }

    /// <summary>
    /// Walks up from the binary looking for the folder that holds LibProsperoPkg.dll.
    /// Depth must agree with PprPfsKrakenTool.Tests/ReleaseFolder.cs's walk (Task 9) and
    /// with PprPfsKrakenTool.OodleLibrary's.
    /// </summary>
    internal static string? FindReleaseFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LibProsperoPkg.dll")))
                return dir.FullName;
        return null;
    }
}
