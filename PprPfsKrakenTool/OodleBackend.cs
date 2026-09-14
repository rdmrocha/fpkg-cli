using System.Buffers;
using LibProsperoPkg.PFS.Compression.Oodle;

namespace PprPfsKrakenTool;

/// <summary>
/// Replaces ProsperoReducedKrakenEncoder's libScePubTools.dll backend with RAD's real
/// Oodle encoder. Output is the generic Reduced profile, not Sony's Publishing Tools
/// bitstream; see docs/superpowers/specs/2026-09-12-native-oodle-backend-design.md.
/// </summary>
public static class OodleBackend
{
    /// <summary>
    /// Why the Oodle backend is unusable, or null when it is usable.
    ///
    /// Two genuinely different failures live here and must not be collapsed into one
    /// "Oodle unavailable":
    ///   1. the user has supplied no RAD Oodle library — the ordinary state of a fresh
    ///      install, and the one with an easy fix;
    ///   2. a library was supplied but could not be bound — wrong file, or (the dangerous
    ///      one) a version whose structure layouts differ from the 2.9.16 headers the
    ///      binding was transcribed from, which Oodle_CheckVersion catches inside
    ///      NativeOodle.Load rather than letting it corrupt output.
    /// A user in case 2 who is told "Oodle unavailable" goes looking for the wrong problem,
    /// so the binder's own error text is surfaced verbatim.
    /// </summary>
    private static readonly Lazy<string?> LoadFailure = new(() =>
    {
        string? oodle = OodleLibrary.Find();
        if (oodle is null)
            return "no RAD Oodle library found: " + OodleLibrary.DescribeSearch() +
                   ". Copy the Oodle Core library out of an OodleUE 2.9.16 SDK into " +
                   "fpkg-tools/native/ (see NOTES.md).";

        try
        {
            int rc = NativeOodle.Load(oodle);
            if (rc != 0)
            {
                string why = NativeOodle.LastError;
                return "the RAD Oodle library could not be bound (" + rc + "): " +
                       (why.Length > 0 ? why : oodle);
            }
        }
        catch (Exception ex)
        {
            return $"the RAD Oodle library at {oodle} could not be bound: " + ex.Message;
        }

        return null;
    }, isThreadSafe: true);

    /// <summary>
    /// Why the backend is unusable, or null when it is usable. Callers that only need a
    /// yes/no use <see cref="IsAvailable"/>; this exists so a CLI can tell a user with the
    /// wrong Oodle version from one with no Oodle at all.
    /// </summary>
    public static string? Unavailability => LoadFailure.Value;

    public static bool IsAvailable(string? publishingToolsPath) => LoadFailure.Value is null;

    public static string Describe(string? publishingToolsPath)
    {
        if (LoadFailure.Value is { } why) return why;
        string note = "native RAD Oodle 2.9.16, bound directly from " +
                      (OodleLibrary.Find() ?? "the Oodle library") +
                      " (generic Reduced profile; not Sony publisher-identical)";
        return string.IsNullOrWhiteSpace(publishingToolsPath)
            ? note
            : note + $"; the supplied Publishing Tools path '{publishingToolsPath}' is ignored " +
                     "because no Windows DLL is loaded";
    }

    public static bool TryEncodeBlock(ReadOnlySpan<byte> data, int level,
        out byte[] payload, out bool multiChunk, out int firstChunkCompSize, out int boundaryFlags)
    {
        payload = Array.Empty<byte>();
        multiChunk = false; firstChunkCompSize = 0; boundaryFlags = 0;

        if (data.IsEmpty || data.Length > NativeOodle.MaxBlock) return false;
        if (level < -4 || level > 9) return false;
        if (!IsAvailable(null)) return false;

        // Chunk 1 always matches back into chunk 0. Controller Ruling D: an INDEPENDENT
        // chunk 1 is not merely worse, it is undecodable - KrakenDecoder.DecodeBlock reads
        // slot 1 with withSeed:false, and an independent compress always produces a
        // withSeed:true stream. There is therefore no independent-halves fallback; the
        // shim stores a half raw (stored=2) when nothing verifies.
        if (TryEncodeWith(data, level,
                          out payload, out multiChunk, out firstChunkCompSize, out boundaryFlags))
            return true;

        Interlocked.Increment(ref _rejections);
        payload = Array.Empty<byte>();
        multiChunk = false; firstChunkCompSize = 0; boundaryFlags = 0;
        return false;
    }

    private static long _rawStoredChunks;
    private static long _encoderFailures;
    private static long _rejections;

