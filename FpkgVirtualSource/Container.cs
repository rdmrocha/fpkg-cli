using System.Buffers.Binary;
using LibProsperoPkg.PFS;
using LibProsperoPkg.Util;
using StreamReader = LibProsperoPkg.Util.StreamReader;

namespace FpkgVirtualSource;

/// <summary>
/// One open source container. Layers, outermost first: the file, an optional PFS wrapper,
/// an optional PFSC decompressor, then the exFAT volume. All reads of the innermost source
/// go through <see cref="Gate"/> because PFSCReader holds one decode buffer.
/// </summary>
public sealed class Container : IDisposable
{
    private const long PfsVersion2 = 2;
    private const long PfsMagic = 0x1332A0B;

    private readonly List<IDisposable> _own = [];
    private bool _disposed;

    private Container(ExfatReader reader, string path)
    {
        Reader = reader;
        ContainerPath = path;
        Files = reader.EnumerateFiles().ToList();
    }

    /// <summary>Named to avoid shadowing System.IO.Path inside this class.</summary>
    public string ContainerPath { get; }
    public ExfatReader Reader { get; }

    /// <summary>
    /// The reader's own lock, not a second one: PFSCReader holds a single non-reentrant
    /// decode buffer, so there must be exactly one lock guarding every read down to it.
    /// </summary>
    public object Gate => Reader.Gate;
    public IReadOnlyList<ExfatEntry> Files { get; }

    /// <summary>Opens a .ffpfsc, a raw PFS image, or a bare .exfat, detected by header.</summary>
    public static Container Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                1 << 20, FileOptions.RandomAccess);
        var own = new List<IDisposable> { fs };
        try
        {
            IMemoryReader source = new StreamReader(fs);
            if (LooksLikePfs(fs))
            {
                var pfs = new PfsReader(source);
                var inner = pfs.GetAllFiles().OrderByDescending(f => f.size).FirstOrDefault()
                    ?? throw new ExfatException($"{path}: PFS container holds no files");
                IMemoryReader view = inner.GetView();
                if ((inner.flags & InodeFlags.compressed) != 0)
                {
                    var pfsc = new PFSCReader(view);
                    own.Add(pfsc);
                    view = pfsc;
                }
                source = view;
            }
            var container = new Container(new ExfatReader(source), path);
            container._own.AddRange(own);
            return container;
        }
        catch
        {
            for (int i = own.Count - 1; i >= 0; i--) own[i].Dispose();
            throw;
        }
    }

    private static bool LooksLikePfs(FileStream fs)
    {
        Span<byte> head = stackalloc byte[16];
        long saved = fs.Position;
        try
        {
            fs.Position = 0;
            if (fs.Read(head) != head.Length) return false;
            return BinaryPrimitives.ReadInt64LittleEndian(head) == PfsVersion2
                && BinaryPrimitives.ReadInt64LittleEndian(head[8..]) == PfsMagic;
        }
        finally { fs.Position = saved; }
    }

    /// <summary>
    /// The directory holding sce_sys/param.json, as a POSIX relative path ("" for the volume
    /// root). Null when there is no app root, or when there is more than one.
    /// </summary>
    public string? FindAppRoot()
    {
        var roots = Files
            .Select(f => AppRootFor(f.RelativePath))
            .Where(root => root is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roots.Count == 1 ? roots[0] : null;
    }

    private const string ParamJsonSuffix = "sce_sys/param.json";

    /// <summary>
    /// The candidate app-root directory for one file's relative path, or null when the path
    /// is not a sce_sys/param.json. The suffix only counts on a path-segment boundary --
    /// preceded by '/', or the path being exactly the suffix (the app root is the volume
    /// root) -- so a file like "Foosce_sys/param.json" (a directory named "Foosce_sys", not
    /// an app root named "Foo") does not spuriously match.
    /// </summary>
    private static string? AppRootFor(string relativePath)
    {
        if (relativePath.Length == ParamJsonSuffix.Length)
            return string.Equals(relativePath, ParamJsonSuffix, StringComparison.OrdinalIgnoreCase)
                ? ""
                : null;

        int boundary = relativePath.Length - ParamJsonSuffix.Length - 1;
        if (boundary < 0 || relativePath[boundary] != '/') return null;
        return relativePath.EndsWith(ParamJsonSuffix, StringComparison.OrdinalIgnoreCase)
            ? relativePath[..boundary]
            : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = _own.Count - 1; i >= 0; i--) _own[i].Dispose();
    }
}
