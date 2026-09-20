using Fpkg.Tests;
using Fpkg.Cli.PlayGo;
using Xunit;

/// <summary>
/// The PlayGo layout derivation (spec §5) and the plgx writer that expresses it.
///
/// <para>
/// The load-bearing test is <see cref="PlgxRoundTripsAWindowsBuiltChunkDat"/>: the writer is only
/// usable at all if re-emitting a container Sony's own toolkit produced reproduces it byte for
/// byte. Everything else here is arithmetic, and is written against synthetic layouts so the rules
/// can be checked at their edges rather than only at whatever a real package happens to hit.
/// </para>
/// </summary>
public class PlayGoTests
{
    private const uint ChunkDatId = 4097;

    [Fact]
    public void PlgxRoundTripsAWindowsBuiltChunkDat()
    {
        var regions = PackageRegions.Load(SyntheticPackage.Path);
        var table = CntEntryTable.Parse(regions.Cnt, SyntheticPackage.Passcode);
        byte[] original = table[ChunkDatId].Payload;

        // Not a smoke test: this container has 100 chunks, 33 extents, a two-reference chunk 0 and
        // 68 label-only chunks, so the section order, the inter-section padding and the extent-ref
        // pooling are all exercised by it.
        Assert.Equal(5376, original.Length);
        Assert.Null(Plgx.RoundTrips(original));

        var parsed = Plgx.Parse(original);
        Assert.Equal(33, parsed.Extents.Count);
        Assert.Equal(100, parsed.Chunks.Count);
        Assert.Equal(PlayGoRebuild.DefinedLanguageMask, parsed.SupportedLanguageMask);
        Assert.Equal([0u, 32u], parsed.Chunks[0].ExtentRefs);
    }

    [Theory]
    [InlineData("playgo-languages/01-en-US.bin", 1)]
    [InlineData("playgo-languages/31-uk-UA.bin", 31)]
    [InlineData("playgo-languages/notanumber.bin", 0)]
    [InlineData("Media/level0", 0)]
    [InlineData("sce_sys/keystone", 0)]
    // Spec §2, PROVEN: a missing chunk assignment is chunk 0, never "unknown".
    [InlineData("", 0)]
    public void ChunkOfReadsTheFillerIndexAndDefaultsToZero(string path, int expected) =>
        Assert.Equal(expected, PlayGoRebuild.ChunkOf(path));

    // Size is the file's own length, which only the zero-length rule reads; every file these
    // tests place has bytes, so it is derived from the footprint rather than stated at each site.
    private static PlayGoRebuild.Placed At(string path, int chunk, long start, long end) =>
        new(path, chunk, start, end, null, end - start);

    private const long Block = PlayGoRebuild.BlockSize;

    [Fact]
    public void DeriveCoalescesEachRunAndTilesTheWholeImage()
    {
        var layout = PlayGoRebuild.Derive(
        [
            At("a", 0, 1 * Block, 2 * Block),
            At("b", 0, 2 * Block, 3 * Block),
            At("f1", 1, 3 * Block, 3 * Block + 40),
            At("f2", 2, 4 * Block, 4 * Block + 40),
            At("c", 0, 5 * Block, 9 * Block),
        ], mountImageSize: 10 * Block);

        // Four files in chunk 0 but only two runs, because two of them are separated by the
        // language fillers — chunk 0 is the one chunk allowed to be discontiguous.
        // Five extents, not four: the region past the LAST file is its own trailing extent rather
        // than being absorbed into whichever chunk happens to own that file. The oracle's stored
        // table does the same -- 33 records, the 33rd being the 983,040-byte tail.
        Assert.Equal([0, 1, 2, 0, 0], layout.ExtentChunk);
        // The first extent starts at 0, not at the first file: everything below it (the FIH block
        // and the inner-PFS metadata) is chunk 0's too.
        Assert.Equal(new Plgx.Extent(0, (ulong)(3 * Block)), layout.Extents[0]);
        Assert.Equal(new Plgx.Extent((ulong)(3 * Block), (ulong)Block), layout.Extents[1]);
        Assert.Equal(new Plgx.Extent((ulong)(4 * Block), (ulong)Block), layout.Extents[2]);
        Assert.Equal(new Plgx.Extent((ulong)(5 * Block), (ulong)(4 * Block)), layout.Extents[3]);
        Assert.Equal(new Plgx.Extent((ulong)(9 * Block), (ulong)Block), layout.Extents[4]);
        Assert.Equal(2, layout.LanguageChunks);
    }

