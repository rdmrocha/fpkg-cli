using System.Buffers.Binary;
using System.Text;
using LibProsperoPkg.PKG;
using LibProsperoPkg.PlayGo;

namespace Fpkg.Cli.PlayGo;

/// <summary>
/// Derives the PlayGo metadata of a BUILT package from the layout that package actually has, and
/// writes it back.
///
/// <para>
/// The defect this exists for (spec §1) is that <c>BuildLanguageChunkLayout</c> computes extent
/// sizes arithmetically while <c>BuildFicm</c> puts every file in chunk 0, and nothing reconciles
/// the two — so live payload ends up inside extents belonging to chunks the FICM declares empty.
/// The rule implemented here is spec §5: lay the chunks out contiguously, then RUN-LENGTH COALESCE
/// the extents from where the files measurably are. No size is ever computed from a formula.
/// </para>
/// </summary>
internal static class PlayGoRebuild
{
    private const uint ChunkDatId = 4097;
    private const uint HashTableId = 8208;
    private const uint FicmId = 8209;
    private const uint ScenarioJsonId = 12288;

    /// <summary>The header mask of the 31 defined language ids — PROVEN, spec §5.</summary>
    internal const ulong DefinedLanguageMask = 0xFFFFFFFE00000000;

    /// <summary>
    /// The PFS block, and the alignment every extent offset and length carries. PROVEN:
    /// <c>FUN_180231a80</c> sets it unconditionally and the extent coalescer <c>FUN_1801f6900</c>
    /// is called with it at <c>0x1801c59e5</c>. Also the <c>naps_meta</c> shift.
    /// </summary>
    internal const long BlockSize = 0x10000;

    private const long NapsMetaShift = BlockSize;
    private const ulong NapsMetaTerminalId = 0x3E9;
    private const ulong NapsMetaTerminalLength = 0x20000;

    /// <param name="Path">The inner path, as the FICM and the hash table spell it.</param>
    /// <param name="Chunk">0 for everything that is not a language filler — ABSENT MEANS 0, PROVEN.</param>
    /// <param name="Start">First package byte involved in the file; -1 when it has no footprint.</param>
    internal readonly record struct Placed(string Path, int Chunk, long Start, long End, string? Note,
                                          long Size);

    internal sealed record Layout(
        IReadOnlyList<Placed> Files,
        IReadOnlyList<Plgx.Extent> Extents,
        IReadOnlyList<int> ExtentChunk,
        int LanguageChunks);

    /// <summary>
    /// Assigns a chunk from the path alone. <c>playgo-languages/NN-*</c> is chunk NN; everything
    /// else, including anything unrecognised, is chunk 0.
    /// </summary>
    internal static int ChunkOf(string path)
    {
        const string prefix = PlayGoFillers.Directory + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return 0;
        string name = path[prefix.Length..];
        if (name.Length < 2 || !char.IsAsciiDigit(name[0]) || !char.IsAsciiDigit(name[1])) return 0;
        return (name[0] - '0') * 10 + (name[1] - '0');
    }

