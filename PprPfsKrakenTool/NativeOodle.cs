using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PprPfsKrakenTool;

/// <summary>
/// Drives RAD's Oodle encoder directly from managed code.
///
/// This used to P/Invoke a small C shim (native/fpkg_oodle.c) that statically linked
/// liboo2coremac64.a. That made the shim itself EULA-encumbered, unshippable, and a
/// per-platform Mach-O artefact that needed a compiler. Everything the shim did is
/// expressible with <see cref="NativeLibrary"/> and unmanaged function pointers, so it is
/// done here instead: no native code of ours ships at all, one set of managed DLLs works
/// wherever .NET and an Oodle build exist, and the user supplies only their own Oodle
/// dynamic library.
///
/// fpkg_oodle.c remains the specification for the encode algorithm below (the option
/// setup, the Reduced profile, the dictionary trick and the three-tier
/// (header, newLz, phase) search); this is a faithful port of it, not a rewrite.
///
/// Only five Oodle entry points are used. Everything else comes from oodle2.h and is
/// transcribed into <see cref="CompressOptions"/> and the enum constants below — which is
/// exactly why <see cref="Load"/> calls Oodle_CheckVersion: the transcription is pinned to
/// 2.9.16 and a library with different structure layouts must be refused, not used.
/// </summary>
internal static unsafe class NativeOodle
{
    internal const int ChunkSize = 131072;
    internal const int MaxBlock = 262144;

    /// <summary>64 MiB, matching the original Backend.Encode.</summary>
    private const int ScratchSize = 67108864;
    private const int MaxHeader = 16;
    private const byte Sentinel = 0xCD;

    // ---- oodle2.h constants, transcribed -------------------------------------------------

    /// <summary>
    /// <c>OODLE_HEADER_VERSION</c> for 2.9.16:
    /// <c>(46 &lt;&lt; 24) | (9 &lt;&lt; 16) | (16 &lt;&lt; 8) | sizeof(OodleLZ_SeekTable)</c>, the last
    /// term being 48. Folding a structure size into the check is what makes
    /// Oodle_CheckVersion an ABI test rather than a version-string comparison.
    /// </summary>
    private const uint OodleHeaderVersion = (46u << 24) | (9u << 16) | (16u << 8) | 48u;

    private const string OodleVersionText = "2.9.16";

    private const int CompressorKraken = 8;        // OodleLZ_Compressor_Kraken
    private const int CompressorInvalid = -1;      // OodleLZ_Compressor_Invalid
    private const int CompressionLevelNormal = 4;  // OodleLZ_CompressionLevel_Normal
    private const int ProfileReduced = 1;          // OodleLZ_Profile_Reduced
    private const int JobifyDisable = 1;           // OodleLZ_Jobify_Disable

    // ---- return codes, unchanged from the C shim so callers keep reading the same values --

    internal const int Ok = 0;
    internal const int EArgs = -1;
    internal const int ENoMem = -2;
    internal const int ECapacity = -4;
    internal const int ENotLoaded = -6;
    internal const int EDlOpen = -7;
    internal const int ESymbol = -8;
    internal const int EVersion = -9;

    /// <summary>
    /// <c>OodleLZ_CompressOptions</c>, field for field from oodle2.h.
    ///
    /// Packing is NOT incidental: oodle2base.h defines OOSTRUCT as
    /// <c>struct __attribute__((__packed__))</c> for clang and gcc, so the native struct is
    /// byte-packed and <c>jobifyUserPtr</c> sits at offset 52 — an unaligned pointer that a
    /// default .NET layout would push to 56 and shift every following field. Verified
    /// against the SDK header by compiling an offsetof/sizeof probe: 84 bytes total, with
    /// jobifyUserPtr at 52, farMatchMinLen at 60, farMatchOffsetLog2 at 64 and reserved at
    /// 68. Get this wrong and Oodle silently encodes with the wrong options rather than
    /// failing, so the size is asserted at load time as well.
    ///
    /// MSVC uses <c>#pragma pack(push, Oodle, 8)</c> instead, which yields the same 84-byte
    /// layout for these fields, so Pack = 1 is right on Windows too.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct CompressOptions
    {
        public uint UnusedWasVerbosity;
        public int MinMatchLen;
        public int SeekChunkReset;                 // OO_BOOL is int32_t
        public int SeekChunkLen;
        public int Profile;                        // OodleLZ_Profile
        public int DictionarySize;
        public int SpaceSpeedTradeoffBytes;
        public int UnusedWasMaxHuffmansPerChunk;
        public int SendQuantumCrcs;
        public int MaxLocalDictionarySize;
        public int MakeLongRangeMatcher;
        public int MatchTableSizeLog2;
        public int Jobify;                         // OodleLZ_Jobify
        public nint JobifyUserPtr;                 // void*, at offset 52 because of the packing
        public int FarMatchMinLen;
        public int FarMatchOffsetLog2;
        public uint Reserved0, Reserved1, Reserved2, Reserved3;
    }

