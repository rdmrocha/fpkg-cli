using Fpkg.Cli.RepairPlayGo;
using LibProsperoPkg.PKG;
using Xunit;

public class CntResealTests
{
    /// <summary>This package's passcode happens to be 32 zeros — not a property of the format.</summary>
    private static readonly string Passcode = new('0', 32);

    /// <summary>
    /// FIXED POINT 1. Reseal with nothing changed. If the output is byte-identical to the input
    /// then every digest, offset and header field in this reimplementation matches what
    /// LibProsperoPkg itself produced. This is the total check the design rests on; if it ever
    /// regresses, nothing downstream can be trusted.
    /// </summary>
    [SkippableFact]
    public void ResealingAnUnchangedPackageIsAFixedPoint()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var t = CntEntryTable.Parse(r.Cnt, Passcode);
        var contentId = ProsperoPkgReader.Read(TestPackage.Path).Header.ContentId;

        var resealed = CntReseal.Seal(r.Cnt, t.Physical, contentId, new string('0', 32));

        // Staged, cheapest and most-localising first. A bare whole-buffer compare reports an
        // offset; these report the STAGE, which is what you actually need at 3am.
        Region("header",          r.Cnt, resealed, 0, 0x2000);
        Region("meta table",      r.Cnt, resealed,
               (int)CntHeader.U32(r.Cnt, CntHeader.EntryTableOffset), 27 * 32);
        Entry ("DIGESTS",         r.Cnt, resealed, t, 1);
        Entry ("GENERAL_DIGESTS", r.Cnt, resealed, t, 128);
        Entry ("METAS",           r.Cnt, resealed, t, 256);
        Region("body",            r.Cnt, resealed,
               (int)CntHeader.U64(r.Cnt, CntHeader.BodyOffset),
               (int)CntHeader.U64(r.Cnt, CntHeader.BodySize));
        Bytes.AssertEqual(r.Cnt, resealed);
    }

    /// <summary>
    /// FIXED POINT 3. The one above proves the digest chain but exercises NO entry movement —
    /// every offset it computes is the one already on disk. The repair shifts everything after
    /// entry 4097 down by 1360 and then the last entry up by 312, and nothing else between the
    /// two gates tests relayout on its own. So: grow a late entry, reseal, shrink it back,
    /// reseal again, and require the round trip to land on the original CNT. Needs no oracle,
    /// and fails HERE rather than as a mystery byte mismatch in Task 9.
    /// </summary>
    [SkippableFact]
    public void RelayoutRoundTripsThroughAChangedEntryLength()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var contentId = ProsperoPkgReader.Read(TestPackage.Path).Header.ContentId;
        var passcode = new string('0', 32);

        // playgo-scenario.json (12288) is last in physical order; pad it so everything after it
        // in the body — nothing — moves, then pick a mid-list entry so plenty does.
        var grown = CntEntryTable.Parse(r.Cnt, Passcode);
        var target = grown[4097];                       // physical index 15 of 27
        var original = target.Payload;
        target.Payload = [.. original, .. new byte[1000]];
        target.DataSize = (uint)target.Payload.Length;

        var bigger = CntReseal.Seal(r.Cnt, grown.Physical, contentId, passcode);
        Assert.NotEqual(Convert.ToHexString(r.Cnt), Convert.ToHexString(bigger));

        var shrunk = CntEntryTable.Parse(bigger, Passcode);
        shrunk[4097].Payload = original;
        shrunk[4097].DataSize = (uint)original.Length;
        var back = CntReseal.Seal(bigger, shrunk.Physical, contentId, passcode);

        Bytes.AssertEqual(r.Cnt, back);
    }

    /// <summary>
    /// The ONLY coverage of re-encryption at a new offset. An encrypted entry's AES key and IV are
    /// derived from its 32-byte meta record, which contains DataOffset — so an entry that moves
    /// must be re-encrypted, never copied. Fixed point 3 cannot show this: all five encrypted
    /// entries sit physically ahead of 4097, so nothing there shifts them, and the real repair
    /// never will either. Entry 8192 (param.json) is seventh in physical order, ahead of all five,
    /// so perturbing it drags every encrypted entry to a new offset. The test asserts that
    /// movement explicitly, so it cannot silently stop exercising the path it exists for.
    /// </summary>
    [SkippableFact]
    public void MovingAnEntryReEncryptsTheEncryptedEntriesAtTheirNewOffsets()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var contentId = ProsperoPkgReader.Read(TestPackage.Path).Header.ContentId;

        var grown = CntEntryTable.Parse(r.Cnt, Passcode);
        var encrypted = grown.Physical.Where(e => e.Encrypted).ToList();
        Assert.NotEmpty(encrypted);
        var offsetsBefore = encrypted.Select(e => e.DataOffset).ToList();

        var target = grown[8192];
        var original = target.Payload;
        target.Payload = [.. original, .. new byte[1000]];

        var bigger = CntReseal.Seal(r.Cnt, grown.Physical, contentId, Passcode);

        // The whole point of this test: at least one entry whose ciphertext depends on its offset
        // actually landed somewhere else, so the bytes below could only round-trip by being
        // re-encrypted against the new meta record.
        Assert.NotEqual(offsetsBefore, encrypted.Select(e => e.DataOffset).ToList());

        var shrunk = CntEntryTable.Parse(bigger, Passcode);
        shrunk[8192].Payload = original;
        var back = CntReseal.Seal(bigger, shrunk.Physical, contentId, Passcode);

        Bytes.AssertEqual(r.Cnt, back);
    }

    private static void Region(string stage, byte[] want, byte[] got, int off, int len)
    {
        for (int i = 0; i < len; i++)
            if (want[off + i] != got[off + i])
                Assert.Fail($"{stage}: first difference at 0x{off + i:X} (0x{i:X} into the region) — " +
                            $"expected 0x{want[off + i]:X2}, got 0x{got[off + i]:X2}");
    }

    private static void Entry(string stage, byte[] want, byte[] got, CntEntryTable t, uint id) =>
        Region(stage, want, got, (int)t[id].DataOffset, (int)t[id].DataSize);
}
