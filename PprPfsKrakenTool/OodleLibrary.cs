using System.Runtime.InteropServices;

namespace PprPfsKrakenTool;

/// <summary>
/// Finds the RAD Oodle dynamic library that <see cref="OodleBackend"/> drives.
///
/// Nothing native of ours ships: the encoder is bound straight out of the user's own Oodle
/// build (see NativeOodle), so this file is the one thing a user has to supply. This class
/// is the single definition of where it is looked for; <c>fpkg patch</c> asks the same
/// question (does the Oodle leg have anything to bind to?) and calls straight into
/// <see cref="Find"/> so the two can never drift apart.
///
/// Deliberately free of any LibProsperoPkg reference: <c>fpkg patch</c> loads this type while
/// deciding whether to apply the Oodle patch, which is precisely the moment at which
/// LibProsperoPkg must NOT be loaded (the patcher is about to rewrite it).
/// </summary>
public static class OodleLibrary
{
    /// <summary>
    /// Overrides the search entirely. Set by tests and by anyone keeping the SDK somewhere
    /// of their own; an absolute path to the Oodle dylib.
    /// </summary>
    public const string PathVariable = "FPKG_OODLE_DYLIB";

    /// <summary>
    /// Accepted file names for this platform, most specific first, exactly as the OodleUE
    /// SDK ships them under lib/. The Core library is the right one and the smaller; Ext is
    /// a superset that exports the same five entry points, so it is accepted rather than
    /// rejected for a user who only has that.
    ///
    /// The managed binding works wherever .NET and an Oodle build do, so the names are
    /// selected per platform rather than hard-coded to macOS. Only macOS is tested here.
    /// </summary>
    public static string[] FileNames { get; } = SelectFileNames();

    private static string[] SelectFileNames()
    {
        if (OperatingSystem.IsMacOS())
            return
            [
                "liboo2coremac64.2.9.16.dylib", "liboo2coremac64.dylib",
                "liboo2extmac64.2.9.16.dylib", "liboo2extmac64.dylib",
            ];
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? ["liboo2corelinuxarm64.so.9", "liboo2corelinuxarm64.so",
                   "liboo2extlinuxarm64.so.9", "liboo2extlinuxarm64.so"]
                : ["liboo2corelinux64.so.9", "liboo2corelinux64.so",
                   "liboo2extlinux64.so.9", "liboo2extlinux64.so"];
        if (OperatingSystem.IsWindows())
            return ["oo2core_9_win64.dll", "oo2core_win64.dll"];
        return [];
    }

    /// <summary>
    /// The directories searched, in order: the documented drop location
    /// <c>fpkg-tools/native/</c> (a sibling of the <c>bin/</c> the CLI runs from), then
    /// <c>native/</c> and the binary's own directory, then the release folder's
    /// <c>native/</c> — which is where an in-repo checkout keeps it.
    /// </summary>
    public static IEnumerable<string> SearchDirectories()
    {
        string bin = AppContext.BaseDirectory;
        yield return Path.Combine(bin, "..", "native");
        yield return Path.Combine(bin, "native");
        yield return bin;

        string? release = FindReleaseFolder(bin);
        if (release is not null) yield return Path.Combine(release, "native");
    }

    /// <summary>
    /// The Oodle library's absolute path, or null when the user has not supplied one.
    /// A path given through <see cref="PathVariable"/> wins and is returned even if it does
    /// not exist, so a typo surfaces as a dlopen error naming the file rather than as a
    /// silent "no Oodle installed".
    /// </summary>
    public static string? Find()
    {
        string? overridden = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        foreach (string dir in SearchDirectories())
            foreach (string name in FileNames)
            {
                string candidate = Path.GetFullPath(Path.Combine(dir, name));
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Human-readable "where we looked", for the not-found message.</summary>
    public static string DescribeSearch() =>
        $"looked for {string.Join(" / ", FileNames)} in " +
        string.Join(", ", SearchDirectories().Select(d => Path.GetFullPath(d)));

    /// <summary>
    /// Walks up from the binary looking for the folder that holds LibProsperoPkg.dll.
    /// Depth must agree with LibraryResolver.FindReleaseFolder's walk.
    /// </summary>
    private static string? FindReleaseFolder(string start)
    {
        var dir = new DirectoryInfo(start);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LibProsperoPkg.dll")))
                return dir.FullName;
        return null;
    }
}