    // ---- the five entry points -----------------------------------------------------------

    private static delegate* unmanaged[Cdecl]<
        int, void*, nint, void*, int, CompressOptions*, void*, void*, void*, nint, nint> _compress;
    private static delegate* unmanaged[Cdecl]<int, int, CompressOptions*> _getDefaultOptions;
    private static delegate* unmanaged[Cdecl]<void*, void*> _setPrintf;
    private static delegate* unmanaged[Cdecl]<
        byte*, nuint, byte*, nuint, long, int, int, int, void*, long, nint> _decodeHeaderless;

    private static CompressOptions _options;
    private static volatile bool _loaded;
    private static readonly Lock LoadLock = new();
    private static string _lastError = "";

    internal static string LastError => _lastError;

    /// <summary>
    /// Loads the RAD Oodle library at <paramref name="path"/> and binds the entry points.
    ///
    /// The path is used verbatim, never through a loader search: the macOS SDK dylib's
    /// install_name names a different file entirely
    /// (<c>@executable_path/liboo2coremacarm64.2.9.16.dylib</c>), so anything that relied on
    /// install_name resolution would fail or, worse, find some other copy.
    ///
    /// Returns <see cref="Ok"/>, or a negative code with detail in <see cref="LastError"/>.
    /// Idempotent once it has succeeded; thread-safe.
    /// </summary>
    internal static int Load(string path)
    {
        if (_loaded) return Ok;
        if (string.IsNullOrWhiteSpace(path))
        {
            _lastError = "no Oodle library path was supplied";
            return EArgs;
        }

        lock (LoadLock)
        {
            if (_loaded) return Ok;

            nint handle;
            try
            {
                handle = NativeLibrary.Load(path);
            }
            catch (Exception ex)
            {
                _lastError = $"could not load '{path}': {ex.Message}";
                return EDlOpen;
            }

            try
            {
                // The ABI guard, before anything else is called. OODLE_HEADER_VERSION folds
                // a format tag, the major and minor version and sizeof(OodleLZ_SeekTable)
                // into one word, so a library whose layouts differ from the headers this
                // was transcribed from is rejected here instead of silently mis-encoding
                // through a mismatched CompressOptions.
                if (!NativeLibrary.TryGetExport(handle, "Oodle_CheckVersion", out nint checkPtr))
                    return Reject(handle, ESymbol,
                        $"'{path}' does not export Oodle_CheckVersion; it does not look like a RAD " +
                        $"Oodle Core library (expected liboo2coremac64.{OodleVersionText}.dylib or " +
                        "the equivalent for this platform)");

                var checkVersion = (delegate* unmanaged[Cdecl]<uint, uint*, int>)checkPtr;
                uint libVersion = 0;
                if (checkVersion(OodleHeaderVersion, &libVersion) == 0)
                    return Reject(handle, EVersion,
                        $"'{path}' is Oodle ABI 0x{libVersion:X8}, but this build targets Oodle " +
                        $"{OodleVersionText} (ABI 0x{OodleHeaderVersion:X8}). Supply the matching " +
                        "Oodle Core library.");

                if (sizeof(CompressOptions) != 84)
                    return Reject(handle, EVersion,
                        $"OodleLZ_CompressOptions marshals to {sizeof(CompressOptions)} bytes, " +
                        "expected 84; the transcription in NativeOodle no longer matches oodle2.h.");

                if (!TryBind(handle, path, "OodleLZ_Compress", out nint compress) ||
                    !TryBind(handle, path, "OodleLZ_CompressOptions_GetDefault", out nint getDefault) ||
                    !TryBind(handle, path, "OodleCore_Plugins_SetPrintf", out nint setPrintf) ||
                    !TryBind(handle, path, "OodleKraken_Decode_Headerless", out nint decode))
                {
                    NativeLibrary.Free(handle);
                    return ESymbol;
                }

                _compress = (delegate* unmanaged[Cdecl]<
                    int, void*, nint, void*, int, CompressOptions*, void*, void*, void*, nint, nint>)compress;
                _getDefaultOptions = (delegate* unmanaged[Cdecl]<int, int, CompressOptions*>)getDefault;
                _setPrintf = (delegate* unmanaged[Cdecl]<void*, void*>)setPrintf;
                _decodeHeaderless = (delegate* unmanaged[Cdecl]<
                    byte*, nuint, byte*, nuint, long, int, int, int, void*, long, nint>)decode;

                // The self-verification sweep below deliberately feeds bad
                // (hdr, newLz, phase) combinations to the decoder, which logs
                // "OODLE ERROR : corruption : ..." for every one. Oodle's default printf
                // plugin writes to stdout, and fpkg's stdout is parsed by callers, so
                // logging is switched off. oodle2.h: "To disable all logging, call
                // OodleCore_Plugins_SetPrintf(NULL)". A managed callback is not an option
                // anyway — the plugin signature is variadic.
                _setPrintf(null);

                _options = *_getDefaultOptions(CompressorInvalid, CompressionLevelNormal);
                _options.Profile = ProfileReduced;
                _options.DictionarySize = 0x40000;
                _options.Jobify = JobifyDisable;

                _lastError = "";
                _loaded = true;
                return Ok;
            }
            catch (Exception ex)
            {
                _lastError = $"'{path}' could not be bound: {ex.Message}";
                try { NativeLibrary.Free(handle); } catch (Exception) { /* best effort */ }
                return ESymbol;
            }
        }
    }

