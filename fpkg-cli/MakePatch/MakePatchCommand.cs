using System.Text.Json.Nodes;
using LibProsperoPkg.PKG;
using FpkgVirtualSource;

namespace Fpkg.Cli.MakePatch;

/// <summary>
/// `fpkg make-patch`: builds the target package of a patch from a base package plus a folder of
/// changed files, without extracting or recompressing the base.
///
/// <para>Unchanged files are staged as placeholders (executables and other raw-stored files are
/// copied straight out of the base's stored bytes, everything else is a sparse file of the right
/// size) and registered with <see cref="PatchPassthrough"/>, which makes the builder reuse the
/// base's stored data at its original offsets. Only changed files are compressed and written,
/// after the base data. Stage 1 of the patch work: this produces the full target image; the
/// LIH patch is derived from it and the base afterwards.</para>
/// </summary>
internal static class MakePatchCommand
{
    internal static int Run(string[] args, Func<string, Exception?, int> fail)
    {
        string? basePkg = null, changes = null, outDir = null, temp = null, version = null, lih = null;
        var deletes = new List<string>();
        var extra = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--base": basePkg = Next(); break;
                case "--changes": changes = Next(); break;
                case "--delete": deletes.Add(Next().Replace('\\', '/').Trim('/')); break;
                case "--out": outDir = Next(); break;
                case "--temp-dir": temp = Next(); break;
                case "--content-version": version = Next(); break;
                case "--lih": lih = Next(); break;
                default: extra.Add(args[i]); break;   // passed through to `fpkg build`
            }
        }
        if (basePkg is null || outDir is null || temp is null)
            return fail("usage: fpkg make-patch --base <base.pkg> [--changes <dir>] [--delete <path>]... " +
                        "--out <dir> --temp-dir <dir> [--content-version XX.YYY.ZZZ] [--lih <patch.pkg>] [build flags]", null);
        string passcode = new('0', 32);

        Console.WriteLine($"make-patch: reading base {basePkg}");
        var image = BaseImage.Open(basePkg, passcode);
        long imageOffset = PfsImageOffset(basePkg);
        Console.WriteLine($"  {image.Files.Count} files, data end {image.DataEnd:N0}, pfs_image.dat at {imageOffset}");

        var changed = changes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : Directory.EnumerateFiles(changes, "*", SearchOption.AllDirectories)
                       .ToDictionary(p => Path.GetRelativePath(changes, p).Replace('\\', '/'), p => p, StringComparer.Ordinal);
        foreach (var d in deletes)
            if (!image.Files.Any(f => f.Path == d || f.Path.StartsWith(d + "/", StringComparison.Ordinal)))
                return fail($"--delete {d}: no such file or directory in the base", null);
        bool Deleted(string p) => deletes.Any(d => p == d || p.StartsWith(d + "/", StringComparison.Ordinal));

        // Staging tree: CNT sce_sys files, then every inner file.
        string staging = Path.Combine(Path.GetFullPath(temp), "make-patch-src");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var rebuilt = ProsperoPackageArchive.ExtractRebuildSourceFiles(basePkg, staging, passcode);
        Console.WriteLine($"  staged {rebuilt.Count} sce_sys CNT file(s)");

        var entries = new Dictionary<string, PatchPassthrough.Entry>(StringComparer.Ordinal);
        long copied = 0;
        using (var src = File.OpenRead(basePkg))
        {
            foreach (var f in image.Files)
            {
                if (Deleted(f.Path) || changed.ContainsKey(f.Path)) continue;
                string dst = Path.Combine(staging, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write))
                {
                    if (f.ContiguousRaw)
                    {
                        // Real bytes: the builder probes headers (executables) and normalises SELFs.
                        src.Position = imageOffset + f.Blocks[0].StoredOffset;
                        CopyExactly(src, o, f.Size);
                        copied += f.Size;
                    }
                    else o.SetLength(f.Size);   // sparse placeholder; passthrough never reads it
                }
                var d = Descriptor.From(f, wholeBlockRaw: false);
                entries["/" + f.Path] = new PatchPassthrough.Entry(d.StoreRaw, d.OnDiskOffset, d.OnDiskSize,
                    f.Size, f.ContiguousRaw ? f.Blocks[0].StoredOffset : -1, d.Chunks, d.Integrity);
            }
        }
        foreach (var (rel, path) in changed)
        {
            string dst = Path.Combine(staging, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(path, dst, overwrite: true);
        }
        Console.WriteLine($"  staged {entries.Count} passthrough file(s) ({copied:N0} bytes copied raw), " +
                          $"{changed.Count} changed, {deletes.Count} deletion(s)");

        // A patch's own version: param.json and the inner sce_sys/pfs-version.dat (which is the
        // version as text). param.json gains origin/target versions and loses attributePub,
        // exactly as Sony's patch does; the base's param.json rides along as 8448/8449.
        string paramPath = Path.Combine(staging, "sce_sys", "param.json");
        byte[] baseParam = File.ReadAllBytes(paramPath);
        var param = JsonNode.Parse(baseParam)!.AsObject();
        string baseVersion = param["contentVersion"]!.GetValue<string>();
        version ??= BumpVersion(baseVersion);
        param["contentVersion"] = version;
        param["originContentVersion"] = baseVersion;
        param["targetContentVersion"] = baseVersion;
        param.Remove("attributePub");
        File.WriteAllText(paramPath, param.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (!changed.ContainsKey("sce_sys/pfs-version.dat"))
        {
            string pv = Path.Combine(staging, "sce_sys", "pfs-version.dat");
            File.WriteAllText(pv, version);
            entries.Remove("/sce_sys/pfs-version.dat");      // changed: written, not reused
        }
        Console.WriteLine($"  content version {baseVersion} -> {version}");

        // Compaction: base data blocks that no reused file touches are dropped and everything
        // after them moves down by whole blocks, so block contents stay identical (the patch
        // maps them back to the base) and no dead space is left in the image.
        long dataBlocks = (image.DataEnd + 0xFFFF) / 0x10000;
        var used = new bool[dataBlocks];
        foreach (var f in image.Files.Where(f => entries.ContainsKey("/" + f.Path)))
            foreach (var blk in f.Blocks)
                for (long k = blk.StoredOffset >> 16; k <= (blk.StoredOffset + Math.Max(blk.CompressedLength, 1) - 1) >> 16; k++)
                    used[k] = true;
        var liveBlocks = new List<long>();
        var newIndex = new long[dataBlocks];
        for (long k = 0; k < dataBlocks; k++)
        {
            newIndex[k] = used[k] ? liveBlocks.Count : -1;
            if (used[k]) liveBlocks.Add(k);
        }
        long Remap(long off) => newIndex[off >> 16] * 0x10000 + (off & 0xFFFF);
        foreach (var key in entries.Keys.ToList())
        {
            var e = entries[key];
            entries[key] = e with
            {
                OnDiskOffset = Remap(e.OnDiskOffset),
                RawOffset = e.RawOffset < 0 ? -1 : Remap(e.RawOffset),
                Chunks = e.Chunks.Select(c => c with { OnDiskOffset = Remap(c.OnDiskOffset) }).ToList(),
            };
        }
        Console.WriteLine($"  compaction: {dataBlocks - liveBlocks.Count:N0} of {dataBlocks:N0} base data block(s) dropped");

        string appDrm = AppDrmOf(paramPath);
        PatchPassthrough.Configure(basePkg, imageOffset, liveBlocks, entries);
        PatchExtras.Configure(baseParam, baseParam);
        try
        {
            string[] build =
            [
                "build", "--source", staging, "--out", outDir, "--temp-dir", temp,
                "--no-coalescing", "--no-relocation-align", "--no-playgo-fix", "--retain-sce-sys",
                "--no-media-repair", "--no-localised-icons", "--keep-right-sprx", "--encrypt-license-entries",
                "--app-drm", appDrm,
                .. extra,
            ];
            Console.WriteLine("  fpkg " + string.Join(' ', build.Skip(1)));
            int rc = Program.Build(build);
            if (rc != 0) return rc;
            var missing = PatchPassthrough.Unapplied.ToList();
            if (missing.Count > 0)
                return fail($"{missing.Count} passthrough file(s) never reached the builder's write loop, e.g. " +
                            string.Join(", ", missing.Take(5)), null);
            Console.WriteLine($"make-patch: {PatchPassthrough.AppliedCount} file(s) reused from the base");

            string target = Directory.GetFiles(outDir, "*.pkg").Single();
            var r = PatchFinalize.Finalize(target, basePkg, liveBlocks);
            Console.WriteLine($"make-patch: target {r.TargetBlocks:N0} blocks, {r.Reused:N0} from the base, " +
                              $"{r.NewBlocks:N0} new ({r.NewBlocks * 65536L:N0} bytes)");
            if (lih is not null)
            {
                PatchFinalize.WriteLih(target, r.Origin, lih);
                Console.WriteLine($"make-patch: wrote {lih} ({new System.IO.FileInfo(lih).Length:N0} bytes)");
            }
            return 0;
        }
        finally
        {
            PatchPassthrough.Reset();
            PatchExtras.Reset();
        }
    }

    /// <summary>"01.300.000" -> "01.300.001".</summary>
    private static string BumpVersion(string v)
    {
        int dot = v.LastIndexOf('.');
        string last = v[(dot + 1)..];
        return v[..(dot + 1)] + (int.Parse(last) + 1).ToString(new string('0', last.Length));
    }

    private static void CopyExactly(Stream src, Stream dst, long length)
    {
        var buf = new byte[1 << 20];
        while (length > 0)
        {
            int n = (int)Math.Min(length, buf.Length);
            src.ReadExactly(buf, 0, n);
            dst.Write(buf, 0, n);
            length -= n;
        }
    }

    private static string AppDrmOf(string paramJson)
    {
        var drm = JsonNode.Parse(File.ReadAllText(paramJson))?["applicationDrmType"]?.GetValue<string>();
        return string.IsNullOrEmpty(drm) ? "standard" : drm;
    }

    /// <summary>Byte offset of pfs_image.dat in the package; make-patch needs it contiguous.</summary>
    private static long PfsImageOffset(string packagePath)
    {
        using var session = InnerList.OpenSession(packagePath, new string('0', 32), CancellationToken.None);
        var desc = new InnerOffsets(packagePath, new string('0', 32), InnerList.PlanOf(session), CancellationToken.None);
        using (desc)
        {
            if (!desc.ImageContiguous)
                throw new InvalidDataException("the base's pfs_image.dat is not stored contiguously; make-patch cannot reuse it in place");
            return desc.ImageOffsetInPkg;
        }
    }
}
