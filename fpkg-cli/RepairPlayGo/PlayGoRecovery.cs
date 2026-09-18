using System.Buffers.Binary;
using System.Text;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// PlayGo layout values recovered from an existing <c>playgo-chunk.dat</c> (entry id 4097's
/// payload). Unlike <see cref="CntHeader"/>, every field here is LITTLE-endian — this is the
/// PlayGo descriptor format, not the CNT header.
/// </summary>
internal sealed record PlayGoRecovery(
    int ChunkCount, int ScenarioCount, int DefaultScenarioId, int DefaultLanguageId,
    int ExtentCount, ulong LanguageMask, string ContentId,
    ulong TotalSize, ulong DataSize, ulong TailSize,
    IReadOnlyList<string> ScenarioLabels)
{
    private const int ChunkCountOffset      = 10;  // u16
    private const int ScenarioCountOffset   = 14;  // u16
    private const int DefaultScenarioOffset = 20;  // u16
    private const int ExtentCountOffset     = 32;  // u32
    private const int DefaultLanguageOffset = 36;  // u8
    private const int LanguageMaskOffset    = 56;  // u64
    private const int ContentIdOffset       = 64;  // 48-byte ASCII slot, 36 chars used
    private const int ContentIdSlotLength   = 48;
    private const int ContentIdLength       = 36;
    private const int ExtentSectionOffsetPos = 216; // u32
    private const int ScenarioLabelsOffsetPos = 240; // u32
    private const int ScenarioLabelsLengthPos = 244; // u32
    private const int ExtentRecordSize      = 16;   // u64 offset, u64 length
    private const ulong ExtentValueMask     = 0xFFFF_FFFF_FFFFul; // low 48 bits; top 16 are flags

    private const int MinimumLength = 256;

    internal static PlayGoRecovery From(ReadOnlySpan<byte> d)
    {
        if (d.Length < MinimumLength)
            throw new InvalidDataException(
                $"playgo-chunk.dat is too small: {d.Length} bytes, need at least {MinimumLength}.");

        int chunkCount    = BinaryPrimitives.ReadUInt16LittleEndian(d[ChunkCountOffset..]);
        int scenarioCount = BinaryPrimitives.ReadUInt16LittleEndian(d[ScenarioCountOffset..]);
        int defScenario   = BinaryPrimitives.ReadUInt16LittleEndian(d[DefaultScenarioOffset..]);
        int extentCount   = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[ExtentCountOffset..]);
        int defLanguage   = d[DefaultLanguageOffset];
        ulong mask        = BinaryPrimitives.ReadUInt64LittleEndian(d[LanguageMaskOffset..]);

        RequireWithin(d.Length, ContentIdOffset, (long)ContentIdSlotLength, "content id");
        string contentId = Encoding.ASCII
            .GetString(d.Slice(ContentIdOffset, ContentIdLength))
            .TrimEnd('\0');

        int extentsAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[ExtentSectionOffsetPos..]);
        if (extentCount < 0)
            throw new InvalidDataException($"playgo-chunk.dat has a negative extent count: {extentCount}.");
        RequireWithin(d.Length, extentsAt, (long)extentCount * ExtentRecordSize, "extent section");

        ulong total = 0, tail = 0;
        for (int i = 0; i < extentCount; i++)
        {
            int at = extentsAt + i * ExtentRecordSize;
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(d[(at + 8)..]) & ExtentValueMask;
            total += length;
            tail = length; // the last extent's length wins — not the max, not the first
        }

        int labelsAt  = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[ScenarioLabelsOffsetPos..]);
        int labelsLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[ScenarioLabelsLengthPos..]);
        RequireWithin(d.Length, labelsAt, (long)labelsLen, "scenario label section");

        var labels = Encoding.ASCII.GetString(d.Slice(labelsAt, labelsLen))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Take(scenarioCount)
            .ToArray();

        return new PlayGoRecovery(chunkCount, scenarioCount, defScenario, defLanguage, extentCount,
            mask, contentId, total, total - tail, tail, labels);
    }

    private static void RequireWithin(int bufferLength, int offset, long length, string what)
    {
        if (offset < 0 || length < 0 || offset + length > bufferLength)
            throw new InvalidDataException(
                $"playgo-chunk.dat's {what} (offset {offset}, length {length}) falls outside its {bufferLength}-byte buffer.");
    }
}
