using System.Reflection;

namespace FpkgVirtualSource;

/// <summary>
/// Builds the FSDir/FSFile tree for a virtual source. Called from patched Populate.
///
/// Reflection is used deliberately: this assembly must not hold a compile-time reference to
/// LibProsperoPkg's FSDir/FSFile, or the shim would pin one library version and break on the
/// next release. The reflection cost is paid once per directory, not per byte.
/// </summary>
public static class TreeOverlay
{
    /// <summary>The marker file the CLI drops in the staging root.</summary>
    public const string MarkerFileName = ".fpkg-virtual-source";

    /// <summary>
    /// Fills <paramref name="node"/> from the container when <paramref name="path"/> is a staging
    /// root carrying the marker. Returns false when it is an ordinary directory, in which case the
    /// caller does its normal filesystem walk.
    /// </summary>
    public static bool Populate(object node, string path)
    {
        string marker = Path.Combine(path, MarkerFileName);
        if (!File.Exists(marker)) return false;

        string handle = File.ReadAllText(marker).Trim();
        var container = ContainerRegistry.Get(handle);
        string appRoot = container.FindAppRoot()
            ?? throw new InvalidOperationException(
                "the source container has no single app root (a directory holding sce_sys/param.json)");

        var asm = node.GetType().Assembly;
        Type dirType = asm.GetType("LibProsperoPkg.PFS.FSDir", throwOnError: true)!;
        Type fileType = asm.GetType("LibProsperoPkg.PFS.FSFile", throwOnError: true)!;
        var fileCtor = fileType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(string), typeof(long)])
            ?? throw new InvalidOperationException("FSFile(string, long) not found");

        var dirs = new Dictionary<string, object>(StringComparer.Ordinal) { [""] = node };
        string prefix = appRoot.Length == 0 ? "" : appRoot + "/";

        foreach (var entry in VirtualSource.Enumerate(VirtualSource.Root(handle))
                                           .OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            if (prefix.Length > 0 && !entry.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            string rel = entry.RelativePath[prefix.Length..];
            if (rel.Length == 0) continue;

            // sce_sys is staged on disk, not overlaid virtually: the staged copy is
            // authoritative and is added below by walking the real filesystem, exactly as the
            // library's own folder walk would. (Previously this comment claimed "the caller's
            // own walk already added it" -- false: Populate returning true makes the patched
            // prologue `if (TreeOverlay.Populate(node, path)) return;` skip the library's own
            // walk of this directory entirely, so nothing else ever added it. That silently
            // dropped every sce_sys file that isn't part of the library's own recognised
            // CNT-entry set -- keystone, pfs-version.dat, and any sce_sys/about/*.sprx included
            // -- from the inner PFS image. Caught by
            // FpkgVirtualSource.Tests/AcceptanceTests.cs.)
            if (rel == "sce_sys" || rel.StartsWith("sce_sys/", StringComparison.Ordinal)) continue;

            int slash = rel.LastIndexOf('/');
            string parentPath = slash < 0 ? "" : rel[..slash];
            string name = slash < 0 ? rel : rel[(slash + 1)..];
            object parent = dirs[parentPath];

            if (entry.IsDirectory)
            {
                object child = Activator.CreateInstance(dirType)!;
                Set(child, "name", name);
                Set(child, "Parent", parent);
                Add(parent, "Dirs", child);
                dirs[rel] = child;
            }
            else
            {
                object file = fileCtor.Invoke([VirtualSource.PathFor(handle, entry.RelativePath), entry.Length]);
                Set(file, "name", name);
                Set(file, "Parent", parent);
                Add(parent, "Files", file);
            }
        }

        // sce_sys is staged to real disk by the caller (ContainerSource.Open stages the
        // container's app-root sce_sys/** into <path>/sce_sys/ before Populate ever runs), so
        // it is added here as an ORDINARY, file-backed subtree -- real absolute paths, not
        // "ffpfsc:" virtual ones -- exactly what the library's own folder walk would produce for
        // a real sce_sys directory. This is what makes DrmTypePatch's in-place param.json
        // rewrite and every other sce_sys-reading build stage work unmodified for a container
        // source, and it is what puts sce_sys's own inner-PFS content (keystone,
        // pfs-version.dat, about/*.sprx -- whatever isn't pulled out into a CNT entry) back into
        // the tree that folder builds have always populated it from.
        string sceSysPath = Path.Combine(path, "sce_sys");
        if (Directory.Exists(sceSysPath))
        {
            object sceSysDir = Activator.CreateInstance(dirType)!;
            Set(sceSysDir, "name", "sce_sys");
            Set(sceSysDir, "Parent", node);
            Add(node, "Dirs", sceSysDir);

            // Mirrors LibProsperoPkg's own rule verbatim (decompiled from
            // ProsperoPkgBuilder.<BuildInnerTree>g__Populate, folder-source path,
            // skipVirtualDecryptedRoot: true):
            //   if (skipVirtualDecryptedRoot && name.Equals("ext_info.dat", OrdinalIgnoreCase))
            //   {
            //       FSDir parent = node.Parent;
            //       if (parent != null && parent.Parent == null &&
            //           string.Equals(node.name, "sce_sys", OrdinalIgnoreCase))
            //           continue;
            //   }
            // i.e.: skip a file named ext_info.dat only when its containing directory is named
            // sce_sys AND that directory is a direct child of the true tree root -- not any
            // ext_info.dat anywhere else. `node` here is that root (the argument Populate was
            // called with), so `sceSysDir` (just created, parented to `node`) is a direct child
            // of root exactly when `node` itself has no parent.
            bool sceSysIsDirectRootChild = Get(node, "Parent") is null;
            AddRealTree(sceSysDir, sceSysPath, dirType, fileCtor,
                        skipExtInfoDat: sceSysIsDirectRootChild);
        }
        return true;
    }

    /// <summary>
    /// Recursively adds the real, on-disk contents of <paramref name="dirPath"/> under
    /// <paramref name="parentNode"/> as ordinary file-backed FSDir/FSFile entries (real absolute
    /// paths). This is the same shape the library's own folder walk produces; the only reason it
    /// is done by hand here is that <c>Populate</c> returning true (see the call site) skips that
    /// walk entirely for the staging root.
    ///
    /// <paramref name="skipExtInfoDat"/> reproduces the one exclusion LibProsperoPkg's own walk
    /// applies at this level (see the call site's comment) -- true only for the direct children
    /// of a root-level <c>sce_sys</c>, false everywhere else (including this method's own
    /// recursive calls into subdirectories), so an <c>ext_info.dat</c> nested deeper is kept, not
    /// skipped.
    ///
    /// There is a second exclusion in the same decompiled method -- a directory literally named
    /// "decrypted" directly under the true tree root is skipped entirely when
    /// skipVirtualDecryptedRoot is true. It is deliberately not reproduced here: it can only ever
    /// fire while populating the true root's own children (the check is against the *populating*
    /// node's parent, not the candidate directory's), and this method is only ever called
    /// starting at a root's <c>sce_sys</c> subdirectory, never at the true root itself -- so
    /// `parentNode` here always already has a non-null Parent, and the condition can never be
    /// true. The container-overlay loop above (which does populate the true root) never touches
    /// <see cref="DirectoryInfo"/> at all -- it is driven entirely by <see cref="VirtualSource"/>
    /// entries synthesised from the container's own listing, never a real filesystem walk -- so
    /// it cannot produce the real <c>DirectoryInfo</c> the original check requires either. There
    /// is currently no code path in this file where a root-level "decrypted" directory could be
    /// added.
    /// </summary>
    private static void AddRealTree(object parentNode, string dirPath, Type dirType, ConstructorInfo fileCtor,
                                     bool skipExtInfoDat = false)
    {
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(dirPath)
                                            .OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(entryPath);
            if (Directory.Exists(entryPath))
            {
                object childDir = Activator.CreateInstance(dirType)!;
                Set(childDir, "name", name);
                Set(childDir, "Parent", parentNode);
                Add(parentNode, "Dirs", childDir);
                AddRealTree(childDir, entryPath, dirType, fileCtor, skipExtInfoDat: false);
            }
            else
            {
                if (skipExtInfoDat && name.Equals("ext_info.dat", StringComparison.OrdinalIgnoreCase))
                    continue;

                object file = fileCtor.Invoke([entryPath, new FileInfo(entryPath).Length]);
                Set(file, "name", name);
                Set(file, "Parent", parentNode);
                Add(parentNode, "Files", file);
            }
        }
    }

    // Every lookup below throws instead of silently no-opping or letting a bare NRE surface:
    // a wrong field/property name here means LibProsperoPkg's shape changed, and that must
    // fail loudly and diagnosably, the same as every dnlib shape assertion in the patcher.
    private static void Set(object target, string member, object? value)
    {
        var type = target.GetType();
        var field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null) { field.SetValue(target, value); return; }
        var property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is not null) { property.SetValue(target, value); return; }
        throw new InvalidOperationException($"{type.FullName} has no field or property named '{member}'");
    }

    private static object? Get(object target, string member)
    {
        var type = target.GetType();
        var field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null) return field.GetValue(target);
        var property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is not null) return property.GetValue(target);
        throw new InvalidOperationException($"{type.FullName} has no field or property named '{member}'");
    }

    private static void Add(object target, string listMember, object item)
    {
        var type = target.GetType();
        var field = type.GetField(listMember, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{type.FullName} has no field named '{listMember}'");
        object list = field.GetValue(target)
            ?? throw new InvalidOperationException($"{type.FullName}.{listMember} is null");
        var add = list.GetType().GetMethod("Add")
            ?? throw new InvalidOperationException($"{list.GetType().FullName} has no Add method");
        add.Invoke(list, [item]);
    }
}
