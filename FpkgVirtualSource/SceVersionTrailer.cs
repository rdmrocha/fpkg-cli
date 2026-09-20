namespace FpkgVirtualSource;

/// <summary>
/// Makes the library's <c>.sceversion</c> restamp idempotent for an executable that already
/// carries the record the restamp is about to write.
///
/// <para><b>The defect.</b> <c>--sdk-version</c> rewrites the version table at the end of a SELF.
/// When the library can locate that table it rewrites it IN PLACE — measured on the reference fixture's
/// <c>eboot.bin</c>: 784 bytes changed, 0 appended, 196 records, same length. When it cannot
/// locate it, it falls back to writing a single record for the module at <c>patchOffset = the
/// file's length</c>, i.e. it APPENDS. Measured on the large title's <c>eboot.bin</c>: 0 bytes of the
/// existing table changed, 27 appended. the large title's table was added by an earlier external repair
/// — its <c>eboot.bin.esbak</c>, the pre-repair original, ends in zeros with no table at all — so
/// the library does not see it and appends a SECOND <c>eboot:</c> record. At the default version
/// the appended record is byte-identical to the one already there, which is why the corruption
/// presents as "the last 27 bytes duplicated".
/// </para>
///
/// <para><b>The correction.</b> Hide the stale record from every read of the original file. The
/// library opens that file twice through <see cref="VirtualSource.OpenOrFile"/> — once to probe
/// for the patch offset, once for the Write delegate's prefix copy — so a single length-limited
/// view moves <c>patchOffset</c> back by exactly the record's length and the append lands on top
/// of the record instead of after it. Net effect: an in-place replace, and at the source's own
/// version an output byte-identical to the input.
/// </para>
///
/// <para>Feeding the library an executable whose trailing record has been physically removed
/// produces the correct output, so hiding the record achieves the same result without modifying
/// the user's file.</para>
///
/// <para><b>Scope.</b> Only paths handed to <see cref="Register"/>, and only the single trailing
/// record whose name matches the module's own. Empty in every build that does not set an SDK
/// override, so <see cref="VirtualSource.OpenOrFile"/> takes the stock path there.</para>
/// </summary>
public static class SceVersionTrailer
{
    /// <summary>
    /// A <c>.sceversion</c> record: <c>00 00 &lt;len&gt; 00 08 &lt;name&gt; &lt;8 bytes&gt;
    /// &lt;8 bytes&gt;</c>, occupying <c>len + 4</c> bytes, where <c>len = name.Length + 17</c>.
    /// Verified against both oracles: <c>eboot:</c> (len 0x17, 27 bytes) and
    /// <c>libSceAmpr_stub_weak:</c> (len 0x26, 42 bytes).
    /// </summary>
    public static int RecordLength(string name) => name.Length + 21;

    /// <summary>The record name a module refers to itself by: the file name without its extension,
    /// plus a colon. <c>eboot.bin</c> -> <c>eboot:</c>.</summary>
    public static string SelfRecordName(string path) =>
        Path.GetFileNameWithoutExtension(path) + ":";

    private static volatile Dictionary<string, long> _hidden =
        new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>Where the notices go. A change this narrow still changes the bytes written, so it
    /// is stated on the build's normal output rather than a debug channel.</summary>
    public static Action<string>? Log { get; set; } = Console.Out.WriteLine;

    /// <summary>Forgets every registration. For tests, and for a second build in one process.</summary>
    public static void Reset()
    {
        lock (Gate) _hidden = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>How many executables are having a stale record hidden.</summary>
    public static int Count => _hidden.Count;

    /// <summary>
    /// True when <paramref name="path"/> ends with a <c>.sceversion</c> record naming the module
    /// itself, and reports its length. Nothing is scanned: the record's length is fixed by its
    /// name, so the one candidate offset is computed and its shape verified there.
    /// </summary>
    public static bool TryFindTrailingSelfRecord(string path, out int recordLength)
    {
        recordLength = 0;
        string name = SelfRecordName(path);
        int want = RecordLength(name);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                                      FileOptions.SequentialScan);
        if (fs.Length < want) return false;

        var buf = new byte[want];
        fs.Seek(-want, SeekOrigin.End);
        fs.ReadExactly(buf);

        // 00 00 <len> 00 08, then the name, then 16 bytes of version fields.
        if (buf[0] != 0 || buf[1] != 0 || buf[2] != name.Length + 17 || buf[3] != 0 || buf[4] != 8)
            return false;
        for (int i = 0; i < name.Length; i++)
            if (buf[5 + i] != (byte)name[i]) return false;

        recordLength = want;
        return true;
    }

    /// <summary>
    /// Hides the trailing self-record of <paramref name="path"/> from subsequent reads through
    /// <see cref="VirtualSource.OpenOrFile"/>. Returns false, and registers nothing, when the file
    /// has no such record — that is the normal case, and there the library's append is the correct
    /// behaviour because it is what stamps a never-stamped executable.
    /// </summary>
    public static bool Register(string path)
    {
        string full = Path.GetFullPath(path);
        if (!TryFindTrailingSelfRecord(full, out int len)) return false;

        lock (Gate)
        {
            var next = new Dictionary<string, long>(_hidden, StringComparer.Ordinal) { [full] = len };
            _hidden = next;
        }
        Log?.Invoke($"  sdk:    {Path.GetFileName(full)} already carries a " +
                    $"'{SelfRecordName(full)}' .sceversion record ({len} bytes); it will be " +
                    "replaced in place rather than appended to");
        return true;
    }

    /// <summary>Bytes to hide from the end of this path, or 0.</summary>
    public static long HiddenBytes(string fullPath) =>
        _hidden.TryGetValue(fullPath, out long n) ? n : 0;

    /// <summary>
    /// The stock read-only open, with the stale record trimmed off the end when one is registered.
    /// </summary>
    public static Stream OpenTrimmed(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                                FileOptions.RandomAccess);
        long hide = HiddenBytes(Path.GetFullPath(path));
        return hide == 0 ? fs : new TruncatedStream(fs, fs.Length - hide);
    }

    /// <summary>
    /// A read-only, seekable view of the first <c>length</c> bytes of another stream. Seeking past
    /// the limit is allowed and simply reads nothing, exactly as it would at a real end of file.
    /// </summary>
    private sealed class TruncatedStream(Stream inner, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = length - inner.Position;
            if (left <= 0) return 0;
            if (buffer.Length > left) buffer = buffer[..(int)left];
            return inner.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(origin == SeekOrigin.End ? length + offset : offset,
                       origin == SeekOrigin.End ? SeekOrigin.Begin : origin);

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
