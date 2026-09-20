using System.Collections;
using System.Reflection;

namespace FpkgVirtualSource;

/// <summary>
/// Site 15. Moves the staged PlayGo language fillers to the end of the AFID candidate list, which
/// is what decides where files are placed in the inner image.
///
/// <para><b>Why.</b> Sony's packer orders placement by chunk: measured on the regenerated
/// the reference fixture oracle, its physical order is <c>sce_sys/pfs-version.dat</c>, <c>sce_sys/keystone</c>,
/// then the GP5 manifest order stable-sorted by chunk id. Only the fillers carry a non-zero chunk,
/// so they land last and the final language extent simply runs to the end of the mount image — 32
/// extents. LibProsperoPkg orders by <c>SystemAfidRank</c> then path, so <c>playgo-languages/</c>
/// falls in its alphabetical place, before <c>sce_module/</c>, leaving real files after the fillers
/// and forcing a 33rd chunk-0 extent.</para>
///
/// <para><b>Why here and not through PublisherAfidAssignments.</b> That option exists and placement
/// does follow the AFID, but the map must be COMPLETE — the library rejects a partial one with
/// "contains N paths but the inner image contains M files" — which would mean reproducing the
/// library's own candidate list exactly, including which sce_sys files it has already promoted to
/// CNT entries. Reordering the list the library itself just built needs none of that.</para>
///
/// <para><b>Reflection, deliberately.</b> <c>FileNode</c> is a LibProsperoPkg type. A shim that
/// named it would make the patched library depend on this assembly depend on the library, and that
/// cycle fails at run time with "only single file assemblies are supported" — measured when site 13
/// first took a library type across the boundary. The list arrives as a non-generic
/// <see cref="IList"/> and <c>FullPath</c> is read by reflection, once per file.</para>
///
/// <para>Inert unless fillers were staged: with an empty allow-list nothing matches and the order
/// is returned untouched.</para>
/// </summary>
public static class PlayGoFillerOrder
{
    /// <summary>Where the notice goes — this changes the physical layout, so it is said out loud.</summary>
    public static Action<string>? Log { get; set; } = Console.Out.WriteLine;

    private static Func<object, string?>? _fullPath;

    /// <summary>
    /// Stable-partitions <paramref name="files"/> in place: everything that is not a registered
    /// filler keeps its order and comes first, the fillers keep their order and come last.
    /// </summary>
    public static void MoveFillersLast(IList files)
    {
        if (files is null || files.Count == 0) return;
        SystemFilesFirst(files);
        if (PlayGoFillerDedup.Count == 0) return;

        var keep = new List<object?>(files.Count);
        var fillers = new List<object?>();
        foreach (object? file in files)
        {
            if (file is null) { keep.Add(file); continue; }
            (PlayGoFillerDedup.IsFiller(FullPathOf(file)) ? fillers : keep).Add(file);
        }
        if (fillers.Count == 0) return;

        int at = 0;
        foreach (object? file in keep) files[at++] = file;
        foreach (object? file in fillers) files[at++] = file;

        Log?.Invoke($"playgo: {fillers.Count} language filler(s) moved to the end of the inner " +
                    "image, so the last language extent reaches the end of the mount image");
    }

    /// <summary>
    /// The two generated <c>sce_sys</c> files, in the order the Windows toolkit places them:
    /// <c>pfs-version.dat</c> then <c>keystone</c>. The library's <c>SystemAfidRank</c> ranks
    /// keystone 0 and pfs-version.dat 3, so it emits them the other way round. Measured on the
    /// regenerated the reference fixture oracle, where they are the first two entries of both the placement
    /// order and the <c>fstr</c> table.
    ///
    /// <para>Applied by moving those two to the head in that order and leaving every other entry
    /// where it is, so a source that ships only one of them, or neither, is untouched.</para>
    /// </summary>
    private static void SystemFilesFirst(IList files)
    {
        int Find(string path)
        {
            for (int i = 0; i < files.Count; i++)
                if (files[i] is { } f && string.Equals(FullPathOf(f), path, StringComparison.Ordinal))
                    return i;
            return -1;
        }
        int version = Find("/sce_sys/pfs-version.dat"), keystone = Find("/sce_sys/keystone");
        if (version < 0 || keystone < 0 || (version == 0 && keystone == 1)) return;

        object? a = files[version], b = files[keystone];
        var rest = new List<object?>(files.Count);
        for (int i = 0; i < files.Count; i++)
            if (i != version && i != keystone) rest.Add(files[i]);

        files[0] = a; files[1] = b;
        for (int i = 0; i < rest.Count; i++) files[i + 2] = rest[i];
        Log?.Invoke("playgo: sce_sys/pfs-version.dat and sce_sys/keystone placed first, in the " +
                    "order the Windows toolkit uses");
    }

    /// <summary>
    /// <c>FileNode.FullPath</c>, resolved once and cached. It is a FIELD on the current release;
    /// both forms are accepted so a future change to a property still works. An unreadable path
    /// throws rather than returning null: a null would make every file look like a non-filler and
    /// leave the layout silently unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type has no readable FullPath.</exception>
    private static string? FullPathOf(object file)
    {
        _fullPath ??= Accessor(file.GetType());
        return _fullPath(file);
    }

    private static Func<object, string?> Accessor(Type type)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        if (type.GetField("FullPath", Any) is { } field && field.FieldType == typeof(string))
            return o => field.GetValue(o) as string;
        if (type.GetProperty("FullPath", Any) is { CanRead: true } property &&
            property.PropertyType == typeof(string))
            return o => property.GetValue(o) as string;
        throw new InvalidOperationException(
            $"{type.FullName} has no readable string FullPath, so the PlayGo language fillers " +
            "cannot be identified and site 15 would silently leave the layout unchanged. " +
            "Re-derive PlayGoFillerOrder against the current LibProsperoPkg.");
    }
}
