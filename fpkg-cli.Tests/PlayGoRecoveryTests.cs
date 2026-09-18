using System.Buffers.Binary;
using Fpkg.Cli.RepairPlayGo;
using Xunit;

public class PlayGoRecoveryTests
{
    [SkippableFact]
    public void RecoversTheMeasuredValues()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        // This package's passcode happens to be 32 zeros — not a property of the format.
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, new string('0', 32));
        var r = PlayGoRecovery.From(t[4097].Payload);

        Assert.Equal(100, r.ChunkCount);
        Assert.Equal(1,   r.ScenarioCount);
        Assert.Equal(0,   r.DefaultScenarioId);
        Assert.Equal(1,   r.DefaultLanguageId);
        Assert.Equal(101, r.ExtentCount);
        Assert.Equal(0xFFFFFFFFFFFFFFFFul, r.LanguageMask);
        Assert.Equal(0x23920000ul, r.TotalSize);
        Assert.Equal(0x23830000ul, r.DataSize);
        Assert.Equal(0xF0000ul,    r.TailSize);
        Assert.Equal(["Scenario #0"], r.ScenarioLabels);
        Assert.Equal(TestPackage.ContentId, r.ContentId);
    }

    [Fact]
    public void BufferShorterThanMinimumThrows()
    {
        // No package needed: this guard fires before anything package-specific is read.
        var tooShort = new byte[200];
        Assert.Throws<InvalidDataException>(() => PlayGoRecovery.From(tooShort));
    }

    /// <summary>
    /// Returns a mutable copy of the real playgo-chunk.dat payload, so each guard test can
    /// corrupt exactly one field and differ from a known-valid buffer in one respect only.
    /// </summary>
    private static byte[] ValidPayload()
    {
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, new string('0', 32));
        return (byte[])t[4097].Payload.Clone();
    }

    [SkippableFact]
    public void ExtentSectionOffsetPastEndThrows()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var d = ValidPayload();
        // Point the extent section (u32 @ 216) well past the end of the buffer.
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(216), (uint)(d.Length + 1000));
        Assert.Throws<InvalidDataException>(() => PlayGoRecovery.From(d));
    }

    [SkippableFact]
    public void LabelSectionLengthPastEndThrows()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var d = ValidPayload();
        // Keep the label offset (u32 @ 240) valid but blow out the length (u32 @ 244).
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(244), 0x7FFFFFFF);
        Assert.Throws<InvalidDataException>(() => PlayGoRecovery.From(d));
    }

    [SkippableFact]
    public void LabelSectionOffsetPastEndThrows()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var d = ValidPayload();
        // Keep the label length (u32 @ 244) valid but point the offset (u32 @ 240) past the end.
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(240), (uint)(d.Length + 1000));
        Assert.Throws<InvalidDataException>(() => PlayGoRecovery.From(d));
    }

    [SkippableFact]
    public void NegativeExtentCountIsRejected()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var d = ValidPayload();
        // u32 @ 32, read into a signed int: 0xFFFFFFFF becomes -1.
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(32), 0xFFFFFFFF);
        Assert.Throws<InvalidDataException>(() => PlayGoRecovery.From(d));
    }

    [SkippableFact]
    public void AbsurdExtentCountIsRejectedRatherThanReadOutOfBounds()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var d = ValidPayload();
        // A huge but still-positive-as-int extent count: the extent section offset stays
        // valid on its own, but offset + count * 16 must still be caught before the read loop
        // walks off the end of the buffer.
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(32), 0x7FFFFFFF);
        Assert.Throws<InvalidDataException>(() => PlayGoRecovery.From(d));
    }
}
