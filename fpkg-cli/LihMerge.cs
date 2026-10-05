using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;

namespace Fpkg.Cli;

/// <summary>
/// Rebuilds the full ("remastered") package from a base PKG and an LIH compact patch — the
/// <c>\x7fLIH</c> file Publishing Tools' <c>img_create --ref_pkg_path</c> writes, and the shape
/// backports ship in. Sony's own route is <c>prospero-pub-cmd img_convert --oformat remastered</c>;
/// this reproduces its output byte for byte (checked on a 34 GB title against that tool).
///
/// <para>
/// The merge is a pure block copy. Nothing is decrypted, recompressed or re-digested, because
/// the patch already carries the target's own FIH, CNT and SI; every 64 KiB block before the CNT
/// is one the target shares with either the base or the patch, and the patch says which:
/// </para>
/// <list type="bullet">
///   <item>The LIH header (0x50 bytes) gives the SC (CNT) and SI segments' offsets and lengths
///   as four u64 at 0x30, and the patch's own FIH at the u64 at 0x20.</item>
///   <item><c>common/update-relocinfo.dat</c> in the SI zip (<c>\x7fRLC</c>) holds one u32 per
///   target block — count at 0x14, entries at the u32 offset at 0x20, their byte length at 0x24.
///   Top nibble 0 is "base block N", 1 is "patch-file block N", N the low 28 bits.</item>
///   <item>After those blocks the target continues with the patch's SC and SI verbatim.</item>
/// </list>
/// </summary>
internal static class LihMerge
{
    internal const int BlockSize = 0x10000;
    internal static ReadOnlySpan<byte> LihMagic => "\u007fLIH"u8;
    internal static ReadOnlySpan<byte> FihMagic => "\u007fFIH"u8;
    internal static ReadOnlySpan<byte> RlcMagic => "\u007fRLC"u8;
    internal const string RelocEntry = "common/update-relocinfo.dat";

    // FIH fields used for the cross-checks. 0x30 is a package's own PFS-area digest and 0xD0 the
    // digest of the package a patch was built against (img_info's "PFS Area Digest (Reference
    // Package)"); 0x58 is the CNT's offset, which the library reports as EmbeddedCntOffset.
    private const int FihPfsDigest = 0x30, FihReferenceDigest = 0xD0, FihCntOffset = 0x58;
    private const int DigestLength = 32;

    /// <summary>Where one run of target blocks comes from.</summary>
    /// <param name="FromPatch">False for the base package, true for the patch file.</param>
    internal readonly record struct Run(bool FromPatch, long SourceBlock, long TargetBlock, int Blocks);

    internal sealed record Plan(
        long PatchLength, long FihOffset, long ScOffset, long ScLength, long SiOffset, long SiLength,
        long TargetBlocks, IReadOnlyList<Run> Runs, byte[] ReferenceDigest, long TargetCntOffset)
    {
        /// <summary>The block-copied part plus the SC and SI tail.</summary>
        public long OutputLength => TargetBlocks * BlockSize + ScLength + SiLength;
        public long PatchBlocks => Runs.Where(r => r.FromPatch).Sum(r => (long)r.Blocks);
        public long BaseBlocks => TargetBlocks - PatchBlocks;
    }

    internal static bool IsLih(string path)
    {
        using var f = File.OpenRead(path);
        Span<byte> m = stackalloc byte[4];
        return f.Read(m) == 4 && m.SequenceEqual(LihMagic);
    }

    /// <summary>Reads and validates everything the merge needs from the patch alone.</summary>
    internal static Plan Read(string patchPath)
    {
        using var pf = File.OpenRead(patchPath);
        long length = pf.Length;
        var lih = ReadExactly(pf, 0, 0x50, "LIH header");
        if (!lih.AsSpan(0, 4).SequenceEqual(LihMagic))
            throw new InvalidDataException("not an LIH compact patch (no \\x7fLIH magic)");

        long declared = U64(lih, 0x18), fih = U64(lih, 0x20);
        long scOff = U64(lih, 0x30), scLen = U64(lih, 0x38);
        long siOff = U64(lih, 0x40), siLen = U64(lih, 0x48);
        if (declared != length)
            throw new InvalidDataException(
                $"LIH header says the patch is {declared:N0} bytes but the file is {length:N0} — truncated or not this file's header");
        if (scOff % BlockSize != 0 || scOff + scLen != siOff || siOff + siLen != length || fih + BlockSize > scOff)
            throw new InvalidDataException(
                $"LIH segments do not tile the file (FIH 0x{fih:x}, SC 0x{scOff:x}+0x{scLen:x}, SI 0x{siOff:x}+0x{siLen:x}, file 0x{length:x})");

        var patchFih = ReadExactly(pf, fih, BlockSize, "patch FIH");
        if (!patchFih.AsSpan(0, 4).SequenceEqual(FihMagic))
            throw new InvalidDataException($"no FIH at 0x{fih:x} inside the patch");

        byte[] rlc;
        using (var si = new ZipArchive(new SubStream(pf, siOff, siLen), ZipArchiveMode.Read))
        {
            var entry = si.GetEntry(RelocEntry)
                ?? throw new InvalidDataException($"the patch's SI has no {RelocEntry} — not a patch this tool can merge");
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            rlc = ms.ToArray();
        }
        if (rlc.Length < 0x28 || !rlc.AsSpan(0, 4).SequenceEqual(RlcMagic))
            throw new InvalidDataException($"{RelocEntry} is not an RLC table");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(rlc.AsSpan(0x14));
        uint eoff = BinaryPrimitives.ReadUInt32LittleEndian(rlc.AsSpan(0x20));
        uint elen = BinaryPrimitives.ReadUInt32LittleEndian(rlc.AsSpan(0x24));
        if (count == 0 || elen != (ulong)count * 4 || (ulong)eoff + elen > (ulong)rlc.Length)
            throw new InvalidDataException(
                $"{RelocEntry}: {count} entries at 0x{eoff:x} (0x{elen:x} bytes) does not fit its {rlc.Length} bytes");

        long cntOffset = U64(patchFih, FihCntOffset);
        if (cntOffset != (long)count * BlockSize)
            throw new InvalidDataException(
                $"the target FIH puts its CNT at 0x{cntOffset:x} but {RelocEntry} maps {count} blocks (0x{(long)count * BlockSize:x})");

        long patchFullBlocks = length / BlockSize;
        var runs = new List<Run>();
        var entries = rlc.AsSpan((int)eoff, (int)elen);
        for (int i = 0; i < count; i++)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(entries[(i * 4)..]);
            uint kind = v >> 28, block = v & 0x0FFF_FFFF;
            if (kind > 1)
                throw new InvalidDataException($"{RelocEntry}: target block {i} has source kind {kind} (only base=0 and patch=1 are known)");
            bool fromPatch = kind == 1;
            if (fromPatch && block >= patchFullBlocks)
                throw new InvalidDataException($"{RelocEntry}: target block {i} reads patch block {block}, past the patch's end");
            if (runs.Count > 0 && runs[^1] is var last && last.FromPatch == fromPatch
                && last.SourceBlock + last.Blocks == block && last.Blocks < 256)
                runs[^1] = last with { Blocks = last.Blocks + 1 };
            else
                runs.Add(new Run(fromPatch, block, i, 1));
        }

