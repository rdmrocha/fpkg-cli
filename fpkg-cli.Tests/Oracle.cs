internal static class Oracle
{
    /// <summary>
    /// The 0.6.9 rebuild that the repair must reproduce byte for byte. Built by hand once —
    /// see Task 7 of the implementation plan — because a 630 MB rebuild is not a per-test cost.
    /// </summary>
    internal static string Path { get; } =
        Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "*.pkg").FirstOrDefault() ?? "" : "";

    internal static bool Available => Path.Length > 0 && File.Exists(Path);

    private static string Dir => System.IO.Path.Combine(TestPackage.RepoRoot, ".oracle", "pkg");
}
