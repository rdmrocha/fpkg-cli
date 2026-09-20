using System.Buffers.Binary;

namespace Fpkg.Cli;

/// <summary>
/// The SELF normalisation the Windows toolkit performs before packaging, transcribed from
/// <c>create-gp5-from-folder.py</c> (<c>self_repair_plan</c> / <c>write_repaired_self</c>) and
/// verified byte-for-byte against its output.
///
/// <para><b>What it does, and what it is not.</b> A SELF's header declares, at offset
/// <c>0x10</c>, where its <c>.sceversion</c> trailer begins. In some binaries the trailer actually
/// starts a few bytes EARLIER than that. The repair pushes the trailer forward to the declared
/// boundary by inserting that many zero bytes in front of it, so the header and the trailer agree.
/// This is not alignment: the shift is whatever the header declares, searched over a 15-byte
/// window, and a file whose trailer already starts at the boundary is left untouched.</para>
///
/// <para>A legacy (PS4) SELF magic is additionally rewritten to the Prospero one, which is the only
/// case where a file with no trailer problem is still rewritten.</para>
///
/// <para>Verified against a Windows-built reference package: every executable it repairs comes out
/// byte-identical, and the executables whose trailer already sits on the boundary are declined.</para>
/// </summary>
internal static class SelfNormalize
{
    internal static ReadOnlySpan<byte> ProsperoMagic => [0x54, 0x14, 0xF5, 0xEE];
    internal static ReadOnlySpan<byte> LegacyMagic => [0x4F, 0x15, 0x3D, 0x1D];
    private const int MaxMetadata = 64 * 1024 * 1024;
    private const int Window = 0x0F;

    /// <summary>What a repair would do: rewrite the magic, and/or insert padding at an offset.</summary>
    internal readonly record struct Plan(bool RewriteMagic, long TrailerOffset, int Padding)
    {
        /// <summary>False when the file is already in the shape the toolkit wants.</summary>
        internal bool ChangesAnything => RewriteMagic || Padding > 0;
    }

    /// <summary>
    /// A chain of <c>.sceversion</c> records that starts here and runs exactly to the end.
    /// Record: <c>00 00</c>, a little-endian <c>u16</c> payload size, a <c>08</c> tag, a printable
    /// colon-terminated name, then the version field TWICE. Anything else and this is not the
    /// trailer.
    /// </summary>
    internal static bool IsCompleteSceVersion(ReadOnlySpan<byte> records)
    {
        if (records.IsEmpty) return false;
        int offset = 0, count = 0;
        while (offset < records.Length)
        {
            if (records.Length - offset < 5 ||
                records[offset] != 0 || records[offset + 1] != 0) return false;
            int payload = BinaryPrimitives.ReadUInt16LittleEndian(records[(offset + 2)..]);
            int record = payload + 4;
            if (payload < 18 || record > records.Length - offset || records[offset + 4] != 8)
                return false;

            int nameSize = payload - 17;
            var name = records.Slice(offset + 5, nameSize);
            if (nameSize == 0 || name[^1] != (byte)':') return false;
            foreach (byte b in name) if (b < 0x20 || b > 0x7E) return false;

            int version = offset + 5 + nameSize;
            if (!records.Slice(version, 8).SequenceEqual(records.Slice(version + 8, 8)))
                return false;

            offset += record;
            count++;
        }
        return count != 0;
    }

    /// <summary>The repair this file needs, or null for "not a SELF, or nothing to do".</summary>
    internal static Plan? PlanFor(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                                          FileOptions.SequentialScan);
        long size = stream.Length;
        if (size < 0x20) return null;

        var header = new byte[0x20];
        stream.ReadExactly(header);
        bool prospero = header.AsSpan(0, 4).SequenceEqual(ProsperoMagic);
        bool legacy = header.AsSpan(0, 4).SequenceEqual(LegacyMagic);
        if (!prospero && !legacy) return null;

        Plan? MagicOnly() => legacy ? new Plan(true, size, 0) : null;

        long boundary = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x10));
        if (boundary < 0x20 || boundary > size) return MagicOnly();

        long start = Math.Max(0, boundary - Window);
        long tailLength = size - start;
        if (tailLength <= 0 || tailLength > MaxMetadata) return MagicOnly();

        var tail = new byte[tailLength];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(tail);

        int local = (int)(boundary - start);
        for (int backtrack = 0; backtrack <= Math.Min(Window, local); backtrack++)
            if (IsCompleteSceVersion(tail.AsSpan(local - backtrack)))
                return legacy || backtrack > 0
                    ? new Plan(legacy, boundary - backtrack, backtrack)
                    : null;

        return MagicOnly();
    }

    /// <summary>Writes the repaired file. The trailer is copied verbatim, only moved.</summary>
    internal static void Write(string source, string destination, Plan plan)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                                         1 << 20, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                                          FileShare.None, 1 << 20, FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        long remaining = plan.TrailerOffset;
        bool first = true;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int got = input.Read(buffer, 0, want);
            if (got == 0) throw new EndOfStreamException($"unexpected EOF while normalizing {source}");
            if (first && plan.RewriteMagic) ProsperoMagic.CopyTo(buffer);
            first = false;
            output.Write(buffer, 0, got);
            remaining -= got;
        }
        if (plan.Padding > 0) output.Write(new byte[plan.Padding]);
        input.CopyTo(output, 1 << 20);
    }
}
