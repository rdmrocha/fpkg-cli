using System.Buffers.Binary;
using LibProsperoPkg.PlayGo;
using Xunit;

/// <summary>
/// The FICM is indexed by hash-table slot, not by file order. Before this was understood, 45 of
/// the reference fixture's 52 files carried another file's chunk id — including
/// <c>Media/Metadata/global-metadata.dat</c>, declared as belonging to language chunk 4. fpkg's
/// own slot ordering has since been replaced by 0.6.11's <c>ProsperoPlayGo.BuildFileMap</c>, which
/// is byte-identical to it; these tests keep asserting the invariant against what fpkg now calls.
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

    [Fact]
    public void EmptyInputIsEmpty()
    {
        var (table, ficm) = ProsperoPlayGo.BuildFileMap([], []);
        Assert.Equal(0, Header(table).Size);
        Assert.Equal(0, Header(ficm).Size);
    }

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

        var (table, ficm) = ProsperoPlayGo.BuildFileMap(paths, [.. paths.Select(Chunk)]);

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
    /// If this ever fails, the slot a path occupies depends on the caller, and the slot lookup the
    /// invariant test above relies on — locating a one-path record inside the combined table —
    /// stops being valid.
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
