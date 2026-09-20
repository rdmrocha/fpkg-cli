using System.Buffers.Binary;
using Fpkg.Cli.PlayGo;
using LibProsperoPkg.PlayGo;
using Xunit;

/// <summary>
/// The FICM is indexed by hash-table slot, not by file order. Before this was understood, 45 of
/// the reference fixture's 52 files carried another file's chunk id — including
/// <c>Media/Metadata/global-metadata.dat</c>, declared as belonging to language chunk 4. These
/// tests assert the invariant that failure violated.
/// </summary>
public class PlayGoFicmOrderTests
{
    private static string[] Paths(int n) =>
        [.. Enumerable.Range(0, n).Select(i => i % 4 == 0
            ? $"playgo-languages/{i / 4 + 1:00}-xx-XX.bin"
            : $"Media/Resources/asset{i}.dat")];

    private static (int Offset, int Size) Header(byte[] t) =>
        (BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(8)),
         BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(12)));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(17)] [InlineData(53)] [InlineData(120)]
    public void IsAPermutationOfEveryFile(int n)
    {
        var order = PlayGoFicmOrder.SlotOrder(Paths(n));
        Assert.Equal(n, order.Count);
        Assert.Equal(Enumerable.Range(0, n), order.Order());
    }

    [Fact]
    public void EmptyInputIsEmpty() => Assert.Empty(PlayGoFicmOrder.SlotOrder([]));

    /// <summary>
    /// The invariant that matters: read the FICM at the slot the hash table puts a path in, and you
    /// get that path's chunk. This is exactly the check that caught the defect on a real package.
    /// </summary>
    [Theory]
    [InlineData(17)] [InlineData(53)]
    public void TheChunkAtAFilesHashSlotIsItsOwnChunk(int n)
    {
        string[] paths = Paths(n);
        byte Chunk(string p) => p.StartsWith("playgo-languages/", StringComparison.Ordinal)
            ? byte.Parse(p.AsSpan(17, 2))
            : (byte)0;

        var order = PlayGoFicmOrder.SlotOrder(paths);
        byte[] ficm = ProsperoPlayGo.BuildFicm([.. order.Select(i => Chunk(paths[i]))]);
        byte[] table = ProsperoPlayGo.BuildHashTable(paths);

        var (tOff, tSize) = Header(table);
        var (fOff, _) = Header(ficm);
        int record = tSize / n;

        foreach (string path in paths)
        {
            byte[] alone = ProsperoPlayGo.BuildHashTable([path]);
            var (aOff, _) = Header(alone);
            int slot = -1;
            for (int s = 0; s < n; s++)
                if (table.AsSpan(tOff + s * record, record)
                         .SequenceEqual(alone.AsSpan(aOff, record))) { slot = s; break; }
            Assert.True(slot >= 0, $"{path} has no slot");
            Assert.Equal(Chunk(path), BinaryPrimitives.ReadUInt16LittleEndian(ficm.AsSpan(fOff + slot * 2)));
        }
    }

    /// <summary>
    /// The premise, restated as a test: the hash table does not depend on the order it is given.
    /// If this ever fails, the slot a path occupies depends on the caller and SlotOrder's whole
    /// approach — locating a one-path record inside the combined table — stops being valid.
    /// </summary>
    [Fact]
    public void TheHashTableItselfIsOrderIndependent()
    {
        string[] paths = Paths(53);
        byte[] baseline = ProsperoPlayGo.BuildHashTable(paths);
        Assert.Equal(baseline, ProsperoPlayGo.BuildHashTable([.. paths.Reverse()]));
        Assert.Equal(baseline, ProsperoPlayGo.BuildHashTable([.. paths.Order(StringComparer.Ordinal)]));
    }
}