    private static bool TryBind(nint handle, string path, string name, out nint export)
    {
        if (NativeLibrary.TryGetExport(handle, name, out export)) return true;
        _lastError = $"'{path}' does not export {name}; it does not look like a RAD Oodle Core library";
        return false;
    }

    private static int Reject(nint handle, int code, string why)
    {
        _lastError = why;
        try { NativeLibrary.Free(handle); } catch (Exception) { /* best effort */ }
        return code;
    }

    // ---- per-thread working memory ---------------------------------------------------------

    /// <summary>
    /// The four scratch buffers one encoding thread needs, in unmanaged memory.
    ///
    /// Unmanaged rather than pinned arrays: 64 MiB of pinned GC heap per worker thread is a
    /// fragmentation hazard, and these live for the whole build. The finalizer frees them
    /// when the owning thread ends and the thread-static reference becomes unreachable,
    /// which is what the C version's pthread_key destructor did.
    /// </summary>
    private sealed unsafe class Work
    {
        [ThreadStatic] private static Work? _current;

        internal readonly byte* Scratch;   // ScratchSize
        internal readonly byte* Comp;      // ChunkSize + 65536
        internal readonly byte* Verify;    // MaxBlock
        internal readonly byte* Pad;       // 3 * ChunkSize (see Ruling D below)

        private Work()
        {
            Scratch = (byte*)NativeMemory.Alloc(ScratchSize);
            Comp = (byte*)NativeMemory.Alloc(ChunkSize + 65536);
            Verify = (byte*)NativeMemory.Alloc(MaxBlock);
            Pad = (byte*)NativeMemory.Alloc(3 * (nuint)ChunkSize);
        }

        ~Work()
        {
            NativeMemory.Free(Scratch);
            NativeMemory.Free(Comp);
            NativeMemory.Free(Verify);
            NativeMemory.Free(Pad);
        }

        internal static Work Current => _current ??= new Work();
    }

    // ---- the (header, newLz, phase) triple, discovered once and then reused ----------------

