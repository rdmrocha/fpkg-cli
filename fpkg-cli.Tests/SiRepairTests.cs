using Fpkg.Cli.RepairPlayGo;
using LibProsperoPkg.PlayGo;
using Xunit;

public class SiRepairTests
{
    private static readonly string Passcode = new string('0', 32);

    /// <summary>The CRC pass's log lines are not under test here; discard them.</summary>
    private static Progress Silent =>
        new(TextWriter.Null, verbose: false, isTty: false, totalStages: 1);

    /// <summary>
    /// FIXED POINT for the SI: rebuilding the zip from its own members, with the ORIGINAL
    /// chunk.dat and the ORIGINAL mount image, must reproduce it byte for byte. This proves
    /// BuildMembers/WriteZip reconstruct the same container before any content changes.
    /// </summary>
    [SkippableFact]
    public void RebuildingTheSiFromItsOwnMembersIsAFixedPoint()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var members = SiRepair.ReadMembers(r.Si);
        Assert.False(members.ContainsKey("common/etc/pfsimage.xml"));

        using var mount = File.OpenRead(TestPackage.Path);
        var got = SiRepair.Rebuild(r.Si, TestPackage.ContentId,
                                   members["common/etc/playgo-chunk.dat"],
                                   mount, r.CntOffset + r.Cnt.Length, Silent);
        Bytes.AssertEqual(r.Si, got);
    }

    /// <summary>
    /// The CRC table's length rule is the END OF THE CNT REGION, not the end of the CNT body.
    /// The last block is a partial read, so the length changes the last entry's value too.
    /// </summary>
    [SkippableFact]
    public void TheCrcTableLengthRuleIsTheRegionEnd()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var stored = SiRepair.ReadMembers(r.Si)[$"config/{TestPackage.ContentId}/playgo-chunk.crc"];
        Assert.Equal(40_304, stored.Length);                       // 10,076 entries
        Assert.Equal(660_340_736, r.CntOffset + r.Cnt.Length);     // ceil(/65536) == 10,076

        using var mount = File.OpenRead(TestPackage.Path);
        var built = ProsperoPlayGo.BuildChunkCrc(mount, r.CntOffset + r.Cnt.Length);
        Bytes.AssertEqual(stored, built);

        // The 8,192-shorter candidate must NOT match, or the rule is underdetermined.
        mount.Position = 0;
        var shorter = ProsperoPlayGo.BuildChunkCrc(
            mount, r.CntOffset + (long)CntHeader.U64(r.Cnt, CntHeader.BodySize));
        Assert.NotEqual(Convert.ToHexString(stored), Convert.ToHexString(shorter));
    }

    /// <summary>FIXED POINT 2, SI half.</summary>
    [SkippableFact]
    public void TheRepairedSiEqualsTheOracles()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var orig = PackageRegions.Load(TestPackage.Path);
        var want = PackageRegions.Load(Oracle.Path).Si;

        var repaired = CntRepair.Repair(orig.Cnt, TestPackage.ContentId, Passcode);

        // The CRC covers the repaired mount image, so it has to be spliced first.
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            orig.WriteTo(tmp, repaired.Cnt, []);
            using var mount = File.OpenRead(tmp);
            var got = SiRepair.Rebuild(orig.Si, TestPackage.ContentId, repaired.NewChunkDat,
                                       mount, orig.CntOffset + repaired.Cnt.Length, Silent);

            // Localise before comparing the whole zip: entry count first, then the prefix the
            // spec says cannot move, then everything.
            var wantCrc = SiRepair.ReadMembers(want)[$"config/{TestPackage.ContentId}/playgo-chunk.crc"];
            var gotCrc  = SiRepair.ReadMembers(got)[$"config/{TestPackage.ContentId}/playgo-chunk.crc"];
            var origCrc = SiRepair.ReadMembers(orig.Si)[$"config/{TestPackage.ContentId}/playgo-chunk.crc"];
            Assert.Equal(origCrc.Length, gotCrc.Length);                 // body_size unchanged
            // Derived, not magic: one 4-byte CRC per 64-KiB block, and every block that lies
            // wholly BEFORE the CNT region covers payload bytes the repair copies through
            // verbatim. Only the CNT tail can move, so the prefix must be identical.
            int blocksBeforeCnt = (int)(orig.CntOffset / 65536);
            Assert.Equal(9106, blocksBeforeCnt);
            Assert.Equal(Convert.ToHexString(origCrc.AsSpan(0, blocksBeforeCnt * 4)),
                         Convert.ToHexString(gotCrc.AsSpan(0, blocksBeforeCnt * 4)));
            Bytes.AssertEqual(wantCrc, gotCrc);
            Bytes.AssertEqual(want, got);
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>
    /// The FIRST naps_meta_3xx path was read with a raw indexer before the loop that has a proper
    /// message for it, so a package missing only that member died on a bare
    /// <c>KeyNotFoundException</c> — which the CLI turns into an `error:` line naming nothing — while
    /// a package missing any of the other three got a message that says which. The two must agree.
    ///
    /// <para>
    /// Asserted over <see cref="SiRepair.EnsureRebuildable"/> rather than <c>Rebuild</c> so it needs
    /// no package and no CRC pass: the refusal is a property of the member set alone, which is
    /// exactly why the command can now ask it before writing anything.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryMissingNapsMetaMemberIsNamedInTheRefusal()
    {
        var complete = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SiRepair.NapsMeta18Path] = [7, 7, 7],
        };
        foreach (var path in SiRepair.NapsMeta300Paths)
            complete[path] = [1, 2, 3];
        SiRepair.EnsureRebuildable(complete);   // the baseline: this set is acceptable

        foreach (var missing in SiRepair.NapsMeta300Paths.Append(SiRepair.NapsMeta18Path))
        {
            var members = complete.Where(m => m.Key != missing)
                                  .ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
            var ex = Assert.Throws<InvalidDataException>(() => SiRepair.EnsureRebuildable(members));
            Assert.Contains(missing, ex.Message);
        }

        // And the identity check the single-blob rebuild rests on.
        var differing = new Dictionary<string, byte[]>(complete, StringComparer.Ordinal);
        differing[SiRepair.NapsMeta300Paths[2]] = [1, 2, 4];
        Assert.Contains("not", Assert.Throws<InvalidDataException>(
            () => SiRepair.EnsureRebuildable(differing)).Message);
    }
}
