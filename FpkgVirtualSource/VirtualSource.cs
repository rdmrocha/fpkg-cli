namespace FpkgVirtualSource;

/// <summary>One entry of a virtual source tree.</summary>
public readonly record struct VirtualEntry(string RelativePath, bool IsDirectory, long Length);

/// <summary>
/// The surface the patched LibProsperoPkg IL calls. Everything here must stay static and
/// exception-safe: it runs deep inside someone else's call stack.
/// </summary>
public static class VirtualSource
{
    private const string Scheme = "ffpfsc:";

    public static bool IsVirtual(string? path) =>
        path is not null && path.StartsWith(Scheme, StringComparison.Ordinal);

    public static string Root(string handle) => $"{Scheme}{handle}:/";

    public static string PathFor(string handle, string relativePath) =>
        $"{Scheme}{handle}:/{relativePath.TrimStart('/')}";

    private static (Container Container, string Relative) Resolve(string path)
    {
        int sep = path.IndexOf(':', Scheme.Length);
        if (sep < 0) throw new InvalidOperationException("malformed virtual path: " + path);
        string handle = path[Scheme.Length..sep];
        return (ContainerRegistry.Get(handle), path[(sep + 1)..].TrimStart('/'));
    }

    public static Stream Open(string path)
    {
        var (container, relative) = Resolve(path);
        var entry = container.Files.FirstOrDefault(
                        f => string.Equals(f.RelativePath, relative, StringComparison.Ordinal))
                    ?? throw new FileNotFoundException("not present in the source container: " + relative, path);
        return container.Reader.OpenFile(entry);
    }

    public static Stream OpenOrFile(string path) =>
        IsVirtual(path)
            ? Open(path)
            // Not a plain FileStream: an executable that already carries the .sceversion record
            // the library is about to write has that record hidden here, so the library's append
            // lands on top of it instead of after it. See SceVersionTrailer. Identical to a
            // FileStream for every path that is not registered, which is all of them unless an
            // SDK override is set.
            : SceVersionTrailer.OpenTrimmed(path);

    /// <summary>Files and directories directly beneath <paramref name="virtualRoot"/>, recursively.</summary>
    public static IEnumerable<VirtualEntry> Enumerate(string virtualRoot)
    {
        var (container, relative) = Resolve(virtualRoot);
        string prefix = relative.Length == 0 ? "" : relative.TrimEnd('/') + "/";
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var f in container.Files)
        {
            if (prefix.Length > 0 && !f.RelativePath.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rel = f.RelativePath[prefix.Length..];
            if (rel.Length == 0) continue;

            for (int slash = rel.IndexOf('/'); slash >= 0; slash = rel.IndexOf('/', slash + 1))
            {
                string dir = rel[..slash];
                if (seenDirs.Add(dir)) yield return new VirtualEntry(dir, IsDirectory: true, Length: 0);
            }
            yield return new VirtualEntry(rel, IsDirectory: false, f.Length);
        }
    }
}
