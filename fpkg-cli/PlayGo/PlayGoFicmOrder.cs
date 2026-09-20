using System.Buffers.Binary;
using LibProsperoPkg.PlayGo;

namespace Fpkg.Cli.PlayGo;

/// <summary>
/// The order the FICM's entries must be written in.
///
/// <para><b>It is not the file order.</b> <c>playgo-hash-table.dat</c> is an array of fixed-size
/// records that is INDEPENDENT of the order the paths are handed to it — measured: feeding Sony's
/// 53 the reference fixture paths in enumeration order, offset order, alphabetical order, reversed order, and
/// with a leading slash all produce the same 480 bytes, byte-identical to the oracle's. So the
/// records are placed by hash, and the slot a path lands in has nothing to do with where it sits in
/// the package or in the tree.</para>
///
/// <para><b>The FICM is indexed by that slot.</b> Also measured, and this is the load-bearing
/// fact: rebuilding Sony's <c>playgo-ficm.dat</c> from its own paths matches byte-for-byte when the
/// chunk ids are written in hash-slot order, and fails in all four of the obvious orders
/// (enumeration 45 bytes differ, offset 44, offset-with-unplaced-last 44, alphabetical 45).</para>
///
/// <para><b>Why it matters.</b> The FICM says which chunk each file belongs to. Written in the
/// wrong order the chunk ids land on the wrong files, so a real game file inherits a language
/// chunk's id — and a file whose chunk the console has not installed is a file the console will not
/// read.</para>
/// </summary>
internal static class PlayGoFicmOrder
{
    /// <summary>
    /// <paramref name="paths"/> reordered so that entry <c>i</c> describes the file occupying slot
    /// <c>i</c> of the hash table built from those same paths.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// A path could not be located, or two shared a slot. Either means the record layout is not
    /// what this was derived against, and a FICM built on a guess would silently mis-assign chunks.
    /// </exception>
    internal static IReadOnlyList<int> SlotOrder(IReadOnlyList<string> paths)
    {
        byte[] table = ProsperoPlayGo.BuildHashTable([.. paths]);
        var (offset, size) = Header(table);
        if (paths.Count == 0) return [];
        if (size % paths.Count != 0)
            throw new InvalidDataException(
                $"the hash table's {size}-byte body does not divide into {paths.Count} equal " +
                "records, so its slots cannot be identified; re-derive PlayGoFicmOrder.");
        int record = size / paths.Count;

        var slotOf = new int[paths.Count];
        var taken = new int[paths.Count];
        Array.Fill(taken, -1);
        for (int p = 0; p < paths.Count; p++)
        {
            // The record this one path produces on its own is the record it contributes to the
            // combined table; finding it there is what identifies the slot.
            byte[] alone = ProsperoPlayGo.BuildHashTable([paths[p]]);
            var (aloneOffset, aloneSize) = Header(alone);
            if (aloneSize != record)
                throw new InvalidDataException(
                    $"a single path produces a {aloneSize}-byte record but the table uses " +
                    $"{record}; re-derive PlayGoFicmOrder.");
            var mine = alone.AsSpan(aloneOffset, record);

            int at = -1;
            for (int s = 0; s < paths.Count; s++)
                if (table.AsSpan(offset + s * record, record).SequenceEqual(mine)) { at = s; break; }
            if (at < 0)
                throw new InvalidDataException(
                    $"'{paths[p]}' has no record in the hash table built from the same list.");
            if (taken[at] >= 0)
                throw new InvalidDataException(
                    $"'{paths[p]}' and '{paths[taken[at]]}' both occupy hash slot {at}. The FICM " +
                    "is indexed by slot, so one of them would take the other's chunk id.");
            taken[at] = p;
            slotOf[p] = at;
        }

        // slotOf maps path -> slot; the FICM wants slot -> path.
        var order = new int[paths.Count];
        for (int p = 0; p < paths.Count; p++) order[slotOf[p]] = p;
        return order;
    }

    /// <summary>The body's offset and length, as the header declares them.</summary>
    private static (int Offset, int Size) Header(byte[] table)
    {
        if (table.Length < 16)
            throw new InvalidDataException("the hash table is too short to carry a header.");
        int offset = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(8));
        int size = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(12));
        if (offset < 16 || size < 0 || (long)offset + size > table.Length)
            throw new InvalidDataException(
                $"the hash table declares a {size}-byte body at {offset} but is {table.Length} bytes.");
        return (offset, size);
    }
}
