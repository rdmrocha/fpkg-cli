using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// Sites 13 and 14's decisions. Site 13 changes how licence material is stored in every package,
/// so the cases that matter are the ones where it must do NOTHING.
/// </summary>
public class CntEntryPolicyTests : IDisposable
{
    public CntEntryPolicyTests() { CntEntryPolicy.Reset(); CntEntryPolicy.Log = null; }
    public void Dispose() { CntEntryPolicy.Reset(); }

    private const uint Enc = CntEntryPolicy.EncryptedFlag;

    /// <summary>The five ids measured as plaintext in the Windows-built package.</summary>
    public static TheoryData<uint> LicenceIds => [1024u, 1025u, 1026u, 8224u, 8225u];

    [Theory, MemberData(nameof(LicenceIds))]
    public void ClearsTheEncryptionBitOnTheFiveLicenceEntries(uint id)
    {
        CntEntryPolicy.PlaintextLicenseEntries = true;
        Assert.Equal(0u, CntEntryPolicy.AdjustFlags1(Enc, id, "sce_sys/x"));
    }

    [Fact]
    public void DoesNothingWhenTheSwitchIsOff()
    {
        CntEntryPolicy.PlaintextLicenseEntries = false;
        Assert.Equal(Enc, CntEntryPolicy.AdjustFlags1(Enc, 1024, "sce_sys/license.dat"));
        Assert.False(CntEntryPolicy.ShouldSkipRightSprx());
    }

    /// <summary>
    /// Every other entry keeps whatever the library decided. 4736 and 4097 are the measured
    /// plaintext-by-policy ones and 4102 an arbitrary media entry; none may move either way.
    /// </summary>
    [Theory]
    [InlineData(4736u)] [InlineData(4097u)] [InlineData(4102u)] [InlineData(1023u)] [InlineData(8226u)]
    public void LeavesEveryOtherEntryAlone(uint id)
    {
        CntEntryPolicy.PlaintextLicenseEntries = true;
        Assert.Equal(Enc, CntEntryPolicy.AdjustFlags1(Enc, id, "sce_sys/x"));
        Assert.Equal(0x08000000u, CntEntryPolicy.AdjustFlags1(0x08000000u, id, "sce_sys/x"));
    }

    /// <summary>It clears a bit and never sets one, for any input.</summary>
    [Fact]
    public void NeverSetsTheEncryptionBit()
    {
        CntEntryPolicy.PlaintextLicenseEntries = true;
        foreach (uint id in new uint[] { 1024, 1025, 1026, 8224, 8225, 4736, 999 })
        foreach (uint flags in new uint[] { 0, 1, 0x08000000, 0x3000, 0x7FFFFFFF })
            Assert.Equal(0u, CntEntryPolicy.AdjustFlags1(flags, id, "x") & Enc);
    }

    /// <summary>
    /// Bits other than the encryption flag survive. If the library ever starts putting something
    /// else in Flags1 for these entries, this catches the shim eating it.
    /// </summary>
    [Fact]
    public void PreservesEveryOtherBit()
    {
        CntEntryPolicy.PlaintextLicenseEntries = true;
        Assert.Equal(0x08000001u, CntEntryPolicy.AdjustFlags1(Enc | 0x08000001u, 1024, "x"));
    }

    [Fact]
    public void AnEntryTheLibraryAlreadyResolvedAsPlaintextIsUntouched()
    {
        CntEntryPolicy.PlaintextLicenseEntries = true;
        Assert.Equal(0u, CntEntryPolicy.AdjustFlags1(0u, 1024, "x"));
    }

    [Fact]
    public void RightSprxIsSkippedOnlyWhenAsked()
    {
        Assert.False(CntEntryPolicy.ShouldSkipRightSprx());
        CntEntryPolicy.SuppressRightSprx = true;
        Assert.True(CntEntryPolicy.ShouldSkipRightSprx());
        CntEntryPolicy.Reset();
        Assert.False(CntEntryPolicy.ShouldSkipRightSprx());
    }

    [Fact]
    public void TheNoticeIsPrintedOncePerEntry()
    {
        var said = new List<string>();
        CntEntryPolicy.Log = said.Add;
        CntEntryPolicy.PlaintextLicenseEntries = true;
        for (int i = 0; i < 4; i++) CntEntryPolicy.AdjustFlags1(Enc, 1024, "sce_sys/license.dat");
        CntEntryPolicy.AdjustFlags1(Enc, 1025, "sce_sys/license.info");
        Assert.Equal(2, said.Count);
    }
}
