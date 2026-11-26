using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Fpkg.Cli.PlayGo;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;

namespace Fpkg.Cli.MakePatch;

/// <summary>
/// Turns a target package built by make-patch into the target of a patch against its base, in
/// place, then writes the LIH compact patch. Every structure here was derived from Sony's own
/// patch for PRAGMATA (see NOTES-make-patch.md); the field comments say what each one holds.
///
/// <para>Relocation is computed from the two packages' imagedigs.dat tables (SHA3 of every
/// plaintext outer-PFS block), so no image data is read: target block i (outer block i-1) is the
/// base block whose digest matches, else new. That holds only for plaintext/no-auth images,
/// where the on-disk block IS the plaintext block.</para>
/// </summary>
internal static class PatchFinalize
{
    private const int Block = 0x10000;
    private const uint New = 0xFFFFFFFF;
    private const uint RlcDate = 0x20221019;
    private const string Passcode = "00000000000000000000000000000000";

    private sealed record Package(string Path, byte[] Fih, long CntOffset, byte[] Cnt, CntEntryTable Table)
    {
        public long CntBlocks => CntOffset / Block;
        public byte[] Game => Fih.AsSpan(0x30, 32).ToArray();
        public byte[] Payload(uint id) => Table[id].Payload;
    }

    private static Package Read(string path)
    {
        using var fs = File.OpenRead(path);
        var fih = new byte[Block];
        fs.ReadExactly(fih);
        long cntOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x58));
        var head = new byte[0x1000];
        fs.Position = cntOffset;
        fs.ReadExactly(head);
        long cntLength = (long)(CntHeader.U64(head, CntHeader.BodyOffset) + CntHeader.U64(head, CntHeader.BodySize));
        var cnt = new byte[cntLength];
        fs.Position = cntOffset;
        fs.ReadExactly(cnt);
        return new Package(path, fih, cntOffset, cnt, CntEntryTable.Parse(cnt, Passcode));
    }

    internal sealed record Result(uint[] Origin, int Reused, int NewBlocks, long TargetBlocks);

    /// <param name="appendStartBlock">First pfs_image.dat block of the region make-patch wrote
    /// fresh (the changed files, after the compacted base data). As in Sony's patches, that region
    /// is shipped whole — every block in it is new — and naps_meta_301/302 describe it.</param>
    internal static Result Finalize(string targetPath, string basePath, IReadOnlyList<long> liveBlocks)
    {
        long appendStartBlock = liveBlocks.Count;
        var b = Read(basePath);
        var t = Read(targetPath);
        long n = t.CntBlocks;
        long appendEndBlock = (BaseImage.Open(targetPath, Passcode).DataEnd + Block - 1) / Block;

        // ---- relocation from imagedigs ----------------------------------------------------
        byte[] tDigs = t.Payload(1034), bDigs = b.Payload(1034);
        if (tDigs.Length / 32 != n - 1 || bDigs.Length / 32 != b.CntBlocks - 1)
            throw new InvalidDataException("imagedigs.dat does not cover exactly the outer PFS blocks");
        // The compacted prefix is the base's live blocks in order (checked against both digest
        // tables); everything after it — the changed files make-patch wrote, the new metadata,
        // and the FIH — is new, exactly the shape of Sony's patches.
        var origin = new uint[n];
        origin[0] = New;
        for (long i = 1; i < n; i++)
        {
            long j = i - 1;
            if (j < liveBlocks.Count)
            {
                long k = liveBlocks[(int)j];
                if (!tDigs.AsSpan((int)j * 32, 32).SequenceEqual(bDigs.AsSpan((int)k * 32, 32)))
                    throw new InvalidDataException($"target block {i} should be base block {k + 1}, but their image digests differ");
                origin[i] = (uint)(k + 1);
            }
            else origin[i] = New;
        }
        int reused = origin.Count(v => v != New);
        int fresh = (int)n - reused;
        var update = new uint[n];
        var gathered = new uint[n];
        uint patchBlock = 1;                                    // patch block 0 is the LIH header
        for (long i = 0; i < n; i++)
        {
            if (origin[i] == New) { update[i] = 0x10000000u | patchBlock; gathered[i] = patchBlock; patchBlock++; }
            else { update[i] = origin[i]; gathered[i] = New; }
        }

        // ---- versions and digests ------------------------------------------------------------
        uint tVer = VersionCode(t.Payload(8192)), bVer = VersionCode(b.Payload(8192));
        uint tInner = BinaryPrimitives.ReadUInt32LittleEndian(t.Fih.AsSpan(0x90));
        byte[] bGame = b.Game, tGame = t.Game;

        byte[] Rlc(uint type, uint[] entries)
        {
            var d = new byte[0x1000 + entries.Length * 4];
            var s = d.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s, 0x434C527F);
            W(s, 0x04, 0x10000 | type); W(s, 0x10, Block); W(s, 0x14, (uint)entries.Length);
            W(s, 0x18, (uint)d.Length); W(s, 0x1C, RlcDate); W(s, 0x20, 0x1000); W(s, 0x24, (uint)entries.Length * 4);
            switch (type)
            {
                case 0: W(s, 0x30, 1); W(s, 0x34, 1); W(s, 0x38, (uint)reused); W(s, 0x3C, tInner); break;
                case 1: W(s, 0x28, (uint)fresh + 1); break;
                case 2: W(s, 0x2C, 2); W(s, 0xC0, (uint)reused); W(s, 0xC4, (uint)fresh); break;
            }
            bGame.CopyTo(s[0xA0..]);
            W(s, 0x100, tVer); W(s, 0x104, (uint)n);
            tGame.CopyTo(s[0x120..]);
            if (type != 1) { W(s, 0x140, bVer); W(s, 0x144, (uint)b.CntBlocks); bGame.CopyTo(s[0x160..]); }
            for (int i = 0; i < entries.Length; i++) W(s, 0x1000 + i * 4, entries[i]);
            return d;
        }
        byte[] originRlc = Rlc(0, origin), updateRlc = Rlc(2, update), gatheredRlc = Rlc(1, gathered);
        byte[] ris = RelocInfoSet(originRlc, origin, tVer, bVer, (uint)n, tGame, bGame);

        // ---- CNT: entries, header, GeneralDigests; FIH reference digest -------------------
        var fih = (byte[])t.Fih.Clone();
        bGame.CopyTo(fih, 0xD0);
        var cnt = (byte[])t.Cnt.Clone();
        // content_flags and ekc_version follow the base (Sony: base flags | SUBSEQUENT_PATCH|REMASTER).
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(b.Cnt.AsSpan(0x78));
        BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(0x78), flags | 0x40400000u);
        b.Cnt.AsSpan(0x9C, 4).CopyTo(cnt.AsSpan(0x9C));
        BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(0x88), (uint)reused & 0xFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(0x8C), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(0x90), (uint)reused);
        BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(0x94), (uint)reused);
        ProsperoImageDigests.Sha3_256(fih).CopyTo(cnt, 0x460);  // pfs_signed_digest = SHA3(FIH block)

        var table = CntEntryTable.Parse(cnt, Passcode);
        table[1035].Payload = originRlc[..0x1000];
        table[1036].Payload = originRlc[..0x1000];
        foreach (uint id in new uint[] { 1035, 1036, 8448, 8449 }) table[id].Flags1 = 0;
        table[8192].Payload = PatchParam(table[8192].Payload, b.Payload(8192));
        byte[] bGd = b.Payload(128);
        byte[] bContent = bGd.AsSpan(0x20, 32).ToArray();
        byte[] AdjustGd(byte[] gd)
        {
            gd = (byte[])gd.Clone();
            uint mask = BinaryPrimitives.ReadUInt32BigEndian(gd.AsSpan(0x1C));
            // Slot +0x100 (mask bit 0x100) is unchanged between a base and its Sony patch; this
            // library never fills it, so carry the base's when it has one.
            uint baseMask = BinaryPrimitives.ReadUInt32BigEndian(bGd.AsSpan(0x1C));
            if ((baseMask & 0x100) != 0 && gd.AsSpan(0x100, 32).IndexOfAnyExcept((byte)0) < 0)
            {
                bGd.AsSpan(0x100, 32).CopyTo(gd.AsSpan(0x100));
                mask |= 0x100;
            }
            BinaryPrimitives.WriteUInt32BigEndian(gd.AsSpan(0x1C), mask | 0x2C00);
            bContent.CopyTo(gd, 0x140);
            bContent.CopyTo(gd, 0x160);
            bGame.CopyTo(gd, 0x180);
            bGame.CopyTo(gd, 0x1A0);
            return gd;
        }
        var sealedCnt = CntReseal.Seal(cnt, table.Physical, CntHeader.ReadContentId(cnt), Passcode,
                                       null, AdjustGd);
        if (sealedCnt.Length != cnt.Length)
            throw new InvalidDataException($"the resealed CNT changed size ({cnt.Length} -> {sealedCnt.Length})");

        // ---- SI ------------------------------------------------------------------------------
        string cid = CntHeader.ReadContentId(cnt).TrimEnd('\0');
        var members = ReadSi(targetPath, out long siStart);
        if (siStart != t.CntOffset + t.Cnt.Length)
            throw new InvalidDataException("the SI does not immediately follow the CNT");
        var baseSi = ReadSi(basePath, out _);
        void Put(string path, byte[] data) => members[path] = data;
        Put("common/origin-relocinfo.dat", originRlc);
        Put("common/target-relocinfo.dat", originRlc);
        Put("common/update-relocinfo.dat", updateRlc);
        Put("common/gathered-relocinfo.dat", gatheredRlc);
        Put("common/etc/relocinfoset.dat", ris);
        Put("common/etc/origin-playgo-chunk.dat", baseSi["common/etc/playgo-chunk.dat"]);
        Put("common/etc/target-playgo-chunk.dat", baseSi["common/etc/playgo-chunk.dat"]);  // sic: Sony's is the base's too
        // naps_meta_301/302: the target pfs_image.dat's new-data run; 304: the base pfs_image.dat
        // block runs the target no longer uses (type 1001 for the base's inner metadata).
        {
            long tPfs = BinaryPrimitives.ReadUInt32LittleEndian(t.Fih.AsSpan(0x90));
            long bPfs = BinaryPrimitives.ReadUInt32LittleEndian(b.Fih.AsSpan(0x90));
            long tDataEnd = tPfs * Block - 0x20000;
            long newStart = appendStartBlock, newSize = Math.Max(0, appendEndBlock - appendStartBlock);
            byte[] Meta3(ulong a1, ulong a2, ulong a4)
            {
                var m = new byte[48];
                BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(8), a1);
                BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(16), a2);
                BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(24), 1001);
                BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(32), a4);
                BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(40), 0x20000);
                return m;
            }
            Put("common/etc/naps_meta_301.dat", Meta3(0, (ulong)(newSize * Block), (ulong)(newSize * Block)));
            Put("common/etc/naps_meta_302.dat", Meta3((ulong)(newStart * Block), (ulong)(newSize * Block), (ulong)tDataEnd));
            var usedBase = new bool[bPfs];
            foreach (var v in origin) if (v != New && v >= 1 && v - 1 < bPfs) usedBase[v - 1] = true;
            long bDataEndBlock = (BaseImage.Open(basePath, Passcode).DataEnd + Block - 1) / Block;
            var runs = new List<(uint Type, uint Start, uint Count)>();
            for (long k = 0; k < bPfs;)
            {
                if (usedBase[k]) { k++; continue; }
                long e = k;
                bool meta = k >= bDataEndBlock;
                while (e < bPfs && !usedBase[e] && (e >= bDataEndBlock) == meta) e++;
                runs.Add((meta ? 1001u : 0u, (uint)k, (uint)(e - k)));
                k = e;
            }
            var r304 = new byte[runs.Count * 12];
            for (int i = 0; i < runs.Count; i++)
            {
                W(r304, i * 12, runs[i].Type); W(r304, i * 12 + 4, runs[i].Start); W(r304, i * 12 + 8, runs[i].Count);
            }
            Put("common/etc/naps_meta_304.dat", r304);
        }
        Put($"config/{cid}/origin-playgo-scenario.json", b.Payload(12288));
        Put($"config/{cid}/target-playgo-scenario.json", b.Payload(12288));   // sic: the base's, as Sony's
        Put($"config/{cid}/playgo-scenario.json", t.Payload(12288));

        // ---- write target: FIH, CNT, SI (+ chunk CRCs for the blocks that changed) ---------
        using (var fs = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = 0; fs.Write(fih);
            fs.Position = t.CntOffset; fs.Write(sealedCnt);
            string crcName = $"config/{cid}/playgo-chunk.crc";
            if (members.TryGetValue(crcName, out var crc))
            {
                crc = (byte[])crc.Clone();
                long end = t.CntOffset + sealedCnt.Length;
                foreach (long k in new[] { 0L }.Concat(Enumerable.Range((int)(t.CntOffset / Block), (int)((end - t.CntOffset + Block - 1) / Block)).Select(x => (long)x)))
                {
                    long off = k * Block;
                    int len = (int)Math.Min(Block, end - off);
                    var buf = new byte[len];
                    fs.Position = off; fs.ReadExactly(buf);
                    BinaryPrimitives.WriteUInt32LittleEndian(crc.AsSpan((int)k * 4), Crc32C(buf));
                }
                members[crcName] = crc;
            }
            var zip = ProsperoSiArchive.WriteZip(members.OrderBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => new ProsperoSiMember(m.Key, m.Value)).ToList());
            fs.Position = siStart;
            fs.Write(zip);
            fs.SetLength(siStart + zip.Length);
        }
        return new Result(origin, reused, fresh, n);
    }

    /// <summary>Writes the LIH compact patch: header block, target FIH, new blocks, CNT, SI.</summary>
    internal static void WriteLih(string targetPath, uint[] origin, string lihPath)
    {
        using var t = File.OpenRead(targetPath);
        var fihBlock = new byte[Block];
        t.ReadExactly(fihBlock);
        long cntOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fihBlock.AsSpan(0x58));
        long tail = t.Length - cntOffset;
        long siStart = BaseImage.SiStart(t);
        long scLen = siStart - cntOffset, siLen = t.Length - siStart;
        var newBlocks = Enumerable.Range(1, origin.Length - 1).Where(i => origin[i] == New).ToList();
        long patchBlocks = 2 + newBlocks.Count;                  // header + FIH + new
        long scOff = patchBlocks * Block;
        long total = scOff + tail;

        using var o = new FileStream(lihPath, FileMode.CreateNew, FileAccess.Write);
        var head = new byte[Block];
        var h = head.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x4849_4C7F);       // "\x7fLIH"
        W(h, 0x04, 0x10000); W(h, 0x10, Block); W(h, 0x14, (uint)patchBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x18..], (ulong)total);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x20..], Block);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x28..], Block);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x30..], (ulong)scOff);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x38..], (ulong)scLen);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x40..], (ulong)(scOff + scLen));
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x48..], (ulong)siLen);
        o.Write(head);
        o.Write(fihBlock);
        var buf = new byte[Block];
        foreach (int i in newBlocks)
        {
            t.Position = (long)i * Block;
            t.ReadExactly(buf);
            o.Write(buf);
        }
        t.Position = cntOffset;
        t.CopyTo(o);
    }

    private static Dictionary<string, byte[]> ReadSi(string path, out long siStart)
    {
        using var fs = File.OpenRead(path);
        siStart = BaseImage.SiStart(fs);
        using var zip = new ZipArchive(new BaseImage.LihSubStream(fs, siStart, fs.Length - siStart), ZipArchiveMode.Read);
        var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            d[e.FullName] = ms.ToArray();
        }
        return d;
    }

    /// <summary>relocinfoset.dat: header, s1 (versions + origin table digest), s2 (origin table,
    /// padded to 16), s3 ("\x7fbmp": three per-block bitmaps, the first marking base blocks).</summary>
    private static byte[] RelocInfoSet(byte[] originRlc, uint[] origin, uint tVer, uint bVer, uint n,
                                       byte[] tGame, byte[] bGame)
    {
        var s1 = new byte[0x40];
        W(s1, 0, tVer); W(s1, 4, bVer);
        BinaryPrimitives.WriteUInt64LittleEndian(s1.AsSpan(0x10), 0x440);
        BinaryPrimitives.WriteUInt64LittleEndian(s1.AsSpan(0x18), (ulong)originRlc.Length);
        ProsperoImageDigests.Sha3_256(originRlc).CopyTo(s1, 0x20);
        int s2Len = (originRlc.Length + 15) & ~15;
        int bmBytes = (int)(n / 32 + 1) * 4;                     // Sony: n/32 + 1 words
        var s3 = new byte[8 + 3 * (8 + bmBytes)];
        W(s3, 0, 0x706D627F); W(s3, 4, 3);
        for (int k = 0; k < 3; k++)
        {
            int at = 8 + k * (8 + bmBytes);
            W(s3, at, 0x7469627F); W(s3, at + 4, n);
            if (k == 0)
                for (int i = 0; i < n; i++)
                    if (origin[i] != New) s3[at + 8 + (i >> 3)] |= (byte)(1 << (i & 7));
        }
        int s3Off = 0x440 + s2Len;
        var d = new byte[s3Off + s3.Length];
        W(d, 0, 0x5349527F); W(d, 4, 0x10000); W(d, 0x10, 0x10000); W(d, 0x14, 1); W(d, 0x1C, RlcDate);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x20), (ulong)d.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x28), 0x400);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x30), 0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x38), 0x440);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x40), (ulong)s2Len);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x48), (ulong)s3Off);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x50), (ulong)s3.Length);
        W(d, 0x80, tVer); W(d, 0x84, n);
        tGame.CopyTo(d, 0xA0);
        bGame.CopyTo(d, 0xC0);
        ProsperoImageDigests.Sha3_256(s1).CopyTo(d, 0xE0);
        ProsperoImageDigests.Sha3_256(s3).CopyTo(d, 0x100);
        s1.CopyTo(d, 0x400);
        originRlc.CopyTo(d, 0x440);
        s3.CopyTo(d, s3Off);
        return d;
    }

    /// <summary>
    /// The builder strips originContentVersion/targetContentVersion (it only makes full
    /// applications); a patch carries both, as the base's version, and a real creation date.
    /// Re-serialised the builder's way: keys sorted ordinally, indented, relaxed escaping.
    /// </summary>
    private static byte[] PatchParam(byte[] target, byte[] baseParam)
    {
        var p = JsonNode.Parse(target)!.AsObject();
        string baseVersion = JsonNode.Parse(baseParam)!["contentVersion"]!.GetValue<string>();
        p["originContentVersion"] = baseVersion;
        p["targetContentVersion"] = baseVersion;
        if (p["pubtools"] is JsonObject pub)
            pub["creationDate"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        static JsonNode? Sort(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                            .Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value?.DeepClone())))),
            JsonArray a => new JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
            _ => n?.DeepClone(),
        };
        string text = Sort(p)!.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return System.Text.Encoding.UTF8.GetBytes(text);
    }

    /// <summary>"01.300.001" -> 0x01300001: the version's digits read as hex.</summary>
    internal static uint VersionCode(byte[] paramJson)
    {
        string v = JsonNode.Parse(paramJson)?["contentVersion"]?.GetValue<string>()
                   ?? throw new InvalidDataException("param.json has no contentVersion");
        return Convert.ToUInt32(new string(v.Where(char.IsDigit).ToArray()), 16);
    }

    private static uint Crc32C(ReadOnlySpan<byte> data) => ProsperoCrc32C.Compute(data);

    private static void W(Span<byte> s, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s[off..], v);
    private static void W(byte[] s, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(off), v);
}