    /// <summary>
    /// Diagnostics for the acceptance run; see Task 6. RawStoredChunks counts halves the
    /// shim could not verify and stored raw (stored == 2) - the frequency that decides
    /// whether the unhandled bare-entropy case (bit 0x40) is worth implementing.
    /// EncoderFailures counts halves where the native encoder itself failed (stored == 3,
    /// comp &lt;= 0) - unlike RawStoredChunks and ordinary incompressible halves (stored == 1,
    /// uncounted), this is never expected to be non-zero and signals a broken Oodle SDK
    /// build, not merely unfavorable input. Both counters only include halves belonging to
    /// a block that was ultimately accepted (see TryEncodeWith) so a rejected block's halves
    /// are not double-reported here and in Rejections.
    /// </summary>
    public static (long RawStoredChunks, long EncoderFailures, long Rejections) Counters()
        => (Interlocked.Read(ref _rawStoredChunks), Interlocked.Read(ref _encoderFailures),
            Interlocked.Read(ref _rejections));

    private static unsafe bool TryEncodeWith(ReadOnlySpan<byte> data, int level,
        out byte[] payload, out bool multiChunk, out int firstChunkCompSize, out int boundaryFlags)
    {
        payload = Array.Empty<byte>();
        multiChunk = false; firstChunkCompSize = 0; boundaryFlags = 0;

        int chunks = data.Length <= NativeOodle.ChunkSize ? 1 : 2;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(NativeOodle.MaxBlock + 65536);
        byte[] assembled = ArrayPool<byte>.Shared.Rent(NativeOodle.MaxBlock + 65536);
        try
        {
            int total = 0, flags = 0, firstLen = 0;
            // Counted locally and only committed to the static counters once this block is
            // confirmed accepted below (see Task 7): a half counted here belongs to a block
            // that is still subject to VerifyWithLibraryDecoder, and TryEncodeBlock counts a
            // failure of that verification as a rejection. Incrementing the static counters
            // eagerly here would double-report such a half in both RawStoredChunks/
            // EncoderFailures and Rejections.
            int localRawStored = 0, localEncoderFailures = 0;
            fixed (byte* block = data)
            fixed (byte* tmp = scratch)
            {
                for (int i = 0; i < chunks; i++)
                {
                    int off = i * NativeOodle.ChunkSize;
                    int len = Math.Min(NativeOodle.ChunkSize, data.Length - off);
                    int useDict = i == 1 ? 1 : 0;

                    int rc = NativeOodle.EncodeChunk(block, data.Length, off, len, level, useDict,
                        tmp, scratch.Length,
                        out int outLen, out int newLz, out int phase, out int stored);
                    if (rc != 0 || outLen <= 0 || outLen > len) return false;
                    if (total + outLen > assembled.Length) return false;

                    Array.Copy(scratch, 0, assembled, total, outLen);
                    total += outLen;
                    if (i == 0) firstLen = outLen;

                    // Flag positions read out of the original Backend.Encode:
                    // chunk 0 contributes 0x2 (newLz) and 0x1 (phase);
                    // chunk 1 contributes 0x20 and 0x10.
                    if (stored == 2) localRawStored++;
                    else if (stored == 3) localEncoderFailures++;

                    // "Zero for stored blocks" - LibProsperoPkg's own documentation of the
                    // boundary flag byte. A stored half contributes no bits.
                    if (stored == 0)
                    {
                        if (newLz != 0) flags |= (i == 0) ? 0x2 : 0x20;
                        if (phase != 0) flags |= (i == 0) ? 0x1 : 0x10;
                    }
                }
            }

            byte[] result = assembled.AsSpan(0, total).ToArray();
            int first = chunks == 2 ? firstLen : 0;
            if (!VerifyWithLibraryDecoder(result, flags, first, data)) return false;

            if (localRawStored > 0) Interlocked.Add(ref _rawStoredChunks, localRawStored);
            if (localEncoderFailures > 0) Interlocked.Add(ref _encoderFailures, localEncoderFailures);

            payload = result; multiChunk = chunks == 2;
            firstChunkCompSize = first; boundaryFlags = flags;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            ArrayPool<byte>.Shared.Return(assembled);
        }
    }

    /// <summary>
    /// Adjudicates a block with LibProsperoPkg's own managed Kraken decoder — an
    /// independent reimplementation, and the same gate the original Backend.Encode used.
    /// </summary>
    public static bool VerifyWithLibraryDecoder(
        byte[] payload, int boundaryFlags, int firstChunkCompSize, ReadOnlySpan<byte> expected)
    {
        byte[] back = new byte[expected.Length];
        return KrakenDecoder.DecodeBlock(payload, boundaryFlags, firstChunkCompSize, back)
                   == KrakenDecodeStatus.Success
               && expected.SequenceEqual(back);
    }
}
