namespace Fpkg.Cli;

/// <summary>
/// The paths given to <c>fpkg extract</c> after the output directory. Each names a file or a
/// folder by its path in the GAME tree — <c>eboot.bin</c>, <c>sce_module</c>, <c>sce_sys</c> —
/// without the caller having to know whether it lives in the inner PFS or in the CNT entries.
///
/// <para>
/// That works because both of the library's filtered extractors hand their predicate the same
/// relative form: <c>uroot/</c> stripped from inner files, CNT records mapped to their canonical
/// <c>sce_sys/</c> path. One predicate therefore serves both, and
/// <c>ProsperoPackageArchive.ListExtractionFiles</c> lists exactly the paths they will offer.
/// </para>
/// </summary>
internal sealed class ExtractSelection
{
    private readonly string[] selectors;

    internal ExtractSelection(IEnumerable<string> paths)
    {
        selectors = paths.Select(p => Normalize(p)
                ?? throw new ArgumentException(
                    $"'{p}' selects the whole package; leave the paths out to extract everything"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal IReadOnlyList<string> Selectors => selectors;

    /// <summary>
    /// True when <paramref name="path"/> is a selector or sits under one. Case-insensitive,
    /// because the paths are typed by hand and the extraction lands on a case-insensitive
    /// filesystem anyway. A selector matches whole path segments only: <c>sce_module</c> does not
    /// take <c>sce_modules/</c>.
    /// </summary>
    internal bool Accepts(string path) => selectors.Any(s => Matches(path, s));

    /// <summary>The selectors that none of <paramref name="available"/> satisfies.</summary>
    internal IReadOnlyList<string> Unmatched(IEnumerable<string> available)
    {
        var paths = available as ICollection<string> ?? available.ToArray();
        return selectors.Where(s => !paths.Any(p => Matches(p, s))).ToArray();
    }

    private static bool Matches(string path, string selector) =>
        path.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
        (path.Length > selector.Length && path[selector.Length] == '/' &&
         path.StartsWith(selector, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Brings a typed path to the library's form: forward slashes, no leading <c>/</c> or
    /// <c>./</c>, no trailing slash, no <c>uroot/</c>. Null when nothing is left, which would mean
    /// "everything".
    /// </summary>
    internal static string? Normalize(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != ".")
            .ToList();
        if (parts.Contains("..")) throw new ArgumentException($"'{path}' climbs out of the package");
        if (parts.Count > 0 && parts[0].Equals("uroot", StringComparison.Ordinal)) parts.RemoveAt(0);
        return parts.Count == 0 ? null : string.Join('/', parts);
    }
}