    /// <summary>
    /// Walks the files in LAYOUT order and opens a new extent whenever the chunk changes — the
    /// coalescer of spec §2, not a size formula.
    ///
    /// <para>
    /// Every boundary is EXACTLY the start of the later run's first footprint. Nothing is
    /// rounded, padded or aligned.
    /// </para>
    /// <para>
    /// This used to round each boundary DOWN to <see cref="BlockSize"/>, and to refuse any layout
    /// where two runs shared a block — because a block-aligned boundary between two such runs
    /// would put one chunk's bytes inside the other's extent. That premise was conditional on
    /// emitting block-aligned extents, and findings §24 disproved the need for it: ten extent
    /// consumers in the SDK, not one block-sized operation among them, and the validator does not
    /// test alignment. With exact boundaries, two runs sharing a block is harmless by
    /// construction — each extent covers precisely its own run's bytes and nothing else — so
    /// block-sharing is no longer refused. The invariants that actually matter are checked
    /// instead, and still refuse loudly: runs must not OVERLAP, boundaries must strictly
    /// increase, and no file may sit in another chunk's extent
    /// (<see cref="VerifyNoCrossChunkContainment"/>).
    /// </para>
    /// <para>
    /// The consequence is a deliberate, recorded geometry divergence from the oracle: a 64-byte
    /// language run gets a 64-byte extent, where Sony's is 65,536. Sony reaches that by padding
    /// between chunks in its own layout, which is not reachable from here, and §24 established
    /// that nothing requires it. Do not manufacture it.
    /// </para>
    /// <para>
    /// Extents tile <c>[0, mountImageSize)</c> with no gaps, as the oracle's do: the first begins at
    /// 0 (so the FIH block and the inner-PFS metadata below the first file belong to chunk 0) and
    /// the last ends at the end of the mount image.
    /// </para>
    /// </summary>
    internal static Layout Derive(IEnumerable<Placed> placed, long mountImageSize)
    {
        // A zero-length file occupies no package bytes, so it has no footprint and cannot belong
        // to any EXTENT. Such files are separated out rather than refused: a refusal is for a file
        // that HAS bytes this failed to locate.
        //
        // They still get a FICM entry. The SDK emits one entry per inner file with no exceptions,
        // so they are appended to Files, which is what the FICM and the hash table are built from.
        // Appended rather than interleaved because they have no offset to sort by; both tables are
        // built from this one list in this one order, so they stay mutually consistent.
        var empty = placed.Where(f => f.Size == 0).ToList();
        var files = placed.Where(f => f.Size != 0)
                          .OrderBy(f => f.Start).ThenBy(f => f.End).ToList();
        var unplaceable = files.Where(f => f.Start < 0).ToList();
        if (unplaceable.Count > 0)
            throw new InvalidDataException(
                $"{unplaceable.Count} inner file(s) have no measurable package footprint, so no " +
                "extent can be derived from where they landed: " +
                string.Join(", ", unplaceable.Take(5).Select(f => $"{f.Path} ({f.Note})")) +
                ". Deriving a layout while some of the payload cannot be located would produce an " +
                "extent table that is honest only about the files it happened to see.");

        // The extents themselves are exact and carry no alignment (see the remarks above), but the
        // mount image is a whole number of PFS blocks by construction. A measurement that says
        // otherwise means the mount image was mis-measured, and every derived offset with it.
        if (mountImageSize % BlockSize != 0)
            throw new InvalidDataException(
                $"the mount image is {mountImageSize:N0} bytes, which is not a multiple of the " +
                $"{BlockSize:N0}-byte PFS block. The mount image is always a whole number of " +
                "blocks, so this measurement is wrong and nothing derived from it can be trusted.");

        // One entry per run: the chunk, where its first footprint starts and where its last ends.
        var runs = new List<(int Chunk, long Start, long End)>();
        for (int i = 0; i < files.Count;)
        {
            int chunk = files[i].Chunk;
            long runStart = files[i].Start, runEnd = files[i].End;
            int j = i + 1;
            while (j < files.Count && files[j].Chunk == chunk)
            {
                runEnd = Math.Max(runEnd, files[j].End);
                j++;
            }
            runs.Add((chunk, runStart, runEnd));
            i = j;
        }

        // Boundary k is EXACTLY where run k begins; the last one is where the final run ENDS, not
        // the end of the image. The space after the last file -- inner-PFS metadata and the CNT --
        // is its own trailing extent, added below, rather than being absorbed into whichever chunk
        // owns the last file. That is the shape the Windows SDK emits: one extent up to the
        // fillers, one per language chunk, and one for the trailing region.
        var boundaries = new long[runs.Count + 1];
        boundaries[0] = 0;
        // Rounded UP to a block. Every other boundary is the next run's start, and the fillers are
        // block-aligned, so the interior language extents already span a whole block each. The last
        // run has no successor, so without the round-up it would stop at its file's measured
        // footprint -- 64 bytes for a zero-dedup filler -- where the SDK's final language extent
        // covers the whole block. The mount image is a whole number of blocks and nothing else
        // occupies the remainder of that one, so this cannot take space from another file.
        boundaries[^1] = runs.Count > 0
            ? Math.Min(RoundUpToBlock(runs[^1].End), mountImageSize)
            : mountImageSize;
        for (int k = 1; k < runs.Count; k++) boundaries[k] = runs[k].Start;

        for (int k = 1; k < runs.Count; k++)
        {
            var previous = runs[k - 1];
            // The real invariant: two runs must not OVERLAP. With exact boundaries this is the
            // same comparison that used to detect block-sharing, but it now fires only when one
            // chunk's bytes genuinely reach into the next chunk's extent.
            if (boundaries[k] < previous.End)
                throw new InvalidDataException(
                    $"chunk {previous.Chunk}'s run ends at {previous.End:N0} but chunk " +
                    $"{runs[k].Chunk}'s run starts at {runs[k].Start:N0} — the two OVERLAP by " +
                    $"{previous.End - runs[k].Start:N0} byte(s). One chunk's bytes would sit " +
                    "inside the other's extent, which is the defect this derivation exists to " +
                    "remove. Extents are exact and unaligned, so this can only mean the measured " +
                    "footprints themselves overlap.");
            if (boundaries[k] <= boundaries[k - 1])
                throw new InvalidDataException(
                    $"chunk {runs[k].Chunk}'s extent would be empty or inverted: the boundary at " +
                    $"{boundaries[k]:N0} does not advance past {boundaries[k - 1]:N0}.");
        }
        if (runs.Count > 0 && boundaries[^1] < runs[^1].End)
            throw new InvalidDataException(
                $"chunk {runs[^1].Chunk}'s run ends at {runs[^1].End:N0}, past the end of the " +
                $"mount image at {mountImageSize:N0}.");

        var extents = new List<Plgx.Extent>();
        var extentChunk = new List<int>();
        for (int k = 0; k < runs.Count; k++)
        {
            extents.Add(new Plgx.Extent((ulong)boundaries[k], (ulong)(boundaries[k + 1] - boundaries[k])));
            extentChunk.Add(runs[k].Chunk);
        }

        // The trailing region: everything past the last file. It holds no file, so no chunk claims
        // it by measurement -- it is chunk 0's, the same way the header below the first file is.
        // Emitted only when it is non-empty, so a layout whose last file ends exactly at the end of
        // the mount image still produces a table with no degenerate extent.
        long tail = boundaries[^1];
        if (tail < mountImageSize)
        {
            extents.Add(new Plgx.Extent((ulong)tail, (ulong)(mountImageSize - tail)));
            extentChunk.Add(0);
        }

        int languageChunks = files.Select(f => f.Chunk).Where(c => c > 0).Distinct().Count();
        // The extents were derived from `files`; the FICM and hash table cover every file.
        var layout = new Layout([.. files, .. empty], extents, extentChunk, languageChunks);
        VerifyTiling(layout, mountImageSize);
        VerifyNoCrossChunkContainment(layout);
        return layout;
    }