        return new Plan(length, fih, scOff, scLen, siOff, siLen, count, runs,
                        patchFih.AsSpan(FihReferenceDigest, DigestLength).ToArray(), cntOffset);
    }

    /// <summary>
    /// Checks the base is the package the patch was built against, by the digest the patch
    /// records for it, and that it holds every block the plan reads.
    /// </summary>
    internal static void CheckBase(Plan plan, string basePath)
    {
        using var bf = File.OpenRead(basePath);
        var fih = ReadExactly(bf, 0, 0x100, "base FIH");
        if (fih.AsSpan(0, 4).SequenceEqual(LihMagic))
            throw new InvalidDataException("the base is itself an LIH compact patch; merge it with ITS base first");
        if (!fih.AsSpan(0, 4).SequenceEqual(FihMagic))
            throw new InvalidDataException("the base is not a PS5 PKG (no \\x7fFIH magic)");
        var digest = fih.AsSpan(FihPfsDigest, DigestLength);
        if (!digest.SequenceEqual(plan.ReferenceDigest))
            throw new InvalidDataException(
                "this is not the base the patch was built against: its PFS-area digest is " +
                $"{Convert.ToHexString(digest)}, the patch expects {Convert.ToHexString(plan.ReferenceDigest)}");
        long baseBlocks = bf.Length / BlockSize;
        foreach (var r in plan.Runs)
            if (!r.FromPatch && r.SourceBlock + r.Blocks > baseBlocks)
                throw new InvalidDataException(
                    $"the patch reads base block {r.SourceBlock + r.Blocks - 1}, past the base's end ({baseBlocks} full blocks)");
    }

    /// <summary>Writes the merged package. The caller owns overwrite policy and the final rename.</summary>
    internal static void Write(Plan plan, string basePath, string patchPath, string outPath,
                               Action<long, long>? progress = null, CancellationToken ct = default)
    {
        const FileOptions seq = FileOptions.SequentialScan;
        using var bf = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        using var pf = new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        using var of = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, seq);
        of.SetLength(plan.OutputLength);

        var buf = new byte[256 * BlockSize];
        long done = 0, total = plan.OutputLength, lastReport = 0;
        void Report()
        {
            if (progress is null || done - lastReport < (1L << 30) && done != total) return;
            lastReport = done;
            progress(done, total);
        }

        foreach (var r in plan.Runs)
        {
            ct.ThrowIfCancellationRequested();
            int n = r.Blocks * BlockSize;
            var src = r.FromPatch ? pf : bf;
            src.Position = r.SourceBlock * BlockSize;
            src.ReadExactly(buf, 0, n);
            of.Write(buf, 0, n);
            done += n;
            Report();
        }

        pf.Position = plan.ScOffset;
        for (long left = plan.ScLength + plan.SiLength; left > 0;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(left, buf.Length);
            pf.ReadExactly(buf, 0, n);
            of.Write(buf, 0, n);
            left -= n;
            done += n;
            Report();
        }
        Debug.Assert(of.Position == plan.OutputLength);
        of.Flush(flushToDisk: true);
    }

    private static long U64(byte[] b, int at) => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at)));

    private static byte[] ReadExactly(Stream s, long at, int n, string what)
    {
        if (at < 0 || at + n > s.Length)
            throw new InvalidDataException($"{what} at 0x{at:x} runs past the end of the file");
        var b = new byte[n];
        s.Position = at;
        s.ReadExactly(b);
        return b;
    }

    /// <summary>A read-only window onto part of a seekable stream, for the SI zip.</summary>
    private sealed class SubStream(Stream inner, long start, long length) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, length); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, length - _pos);
            if (n <= 0) return 0;
            inner.Position = start + _pos;
            n = inner.Read(buffer[..n]);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => length + offset,
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