    [Fact]
    public void EveryInteriorBoundaryIsTheExactRunStart_NotRoundedToABlock()
    {
        // Extents are EXACT. The derivation used to round each boundary down to a block because
        // it believed alignment was required; findings §24 disproved that (ten extent consumers,
        // no block-sized operation among them, and the validator does not test it). A run whose
        // start is not block-aligned must now yield an extent that begins exactly there.
        //
        // Only the FINAL boundary is block-rounded, and for a different reason: the oracle's last
        // language extent is a full 65,536 even though the filler's footprint is 64 bytes, and the
        // trailing extent begins on that block boundary.
        var layout = PlayGoRebuild.Derive(
        [
            At("a", 0, 0, 2 * Block + 17),
            At("f1", 1, 3 * Block + 9, 3 * Block + 400),
            At("f2", 2, 4 * Block + 5, 4 * Block + 400),
            At("c", 0, 5 * Block, 6 * Block),
        ], mountImageSize: 8 * Block);

        Assert.Equal([0, 1, 2, 0, 0], layout.ExtentChunk);
        Assert.Equal((ulong)(3 * Block + 9), layout.Extents[1].Offset);
        Assert.Equal((ulong)(4 * Block + 5), layout.Extents[2].Offset);
        // ... and the length is the exact distance to the next run, not a block multiple.
        Assert.Equal((ulong)(Block - 4), layout.Extents[1].Length);
        Assert.NotEqual(0UL, layout.Extents[1].Offset % (ulong)Block);
    }

    [Fact]
    public void ATinyLanguageRunGetsAnExactlySizedExtent()
    {
        // An INTERIOR tiny run gets an exactly-sized extent: its boundary is the next run's start,
        // so a 64-byte filler footprint followed immediately by another file yields 64 bytes. Only
        // the last run is rounded up to its block, which is what makes the oracle's final language
        // extent a full 65,536.
        var layout = PlayGoRebuild.Derive(
        [
            At("a", 0, 0, 3 * Block),
            At("f1", 1, 3 * Block, 3 * Block + 64),
            At("f2", 2, 3 * Block + 64, 3 * Block + 128),
            At("c", 0, 3 * Block + 128, 4 * Block),
        ], mountImageSize: 8 * Block);

        Assert.Equal([0, 1, 2, 0, 0], layout.ExtentChunk);
        Assert.Equal(64UL, layout.Extents[1].Length);
        Assert.Equal(64UL, layout.Extents[2].Length);
    }

    [Fact]
    public void DerivedExtentsTileTheMountImageExactly()
    {
        const long Mount = 10 * Block;
        var layout = PlayGoRebuild.Derive(
        [
            At("a", 0, 0, 2 * Block + 17),
            At("f1", 1, 3 * Block + 9, 3 * Block + 400),
            At("f2", 2, 4 * Block + 5, 4 * Block + 400),
            At("c", 0, 5 * Block, 6 * Block),
        ], mountImageSize: Mount);

        // No gaps and no overlaps, and the total is the mount image — that is what makes the
        // table honest about the image rather than merely self-consistent.
        ulong at = 0;
        foreach (var e in layout.Extents) { Assert.Equal(at, e.Offset); at += e.Length; }
        Assert.Equal((ulong)Mount, at);
        Assert.Equal((ulong)Mount, layout.Extents.Aggregate(0UL, (sum, e) => sum + e.Length));
    }

    [Fact]
    public void DeriveAcceptsTwoChunksSharingABlock()
    {
        // This is the case the derivation used to REFUSE, and it is the case every real build now
        // produces: the 31 zero fillers compress to 64 bytes each, so all 31 land inside one
        // 65,536-byte block. With exact extents that is harmless — each covers precisely its own
        // run — so it must be accepted, and each chunk must get its own extent.
        var layout = PlayGoRebuild.Derive(
        [
            At("f1", 1, 3 * Block, 3 * Block + 75),
            At("f2", 2, 3 * Block + 75, 3 * Block + 150),
        ], mountImageSize: 8 * Block);

        // Three extents: the two chunks sharing a block, then the trailing region past the last
        // file, which is chunk 0's.
        Assert.Equal([1, 2, 0], layout.ExtentChunk);
        // Extent 0 starts at 0 (everything below the first file belongs to the first run) and ends
        // exactly where chunk 2's run begins, 75 bytes into the shared block.
        Assert.Equal(new Plgx.Extent(0, (ulong)(3 * Block + 75)), layout.Extents[0]);
        Assert.Equal((ulong)(3 * Block + 75), layout.Extents[1].Offset);
        // Chunk 2 is the LAST run, so its extent reaches the end of its block and the remainder of
        // the image becomes the trailing extent.
        Assert.Equal((ulong)(4 * Block) - (ulong)(3 * Block + 75), layout.Extents[1].Length);
        Assert.Equal(new Plgx.Extent((ulong)(4 * Block), (ulong)(4 * Block)), layout.Extents[2]);
    }

