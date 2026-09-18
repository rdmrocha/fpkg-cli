internal static class Oracle
{
    /// <summary>
    /// The 0.6.9 rebuild that the repair must reproduce byte for byte. Built by hand once —
    /// see Task 7 of the implementation plan — because a 630 MB rebuild is not a per-test cost.
    /// </summary>
    internal static string Path
    {
        get
        {
            if (!Directory.Exists(Dir)) return "";
            var candidates = Directory.EnumerateFiles(Dir, "*.pkg").ToList();
            return candidates.Count switch
            {
                0 => "",
                1 => candidates[0],
                // More than one *.pkg under .oracle/pkg/ means the acceptance gate could
                // silently measure against whichever file the filesystem happened to return
                // first — and this is the one artifact underwriting the whole acceptance
                // story. Ambiguity here must be loud, not resolved by directory order.
                _ => throw new InvalidOperationException(
                    $"Ambiguous acceptance oracle: found {candidates.Count} *.pkg files under " +
                    $"'{Dir}' ({string.Join(", ", candidates.Select(System.IO.Path.GetFileName))}). " +
                    "The acceptance oracle must be unambiguous — remove all but the intended rebuild.")
            };
        }
    }

    internal static bool Available => Path.Length > 0 && File.Exists(Path);

    private static string Dir => System.IO.Path.Combine(TestPackage.RepoRoot, ".oracle", "pkg");
}