    /* Indexed by chunk slot: [0] is the seeded first chunk, [1] the continuation.
       They genuinely differ - measured (8,1,0) and (8,1,1) respectively.

       Concurrency note: these arrays are read and written by concurrent worker threads
       with no lock, and the payload this function accepts is always Comp + hdr using the
       hdr that verified for THIS chunk (not necessarily the one currently cached here).
       Determinism therefore does not depend on the cache being race-free - it depends on
       there being exactly one (hdr, newLz, phase) triple that verifies for a given chunk's
       content at a given slot, so whichever thread's write "wins" the race is immaterial to
       any thread's own accepted output. */
    private static readonly int[] CachedHeader = [8, 8];
    private static readonly int[] CachedNewLz = [1, 1];
    private static readonly int[] CachedPhase = [0, 1];

    /// <summary>
    /// Decodes one half and reports whether it reproduced the plaintext exactly. The verify
    /// buffer is seeded with the whole block so a dependent half can match back into its
    /// predecessor, then the half's own range is overwritten with a sentinel so a decode
    /// that writes nothing cannot pass.
    /// </summary>
    private static bool VerifyOne(Work w, byte* block, int blockLen, int chunkOff, int chunkLen,
                                  byte* payload, int payloadLen, int newLz, int phase)
    {
        Buffer.MemoryCopy(block, w.Verify, MaxBlock, blockLen);
        new Span<byte>(w.Verify + chunkOff, chunkLen).Fill(Sentinel);
        nint r = _decodeHeaderless(
            w.Verify + chunkOff, (nuint)chunkLen, payload, (nuint)payloadLen,
            chunkOff, 0, newLz, phase, w.Scratch, ScratchSize);
        if (r != chunkLen) return false;
        return new ReadOnlySpan<byte>(w.Verify + chunkOff, chunkLen)
            .SequenceEqual(new ReadOnlySpan<byte>(block + chunkOff, chunkLen));
    }