    /// <summary>
    /// The extents must tile <c>[0, mountImageSize)</c> exactly and contiguously: the first starts
    /// at 0, each one begins where the previous ended, none is empty, and the last ends at the end
    /// of the mount image. True by construction above — asserted here so a future change to the
    /// boundary arithmetic cannot quietly introduce a gap or an overlap.
    /// </summary>
    internal static void VerifyTiling(Layout layout, long mountImageSize)
    {
        long expected = 0;
        for (int e = 0; e < layout.Extents.Count; e++)
        {
            var extent = layout.Extents[e];
            if ((long)extent.Offset != expected)
                throw new InvalidDataException(
                    $"extent {e} (chunk {layout.ExtentChunk[e]}) starts at {extent.Offset:N0} but " +
                    $"the previous extent ended at {expected:N0} — the table does not tile.");
            if (extent.Length == 0)
                throw new InvalidDataException(
                    $"extent {e} (chunk {layout.ExtentChunk[e]}) is empty.");
            expected = checked((long)extent.Offset + (long)extent.Length);
        }
        if (expected != mountImageSize)
            throw new InvalidDataException(
                $"the extents cover [0, {expected:N0}) but the mount image is " +
                $"[0, {mountImageSize:N0}) — the table does not tile it exactly.");
    }

