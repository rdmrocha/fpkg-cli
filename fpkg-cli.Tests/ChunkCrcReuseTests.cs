using Fpkg.Cli.PlayGo;
using LibProsperoPkg.PlayGo;
using Xunit;

/// <summary>
/// The CRC table splice. It is the difference between 84 GiB of hashing and a few dozen MB on the
/// 90 GB title, so the thing that matters is not the saving but that the spliced table is
/// indistinguishable from the honest one — and that it refuses to splice the moment its premise
/// stops holding.
/// </summary>
public class ChunkCrcReuseTests
{
    private const int Block = ProsperoPlayGo.ChunkCrcBlockSize;

    private static MemoryStream Image(int blocks, int tail = 0, int seed = 1)
    {
        var rng = new Random(seed);
        var data = new byte[blocks * Block + tail];
        rng.NextBytes(data);
        return new MemoryStream(data, writable: false);
    }

    private static byte[] Full(MemoryStream image)
    {
        image.Position = 0;
        return ProsperoPlayGo.BuildChunkCrc(image, image.Length, CancellationToken.None, _ => { });
    }

    /// <summary>
    /// The load-bearing one: with a valid previous table and a real splice point, the spliced
    /// table equals the table computed from scratch. Byte for byte, including the block that
    /// straddles the boundary and a final partial block.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public void SplicedTableEqualsAFullRecompute(int tail)
    {
        var image = Image(200, tail);
        byte[] previous = Full(image);

        // The CNT starts mid-block, exactly as it does in a real package.
        long cntOffset = 180 * Block + 1234;
        byte[] spliced = PlayGoRebuild.ChunkCrcReusingUnchangedBlocks(
            image, image.Length, previous, cntOffset, null);

        Assert.Equal(previous, spliced);
    }

    /// <summary>
    /// The bytes at or above the splice point are the ones allowed to change, and a changed block
    /// there must show up in the table.
    /// </summary>
    [Fact]
    public void AChangeAtOrAboveTheSplicePointIsPickedUp()
    {
        var original = Image(200);
        byte[] previous = Full(original);

        var edited = new byte[original.Length];
        original.Position = 0;
        original.ReadExactly(edited);
        edited[190 * Block + 7] ^= 0xFF;                       // above the CNT boundary
        var image = new MemoryStream(edited, writable: false);

        byte[] spliced = PlayGoRebuild.ChunkCrcReusingUnchangedBlocks(
            image, image.Length, previous, 180 * Block, null);

        Assert.Equal(Full(image), spliced);
        Assert.NotEqual(previous, spliced);
    }

    /// <summary>
    /// The self-check. If a block BELOW the splice point has changed, the premise is wrong: the
    /// table is recomputed in full rather than shipped with a stale entry, and it says so.
    /// </summary>
    [Fact]
    public void AChangeBelowTheSplicePointForcesAFullRecomputeAndIsReported()
    {
        var original = Image(200);
        byte[] previous = Full(original);

        var edited = new byte[original.Length];
        original.Position = 0;
        original.ReadExactly(edited);
        // Inside the re-verified overlap, which is the 16 blocks below the boundary.
        edited[(180 - 3) * Block + 11] ^= 0xFF;
        var image = new MemoryStream(edited, writable: false);

        var said = new List<string>();
        byte[] spliced = PlayGoRebuild.ChunkCrcReusingUnchangedBlocks(
            image, image.Length, previous, 180 * Block, said.Add);

        Assert.Equal(Full(image), spliced);
        Assert.Contains(said, l => l.Contains("does NOT match", StringComparison.Ordinal));
        Assert.Contains(said, l => l.Contains("recomputing the whole table", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPreviousTableMeansAFullRecompute()
    {
        var image = Image(200);
        var said = new List<string>();
        Assert.Equal(Full(image),
            PlayGoRebuild.ChunkCrcReusingUnchangedBlocks(image, image.Length, null, 180 * Block, said.Add));
        Assert.Contains(said, l => l.Contains("carries no PlayGo CRC table", StringComparison.Ordinal));
    }

    [Fact]
    public void APreviousTableOfTheWrongSizeMeansAFullRecompute()
    {
        var image = Image(200);
        var said = new List<string>();
        Assert.Equal(Full(image),
            PlayGoRebuild.ChunkCrcReusingUnchangedBlocks(image, image.Length, new byte[64],
                                                         180 * Block, said.Add));
        Assert.Contains(said, l => l.Contains("the layout changed", StringComparison.Ordinal));
    }

    /// <summary>A splice point so low there is nothing to save is not worth the risk of trying.</summary>
    [Fact]
    public void ASplicePointInsideTheOverlapMeansAFullRecompute()
    {
        var image = Image(200);
        byte[] previous = Full(image);
        var said = new List<string>();
        Assert.Equal(previous,
            PlayGoRebuild.ChunkCrcReusingUnchangedBlocks(image, image.Length, previous,
                                                         8 * Block, said.Add));
        Assert.Contains(said, l => l.Contains("nothing worth reusing", StringComparison.Ordinal));
    }
}