    /// <summary>
    /// Encodes one &lt;= 128 KiB half of a block into a headerless Kraken payload. A direct
    /// port of fpkg_oodle_encode_chunk; the signature, the return codes and the quad-state
    /// <paramref name="stored"/> are unchanged.
    ///   0 compressed and verified; <paramref name="output"/> holds a Kraken payload
    ///   1 genuinely incompressible (comp >= chunkLen) - legitimate
    ///   2 no (hdr, newLz, phase) triple verified during the sweep; diagnostic only, the
    ///     caller's decoder adjudicates the block
    ///   3 the encoder itself failed (comp &lt;= 0) - distinct from case 1: compression was
    ///     never attempted successfully and should be surfaced as a warning
    /// </summary>
    internal static int EncodeChunk(
        byte* block, int blockLen, int chunkOff, int chunkLen,
        int level, int useDictionary, byte* output, int outputCapacity,
        out int outLen, out int newLz, out int phase, out int stored)
    {
        outLen = 0; newLz = 0; phase = 0; stored = 0;

        if (block == null || output == null) return EArgs;
        if (blockLen <= 0 || blockLen > MaxBlock) return EArgs;
        if (chunkOff < 0 || chunkLen <= 0 || chunkOff + chunkLen > blockLen) return EArgs;
        if (chunkLen > ChunkSize) return EArgs;
        if (level < -4 || level > 9) return EArgs;
        if (outputCapacity < chunkLen) return ECapacity;
        if (!_loaded) return ENotLoaded;

        Work w;
        try { w = Work.Current; }
        catch (OutOfMemoryException) { return ENoMem; }

        int slot = chunkOff == 0 ? 0 : 1;

        // Ruling D: Oodle rejects a dictionary whose offset from dictionaryBase is not a
        // multiple of OODLELZ_BLOCK_LEN (262144). Copying the block to offset ChunkSize
        // inside a 3-chunk work buffer puts chunk 1 at dictionary offset 262144, which is
        // legal, and lets it match back into chunk 0 - which is what DecodeBlock's
        // withSeed:false slot requires. dictionarySize is clamped to one chunk so the
        // encoder can never reach into the filler, which the decoder does not have.
        new Span<byte>(w.Pad, ChunkSize).Clear();
        Buffer.MemoryCopy(block, w.Pad + ChunkSize, 2 * (long)ChunkSize, blockLen);

        CompressOptions opts = _options;
        byte* dictBase = null;
        if (useDictionary != 0 && slot == 1)
        {
            dictBase = w.Pad;
            opts.DictionarySize = ChunkSize;
        }

        nint comp = _compress(
            CompressorKraken,
            w.Pad + ChunkSize + chunkOff, chunkLen, w.Comp,
            level, &opts,
            dictBase, null, w.Scratch, ScratchSize);

        // The encoder itself failed (e.g. a different Oodle build that errors out of
        // OodleLZ_Compress). This is NOT "incompressible data" - it means compression was
        // never attempted successfully, and must be distinguishable from stored=1 so a
        // caller can warn instead of silently treating the whole build as fine.
        if (comp <= 0)
        {
            Buffer.MemoryCopy(block + chunkOff, output, outputCapacity, chunkLen);
            outLen = chunkLen; stored = 3;
            return Ok;
        }

        // Genuinely did not compress: store the half raw. Legitimate and expected.
        if (comp >= chunkLen)
        {
            Buffer.MemoryCopy(block + chunkOff, output, outputCapacity, chunkLen);
            outLen = chunkLen; stored = 1;
            return Ok;
        }

        // Three-tier lookup. hdr is stable at 8 for a slot, but the (newLz, phase) flag
        // pair varies with content, so tier 1 (the exact cached triple) misses often.
        // Tier 2 catches that common case cheaply - at most 3 extra decodes at the SAME
        // cached header size - before falling back to the expensive full sweep in tier 3.
        int hdr = CachedHeader[slot], nl = CachedNewLz[slot], ph = CachedPhase[slot];
        bool accepted = false;

        // Tier 1: the exact cached triple for this slot.
        if (hdr < (int)comp &&
            VerifyOne(w, block, blockLen, chunkOff, chunkLen, w.Comp + hdr, (int)comp - hdr, nl, ph))
        {
            accepted = true;
        }

        // Tier 2: the other three (newLz, phase) combinations at the cached header size.
        // On success only the flag pair is re-cached; hdr = 8 remains correct.
        if (!accepted && hdr < (int)comp)
        {
            for (int nl2 = 0; nl2 <= 1 && !accepted; nl2++)
                for (int ph2 = 0; ph2 <= 1 && !accepted; ph2++)
                {
                    if (nl2 == nl && ph2 == ph) continue;
                    if (VerifyOne(w, block, blockLen, chunkOff, chunkLen,
                                  w.Comp + hdr, (int)comp - hdr, nl2, ph2))
                    {
                        nl = nl2; ph = ph2;
                        CachedNewLz[slot] = nl; CachedPhase[slot] = ph;
                        accepted = true;
                    }
                }
        }

        // Tier 3: full sweep, only reached when the cached header size itself is wrong.
        // The triple that matched is kept in locals rather than read back out of the shared
        // cache: another thread may overwrite the cache between the store and the read, and
        // the payload accepted below must be the one that actually verified here.
        if (!accepted)
        {
            for (int h = 0; h <= MaxHeader && !accepted; h++)
            {
                if (h >= (int)comp) break;
                for (int n = 0; n <= 1 && !accepted; n++)
                    for (int p = 0; p <= 1 && !accepted; p++)
                        if (VerifyOne(w, block, blockLen, chunkOff, chunkLen,
                                      w.Comp + h, (int)comp - h, n, p))
                        {
                            hdr = h; nl = n; ph = p;
                            CachedHeader[slot] = h; CachedNewLz[slot] = n; CachedPhase[slot] = p;
                            accepted = true;
                        }
            }
        }

        if (!accepted)
        {
            // Nothing verified. Store raw rather than failing the block: stored=2
            // distinguishes this from "did not compress" (1) and "encoder failed" (3) so
            // the managed layer can count it separately.
            Buffer.MemoryCopy(block + chunkOff, output, outputCapacity, chunkLen);
            outLen = chunkLen; stored = 2;
            return Ok;
        }

        int payloadLen = (int)comp - hdr;
        if (payloadLen > outputCapacity) return ECapacity;
        Buffer.MemoryCopy(w.Comp + hdr, output, outputCapacity, payloadLen);
        outLen = payloadLen; newLz = nl; phase = ph; stored = 0;
        return Ok;
    }
}