    /// <summary>
    /// The original defect, stated directly rather than inferred from the tiling: no chunk-0 byte
    /// may fall inside a language chunk's extent, and no language chunk's byte inside chunk 0's
    /// (nor inside any other language chunk's). Every measured file footprint must lie wholly
    /// within an extent whose chunk is the file's own.
    ///
    /// <para>This is the check that has to hold even if the boundary arithmetic is later changed
    /// again. It is deliberately independent of how the extents were derived: it re-reads the
    /// finished table and the measured footprints and compares them.</para>
    /// </summary>
    internal static void VerifyNoCrossChunkContainment(Layout layout)
    {
        foreach (var file in layout.Files)
        {
            if (file.Start < 0) continue;

            int owner = -1;
            for (int e = 0; e < layout.Extents.Count && owner < 0; e++)
            {
                long start = (long)layout.Extents[e].Offset;
                long end = start + (long)layout.Extents[e].Length;
                if (file.Start >= start && file.Start < end) owner = e;
            }
            if (owner < 0)
                throw new InvalidDataException(
                    $"'{file.Path}' (chunk {file.Chunk}) starts at {file.Start:N0}, which no " +
                    "extent covers.");

            long ownerStart = (long)layout.Extents[owner].Offset;
            long ownerEnd = ownerStart + (long)layout.Extents[owner].Length;

            if (layout.ExtentChunk[owner] != file.Chunk)
                throw new InvalidDataException(
                    $"'{file.Path}' belongs to chunk {file.Chunk} but its bytes " +
                    $"[{file.Start:N0}, {file.End:N0}) start inside extent {owner}, which belongs " +
                    $"to chunk {layout.ExtentChunk[owner]} ([{ownerStart:N0}, {ownerEnd:N0})). " +
                    "One chunk's payload inside another chunk's extent is the exact defect this " +
                    "derivation exists to remove.");

            if (file.End > ownerEnd)
                throw new InvalidDataException(
                    $"'{file.Path}' (chunk {file.Chunk}) spans [{file.Start:N0}, {file.End:N0}) " +
                    $"but its own extent {owner} ends at {ownerEnd:N0}, so its tail falls into " +
                    $"chunk {(owner + 1 < layout.ExtentChunk.Count ? layout.ExtentChunk[owner + 1] : -1)}'s extent.");
        }
    }