    [Fact]
    public void DeriveRefusesWhenTwoRunsOverlap()
    {
        // The invariant that replaced the block-sharing guard: genuine overlap, where one chunk's
        // measured bytes reach into the next chunk's run.
        var ex = Assert.Throws<InvalidDataException>(() => PlayGoRebuild.Derive(
        [
            At("f1", 1, 3 * Block, 3 * Block + 200),
            At("f2", 2, 3 * Block + 75, 3 * Block + 250),
        ], mountImageSize: 8 * Block));
        Assert.Contains("OVERLAP", ex.Message);
    }

    [Fact]
    public void VerifyNoCrossChunkContainmentRejectsAFileInAnotherChunksExtent()
    {
        // The original defect, stated directly rather than inferred from the tiling: a chunk-0
        // file whose bytes sit inside a language chunk's extent. Derive() cannot currently build
        // such a layout, which is the point — this asserts the check would catch it if a later
        // change to the boundary arithmetic ever did.
        var bad = new PlayGoRebuild.Layout(
            Files:
            [
                At("Media/level0", 0, 3 * Block + 10, 3 * Block + 20),
                At("playgo-languages/01-en-US.bin", 1, 3 * Block, 3 * Block + 64),
            ],
            Extents:
            [
                new Plgx.Extent(0, (ulong)(3 * Block)),
                new Plgx.Extent((ulong)(3 * Block), (ulong)(5 * Block)),
            ],
            ExtentChunk: [0, 1],
            LanguageChunks: 1);

        var ex = Assert.Throws<InvalidDataException>(
            () => PlayGoRebuild.VerifyNoCrossChunkContainment(bad));
        Assert.Contains("Media/level0", ex.Message);
        Assert.Contains("belongs to chunk 0", ex.Message);
    }

    [Fact]
    public void VerifyNoCrossChunkContainmentRejectsATailThatSpillsIntoTheNextChunk()
    {
        var bad = new PlayGoRebuild.Layout(
            Files:
            [
                At("Media/level0", 0, 2 * Block, 3 * Block + 8),
                At("playgo-languages/01-en-US.bin", 1, 3 * Block, 3 * Block + 64),
            ],
            Extents:
            [
                new Plgx.Extent(0, (ulong)(3 * Block)),
                new Plgx.Extent((ulong)(3 * Block), (ulong)(5 * Block)),
            ],
            ExtentChunk: [0, 1],
            LanguageChunks: 1);

        var ex = Assert.Throws<InvalidDataException>(
            () => PlayGoRebuild.VerifyNoCrossChunkContainment(bad));
        Assert.Contains("tail falls into", ex.Message);
    }

