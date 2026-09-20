using LibProsperoPkg.PKG;
using LibProsperoPkg.PlayGo;

namespace Fpkg.Cli.PlayGo;

/// <summary>
/// <c>fpkg playgo-fix &lt;file.pkg&gt;</c> — replace a built package's PlayGo metadata with metadata
/// derived from its own measured layout, and reseal. <c>--inspect</c> stops after the measurement
/// and prints it.
/// </summary>
internal static class PlayGoFixCommand
{
    internal static int Run(string[] args, Func<string, Exception?, int> fail,
                            Func<string[], Dictionary<string, string?>> parseFlags)
    {
        if (args.Length < 2) return fail("playgo-fix needs a package path", null);
        var flags = parseFlags(args[1..]);
        string path = Path.GetFullPath(args[1]);
        if (!File.Exists(path)) return fail($"no such file: {path}", null);
        string passcode = flags.GetValueOrDefault("passcode") ?? new string('0', 32);
        bool inspect = flags.ContainsKey("inspect");
        string? outPath = flags.GetValueOrDefault("out");
        string defaultLanguage = flags.GetValueOrDefault("default-language") ?? "en-US";
        bool quiet = flags.ContainsKey("quiet");
        void Log(string line) { if (!quiet) Console.WriteLine($"  {line}"); }

        var regions = PackageRegions.Load(path);
        long mountImageSize = regions.CntOffset;
        Console.WriteLine($"mount image: [0, {mountImageSize:N0}) — the space the extent table tiles");

        var table = CntEntryTable.Parse(regions.Cnt, passcode);
        string contentId = CntHeader.ReadContentId(regions.Cnt);
        byte[] originalChunkDat = table[PlayGoIds.ChunkDat].Payload;

        // ---- gate 1: the plgx writer reproduces this package's own playgo-chunk.dat ------------
        if (Plgx.RoundTrips(originalChunkDat) is string plgxProblem)
            return fail("the plgx writer does not round-trip this package's playgo-chunk.dat, so " +
                        "nothing it emits can be trusted: " + plgxProblem, null);
        Console.WriteLine($"ok: plgx round-trip — re-emitting playgo-chunk.dat reproduces all " +
                          $"{originalChunkDat.Length:N0} bytes");

        // ---- gate 2: resealing the UNTOUCHED package reproduces it byte for byte ---------------
        // Mandatory pre-flight. The library's verifier recomputes the chain exactly as the resealer
        // does, so it agrees with whatever is stored and cannot catch a package whose digests
        // already disagree with its bytes; only this can.
        if (CntReseal.FixedPointProblem(regions.Cnt, table.Physical, contentId, passcode) is string sealProblem)
            return fail("reseal pre-flight failed. " + sealProblem, null);
        Console.WriteLine($"ok: reseal fixed point — the untouched CNT reseals to its own " +
                          $"{regions.Cnt.Length:N0} bytes");

        // ---- measure --------------------------------------------------------------------------
        var progress = new Progress(Log);
        progress.Stage("measuring the inner files");
        var placed = new List<PlayGoRebuild.Placed>();
        foreach (var e in InnerList.Enumerate(path, passcode, sha256: false, offsets: true))
            placed.Add(new PlayGoRebuild.Placed(e.Path, PlayGoRebuild.ChunkOf(e.Path),
                                                e.PkgSpanStart, e.PkgSpanEnd, e.SpanNote, e.Size));
            progress.Report(placed.Count, 0);
        progress.Finish();
        Console.WriteLine($"measured {placed.Count} inner file(s)");

        // A language filler that shares its footprint with another file cannot own an extent: the
        // extent would contain that other file's bytes, which is the defect being fixed, only moved.
        var shared = placed.Where(f => f.Chunk > 0)
            .Where(f => placed.Any(o => o.Path != f.Path && o.Start < f.End && o.End > f.Start))
            .ToList();
        if (shared.Count > 0 && !flags.ContainsKey("allow-shared-fillers"))
            return fail(
                $"{shared.Count} language filler(s) share their package footprint with another " +
                $"file — e.g. {shared[0].Path} at [{shared[0].Start:N0}, {shared[0].End:N0}). " +
                "This library's NAPS content-de-duplicates identical spans onto one stored copy, so " +
                "all-zero fillers own no package bytes of their own and no honest extent can be " +
                "derived for them. Rebuild with distinct filler payloads (fpkg build " +
                "--playgo-fix, which stages them) before running this.", null);

        var layout = PlayGoRebuild.Derive(placed, mountImageSize);
        Console.WriteLine(PlayGoRebuild.Describe(layout));
        Console.WriteLine("FICM histogram: " + PlayGoRebuild.Histogram(layout.Files));

        int runs = layout.ExtentChunk.Count(c => c != 0);
        int distinctLanguageRuns = layout.ExtentChunk.Where(c => c != 0).Distinct().Count();
        Console.WriteLine(runs == distinctLanguageRuns
            ? $"ok: every language chunk is contiguous ({distinctLanguageRuns} chunk(s), {runs} run(s))"
            : $"WARNING: {runs} run(s) across {distinctLanguageRuns} language chunk(s) — a chunk is fragmented");

        if (inspect) return 0;

        // ---- rebuild the three entries ---------------------------------------------------------
        var paths = layout.Files.Select(f => f.Path).ToList();
        byte[] hashTable = ProsperoPlayGo.BuildHashTable(paths);
        // The FICM is indexed by HASH-TABLE SLOT, not by file order -- measured against the
        // oracle, which this reproduces byte-for-byte and which no file ordering does. See
        // PlayGoFicmOrder.
        var slotOrder = PlayGoFicmOrder.SlotOrder(paths);
        byte[] ficm = ProsperoPlayGo.BuildFicm(
            [.. slotOrder.Select(i => (byte)layout.Files[i].Chunk)]);
        byte[] chunkDat = PlayGoRebuild.BuildChunkDat(originalChunkDat, layout, defaultLanguage);
        // NOT the library's copy. CNT entry 12288 is 3,248 bytes in both Windows-built oracles and
        // byte-identical between the two titles; the library emits 2,293. See PlayGoScenario.
        byte[] scenarioJson = PlayGoScenario.Build(
            scenarioCount: 1, defaultScenarioId: 0, defaultLanguage: "en-US",
            languages: PlayGoScenario.AllLanguages());

        Log($"hash table {hashTable.Length:N0} B (was {table[PlayGoIds.HashTable].Payload.Length:N0}), " +
            $"ficm {ficm.Length:N0} B (was {table[PlayGoIds.Ficm].Payload.Length:N0}), " +
            $"chunk.dat {chunkDat.Length:N0} B (was {originalChunkDat.Length:N0})");

        var info = ProsperoPlayGo.ValidateLayout(chunkDat, ficm, hashTable, scenarioJson,
                                                 contentId, (ulong)mountImageSize);
        Console.WriteLine($"ok: ValidateLayout — {info.ChunkCount} chunks, {info.ScenarioCount} scenario(s), " +
                          $"{info.ExtentCount} extents, {info.FileCount} file mappings, " +
                          $"{info.CoveredBytes:N0} bytes covered");

        table[PlayGoIds.ChunkDat].Payload = chunkDat;
        table[PlayGoIds.HashTable].Payload = hashTable;
        table[PlayGoIds.Ficm].Payload = ficm;
        // The SI member and CNT entry 12288 must be the same document, so the entry is rewritten
        // here too -- without this the package carries the library's 2,293-byte scenario in the
        // CNT and the oracle's 3,248-byte one in the SI.
        table[PlayGoIds.ScenarioJson].Payload = scenarioJson;

        byte[] newCnt = CntReseal.Seal(regions.Cnt, table.Physical, contentId, passcode,
                                       Announce(progress, "resealing the CNT digest chain"));

        string staged = (outPath is null ? path : Path.GetFullPath(outPath)) + ".playgo-fix.tmp";
        regions.WriteTo(staged, newCnt, [], flushToDisk: false);
        byte[] napsMeta300 = PlayGoRebuild.BuildNapsMeta300(layout);
        byte[] si;
        using (var mount = File.OpenRead(staged))
            si = PlayGoRebuild.RebuildSi(regions.Si, contentId, chunkDat, napsMeta300, scenarioJson,
                                         mount, regions.CntOffset + newCnt.Length, regions.CntOffset, Log);
        regions.OverwriteSi(staged, newCnt.Length, si);

        string final = outPath is null ? path : Path.GetFullPath(outPath);
        File.Move(staged, final, overwrite: true);
        Console.WriteLine($"wrote {final}");
        Console.WriteLine($"naps_meta_3xx: {napsMeta300.Length / 24} record(s) " +
                          $"(= 1 + {layout.LanguageChunks} language chunk(s) + 1), SI members rebuilt with " +
                          $"config/{contentId}/playgo-scenario.json");
        return 0;
    }

    internal static class PlayGoIds
    {
        internal const uint ChunkDat = 4097;
        internal const uint HashTable = 8208;
        internal const uint Ficm = 8209;
        internal const uint ScenarioJson = 12288;
    }

    /// <summary>Names the stage a shared <see cref="Progress"/> is about to report on.</summary>
    private static Progress Announce(Progress progress, string stage)
    {
        progress.Stage(stage);
        return progress;
    }
}