    /// <summary>
    /// The <c>naps_meta_300/301/302/308</c> payload: <c>(id u64, offset u64, length u64)</c>,
    /// 24 bytes each, no header and no terminator. MEASURED, spec §5 — <c>offset = extent offset -
    /// 0x10000</c>, chunk 0 clamped to offset 0 with the shortfall taken out of its length, and a
    /// terminal record id <c>0x3E9</c> whose length is the fixed <c>0x20000</c>.
    /// </summary>
    internal static byte[] BuildNapsMeta300(Layout layout)
    {
        var records = new List<(ulong Id, long Offset, long Length)>();
        var chunkZero = layout.Extents[0];
        records.Add((0, 0, (long)chunkZero.Length - NapsMetaShift));
        for (int chunk = 1; chunk <= layout.LanguageChunks; chunk++)
        {
            int at = -1;
            for (int e = 0; e < layout.ExtentChunk.Count && at < 0; e++)
                if (layout.ExtentChunk[e] == chunk) at = e;
            if (at < 0)
                throw new InvalidDataException($"no extent was derived for language chunk {chunk}.");
            records.Add(((ulong)chunk, (long)layout.Extents[at].Offset - NapsMetaShift,
                         (long)layout.Extents[at].Length));
        }
        var tail = layout.Extents[^1];
        records.Add((NapsMetaTerminalId, (long)tail.Offset - NapsMetaShift, (long)NapsMetaTerminalLength));

        var bytes = new byte[records.Count * 24];
        for (int i = 0; i < records.Count; i++)
        {
            var span = bytes.AsSpan(i * 24);
            BinaryPrimitives.WriteUInt64LittleEndian(span, records[i].Id);
            BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)Math.Max(0, records[i].Offset));
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], (ulong)records[i].Length);
        }
        return bytes;
    }

    /// <summary>
    /// Rewrites a parsed <c>playgo-chunk.dat</c> to describe <paramref name="layout"/>. The header
    /// and every chunk record are the originals with only the named fields patched — see
    /// <see cref="Plgx"/> for why.
    /// </summary>
    internal static byte[] BuildChunkDat(byte[] original, Layout layout, string defaultLanguageCode)
    {
        var plgx = Plgx.Parse(original);
        var order = PlayGoFillers.Order(defaultLanguageCode);

        plgx.Extents = [.. layout.Extents];
        plgx.SupportedLanguageMask = DefinedLanguageMask;
        plgx.DefaultLanguageId = (byte)order[0].Id;

        for (int chunk = 0; chunk < plgx.Chunks.Count; chunk++)
        {
            var c = plgx.Chunks[chunk];
            c.ExtentRefs = [.. Enumerable.Range(0, layout.ExtentChunk.Count)
                                         .Where(e => layout.ExtentChunk[e] == chunk)
                                         .Select(e => (uint)e)];
            // A language chunk carries exactly its own language. Chunk 0 and every chunk past the
            // language count carry the header mask, which is what the oracle writes: §7c allows a
            // zero mask only for a chunk that is fully unused, and these are declared, just empty.
            c.Mask = chunk >= 1 && chunk <= layout.LanguageChunks
                ? order[chunk - 1].Mask
                : DefinedLanguageMask;
        }
        return plgx.Build();
    }

    /// <summary>Rounds a byte position up to the next whole PFS block.</summary>
    private static long RoundUpToBlock(long value) =>
        (value + BlockSize - 1) / BlockSize * BlockSize;

    /// <summary>
    /// A histogram of the FICM, for §7.3: how many files each chunk claims.
    /// </summary>
    internal static string Histogram(IReadOnlyList<Placed> files)
    {
        var counts = files.GroupBy(f => f.Chunk).OrderBy(g => g.Key)
                          .Select(g => $"chunk {g.Key}: {g.Count()}");
        return string.Join(", ", counts);
    }


    /// <summary>
    /// The number of leading blocks whose CRC entry is re-verified against the previous table
    /// before any of it is trusted. One MiB of hashing to turn "these bytes cannot have changed"
    /// from an argument into a check.
    /// </summary>
    private const int CrcOverlapBlocks = 16;

    /// <summary>
    /// The PlayGo CRC table, recomputing only the blocks that can have changed.
    ///
    /// <para>
    /// The table is CRC32C over every <see cref="ProsperoPlayGo.ChunkCrcBlockSize"/> block of
    /// [0, <paramref name="mountImageLength"/>) — verified on a real build: 10,113 entries x 65,536
    /// is exactly the SI's offset. Recomputing all of it costs a full pass over the package, which
    /// on the 90 GB title is 84 GiB of reads for a change that touches the last few MB.
    /// </para>
    ///
    /// <para>
    /// <paramref name="verbatimBelow"/> is <c>PackageRegions.CntOffset</c>, which
    /// <c>PackageRegions.WriteTo</c> uses as its verbatim copy length: everything below it is
    /// streamed from the source byte for byte and everything at or above it is replaced. So every
    /// block that lies ENTIRELY below it is bit-identical to the one the previous table describes,
    /// and its entry carries over. The block straddling the boundary is recomputed with the rest.
    /// </para>
    ///
    /// <para>
    /// This is not taken on trust. <see cref="CrcOverlapBlocks"/> blocks below the boundary are
    /// recomputed too and compared against the previous table; a single mismatch means something
    /// changed where the reasoning above says nothing could, so the whole table is recomputed and
    /// the discrepancy is reported rather than shipped. The same fallback covers a missing or
    /// wrong-sized previous table, which is what a layout change would look like.
    /// </para>
    /// </summary>
    internal static byte[] ChunkCrcReusingUnchangedBlocks(
        Stream mountImage, long mountImageLength, byte[]? previous, long verbatimBelow,
        Action<string>? log)
    {
        const int Block = ProsperoPlayGo.ChunkCrcBlockSize;
        long blocks = (mountImageLength + Block - 1) / Block;
        long reusable = verbatimBelow / Block;          // whole blocks below the splice point

        string? refuse =
            previous is null ? "the previous SI carries no PlayGo CRC table"
            : previous.Length != blocks * 4
                ? $"the previous CRC table has {previous.Length / 4:N0} entries but this image needs " +
                  $"{blocks:N0} — the layout changed"
            : reusable <= CrcOverlapBlocks ? "there is nothing worth reusing below the CNT"
            : null;

        if (refuse is not null)
        {
            log?.Invoke($"  PlayGo CRC table: recomputing in full ({refuse}).");
            mountImage.Position = 0;
            return ProsperoPlayGo.BuildChunkCrc(mountImage, mountImageLength, CancellationToken.None,
                                                log ?? (_ => { }));
        }

        long from = reusable - CrcOverlapBlocks;
        var table = (byte[])previous!.Clone();
        var buffer = new byte[Block];

        log?.Invoke($"  PlayGo CRC table: reusing {reusable:N0} of {blocks:N0} block(s) below the " +
                    $"CNT at {verbatimBelow:N0}; recomputing {blocks - from:N0} " +
                    $"({(blocks - from) * (long)Block / 1024 / 1024:N0} MiB) and re-verifying " +
                    $"{CrcOverlapBlocks} of the reused ones.");

        mountImage.Position = from * Block;
        for (long index = from; index < blocks; index++)
        {
            int want = (int)Math.Min(Block, mountImageLength - index * Block);
            mountImage.ReadExactly(buffer, 0, want);
            uint crc = LibProsperoPkg.Util.ProsperoCrc32C.Compute(buffer.AsSpan(0, want));

            int at = (int)(index * 4);
            if (index < reusable)
            {
                // A reused entry, recomputed only to check it. Anything but equality means the
                // splice point is wrong and the whole premise with it.
                if (BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at)) == crc) continue;
                log?.Invoke($"  PlayGo CRC table: block {index:N0} below the CNT does NOT match the " +
                            "previous table, so the bytes below the splice point are not verbatim " +
                            "after all — recomputing the whole table.");
                mountImage.Position = 0;
                return ProsperoPlayGo.BuildChunkCrc(mountImage, mountImageLength,
                                                    CancellationToken.None, log ?? (_ => { }));
            }
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), crc);
        }
        return table;
    }

    /// <summary>Rebuilds the SI zip: the 7 members the library knows plus playgo-scenario.json.</summary>
    internal static byte[] RebuildSi(byte[] si, string contentId, byte[] chunkDat, byte[] napsMeta300,
                                     byte[] scenarioJson, Stream mountImage, long mountImageLength,
                                     long verbatimBelow, Action<string>? log)
    {
        var members = SiMembers.Read(si);
        members.TryGetValue(SiMembers.ChunkCrcPath(contentId), out byte[]? previousCrc);
        var rebuilt = ProsperoSiArchive.BuildMembers(
            contentId,
            null,                                        // pfsimage.xml: absent from these packages
            chunkDat,
            members[SiMembers.NapsMeta18Path],
            napsMeta300,
            ChunkCrcReusingUnchangedBlocks(mountImage, mountImageLength, previousCrc, verbatimBelow, log),
            null).ToList();
        // The 8th member. BuildMembers' set is hard-coded to the library's own 7-member profile and
        // has no slot for it, but the Windows oracle's SI carries it and spec §5 requires it, so it
        // is appended here rather than the whole zip being reimplemented. Position matches the
        // oracle: last, after playgo-chunk.crc.
        rebuilt.Add(new ProsperoSiMember($"config/{contentId}/playgo-scenario.json", scenarioJson));
        return ProsperoSiArchive.WriteZip(rebuilt);
    }

    internal static class SiMembers
    {
        internal const string NapsMeta18Path = "common/etc/naps_meta_18.dat";

        /// <summary>The PlayGo CRC table's member name, which is content-id scoped.</summary>
        internal static string ChunkCrcPath(string contentId) =>
            $"config/{contentId}/playgo-chunk.crc";

        internal static Dictionary<string, byte[]> Read(byte[] si)
        {
            var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using var stream = new MemoryStream(si, writable: false);
            using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                using var content = entry.Open();
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                if (!members.TryAdd(entry.FullName, buffer.ToArray()))
                    throw new InvalidDataException(
                        $"the SI segment lists '{entry.FullName}' twice; refusing to guess which copy is current.");
            }
            if (!members.ContainsKey(NapsMeta18Path))
                throw new InvalidDataException($"the SI segment is missing '{NapsMeta18Path}'.");
            return members;
        }
    }

    internal static string Describe(Layout layout)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{layout.Extents.Count} extent(s) derived, {layout.LanguageChunks} language chunk(s)");
        for (int i = 0; i < layout.Extents.Count; i++)
            sb.AppendLine($"  ext {i,2} chunk {layout.ExtentChunk[i],2} " +
                          $"off {layout.Extents[i].Offset,14:N0} len {layout.Extents[i].Length,14:N0} " +
                          $"end {layout.Extents[i].Offset + layout.Extents[i].Length,14:N0}");
        return sb.ToString().TrimEnd();
    }
}