    [Fact]
    public void DeriveRefusesAMountImageThatIsNotBlockAligned()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PlayGoRebuild.Derive(
            [At("a", 0, 0, 100)], mountImageSize: 1000));
        Assert.Contains("not a multiple of the", ex.Message);
    }

    [Fact]
    public void DeriveRefusesAFileItCannotPlace()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PlayGoRebuild.Derive(
        [
            At("a", 0, 0, 100),
            // Unplaceable but NOT empty: a file with bytes we failed to locate is refused,
            // which is exactly what a zero-length file must not be confused with.
            new PlayGoRebuild.Placed("lost", 0, -1, -1, "fragmented-in-inner-pfs", 4096),
        ], mountImageSize: 1000));
        Assert.Contains("no measurable package footprint", ex.Message);
        Assert.Contains("lost", ex.Message);
    }

    [Fact]
    public void NapsMeta300FollowsTheMeasuredOffsetAndLengthRules()
    {
        const long Shift = 0x10000;
        var layout = PlayGoRebuild.Derive(
        [
            At("a", 0, 0, 3 * Shift),
            At("f1", 1, 3 * Shift, 4 * Shift),
            At("f2", 2, 4 * Shift, 5 * Shift),
            At("c", 0, 5 * Shift, 6 * Shift),
        ], mountImageSize: 8 * Shift);

        byte[] meta = PlayGoRebuild.BuildNapsMeta300(layout);
        // Count = 1 + languageChunks + 1, spec §5.
        Assert.Equal(4, meta.Length / 24);

        (ulong Id, ulong Offset, ulong Length) Record(int i) =>
            (BitConverter.ToUInt64(meta, i * 24),
             BitConverter.ToUInt64(meta, i * 24 + 8),
             BitConverter.ToUInt64(meta, i * 24 + 16));

        // Chunk 0 clamps its offset at 0 and takes the shortfall out of its LENGTH.
        Assert.Equal((0UL, 0UL, (ulong)(3 * Shift - Shift)), Record(0));
        // Every other record is simply the extent shifted down by one block.
        Assert.Equal((1UL, (ulong)(3 * Shift - Shift), (ulong)Shift), Record(1));
        Assert.Equal((2UL, (ulong)(4 * Shift - Shift), (ulong)Shift), Record(2));
        // The terminal record keeps the shift rule but its length is the fixed 0x20000, NOT the
        // tail extent's own length. Its OFFSET follows the last extent, which since the derivation
        // started emitting a trailing extent is the region past the final file (6..8 here), not
        // the last chunk-0 file run (5..6). One rule, applied to a table that now has one more
        // entry.
        Assert.Equal((0x3E9UL, (ulong)(6 * Shift - Shift), 0x20000UL), Record(3));
    }

    [Fact]
    public void FillerOrderPutsTheDefaultLanguageInChunkOne()
    {
        var order = PlayGoFillers.Order("en-US");
        Assert.Equal(31, order.Count);
        Assert.Equal("en-US", order[0].Code);
        Assert.Equal("01-en-US.bin", PlayGoFillers.FileName(1, order[0]));
        // ja-JP is language id 0 and would be first in canonical order; it is not the default here.
        Assert.Equal("ja-JP", order[1].Code);
        Assert.Equal("02-ja-JP.bin", PlayGoFillers.FileName(2, order[1]));

        var swapped = PlayGoFillers.Order("ja-JP");
        Assert.Equal("ja-JP", swapped[0].Code);
        Assert.Equal("en-US", swapped[1].Code);
    }

    [Fact]
    public void DistinctFillerPayloadsAreUniqueAndZeroOnesAreNot()
    {
        var order = PlayGoFillers.Order("en-US");
        var distinct = Enumerable.Range(1, order.Count)
            .Select(c => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                PlayGoFillers.Payload(c, order[c - 1], distinct: true))))
            .ToList();
        Assert.Equal(order.Count, distinct.Distinct().Count());
        Assert.All(distinct, _ => Assert.Equal(PlayGoFillers.DistinctPayloadSize,
            PlayGoFillers.Payload(1, order[0], distinct: true).Length));
        // Incompressible is the load-bearing property, not just uniqueness: only a stored-raw file
        // is placed on a block boundary, and without that the aligned derivation refuses. A crude
        // entropy proxy — every byte value should appear in 64 KiB of a good stream.
        var histogram = new bool[256];
        foreach (byte b in PlayGoFillers.Payload(1, order[0], distinct: true)) histogram[b] = true;
        Assert.DoesNotContain(false, histogram);
        // Deterministic across calls, which §7.5 depends on.
        Assert.Equal(PlayGoFillers.Payload(5, order[4], distinct: true),
                     PlayGoFillers.Payload(5, order[4], distinct: true));

        // The reason the distinct payload existed: identical zero fillers are what this library's
        // NAPS folds onto one stored copy, leaving every language chunk owning no bytes at all.
        // Sony's zeros are the default again, and they are byte-identical by design — what makes
        // them usable is IL site 10, not their content, so this identity must hold exactly.
        var zeros = Enumerable.Range(1, order.Count)
            .Select(c => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                PlayGoFillers.Payload(c, order[c - 1], distinct: false))))
            .ToList();
        Assert.Single(zeros.Distinct());
    }

    [Fact]
    public void SealDoesNotMutateTheEntriesItIsGiven()
    {
        // The repair-playgo original assigned DataOffset/DataSize and three derived payloads onto
        // the caller's entries, so running the fixed-point gate and then the real repair resealed
        // from an already-rewritten table. Sealing twice from one table must give the same bytes.
        var regions = PackageRegions.Load(SyntheticPackage.Path);
        var table = CntEntryTable.Parse(regions.Cnt, SyntheticPackage.Passcode);
        string contentId = CntHeader.ReadContentId(regions.Cnt);

        var before = table.Physical
            .Select(e => (e.Id, e.DataOffset, e.DataSize, Payload: Convert.ToHexString(e.Payload)))
            .ToList();
        byte[] first = CntReseal.Seal(regions.Cnt, table.Physical, contentId, SyntheticPackage.Passcode);
        var after = table.Physical
            .Select(e => (e.Id, e.DataOffset, e.DataSize, Payload: Convert.ToHexString(e.Payload)))
            .ToList();
        Assert.Equal(before, after);

        byte[] second = CntReseal.Seal(regions.Cnt, table.Physical, contentId, SyntheticPackage.Passcode);
        Assert.Equal(first, second);
    }
}
