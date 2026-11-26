namespace Fpkg.Cli.MakePatch;

/// <summary>
/// `fpkg patch-plan BASE.pkg`: development check for make-patch. Reads the base the way
/// make-patch will and proves the reconstruction: every file's blocks are rebuilt into the
/// library's chunk descriptors and turned back into NAPS entries with the library's own
/// DeriveDataRegionBlocks, which must reproduce the base's spans field for field.
/// </summary>
internal static class PatchPlanCommand
{
    internal static int Run(string[] args, Func<string, Exception?, int> fail)
    {
        if (args.Length < 2) return fail("usage: fpkg patch-plan <base.pkg> [--passcode <32>]", null);
        string passcode = args.Length >= 4 && args[2] == "--passcode" ? args[3] : new string('0', 32);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var image = BaseImage.Open(args[1], passcode);
        Console.WriteLine($"base: {image.Files.Count} files, {image.Spans.Count} spans, data end {image.DataEnd:N0} " +
                          $"(block {image.DataEnd / 65536.0:F3}), read in {sw.Elapsed.TotalSeconds:F1}s");

        int ok = 0, bad = 0;
        foreach (var f in image.Files)
        {
            var d = Descriptor.From(f);
            string? err = d.CheckRoundTrip(f);
            if (err is null) ok++; else { bad++; if (bad <= 20) Console.WriteLine($"  MISMATCH {f.Path}: {err}"); }
        }
        foreach (var f in image.Files.Take(12).Concat(image.Files.Where(f => f.ContiguousRaw).Take(5)).Distinct())
        {
            var d = Descriptor.From(f);
            Console.WriteLine($"  {f.Path,-44} size {f.Size,14:N0} blocks {f.Blocks.Count,7} " +
                              $"{(d.StoreRaw ? (d.Chunks.Count == 0 ? "raw-contiguous" : "stored") : "kraken"),-14} " +
                              $"at {d.OnDiskOffset,14:N0} ondisk {d.OnDiskSize,14:N0}{(f.Executable ? " exec" : "")}");
        }
        Console.WriteLine($"round trip: {ok} ok, {bad} mismatched");
        return bad == 0 ? 0 : 1;
    }
}
