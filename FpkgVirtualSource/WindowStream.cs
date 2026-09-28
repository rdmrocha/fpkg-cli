namespace FpkgVirtualSource;

/// <summary>
/// A seekable read/write view of a byte range of <paramref name="file"/> starting at
/// <c>baseOffset</c>, presented as if it were a file of its own.
///
/// <para>This is what lets LibProsperoPkg write the inner <c>pfs_image.dat</c> straight into the
/// final package's outer-PFS data region instead of into a spool file: every offset the library
/// computes from zero lands at <c>baseOffset + offset</c>, so stage 1's plaintext ends up exactly
/// where stage 3 is going to read it from and encrypt it back into.</para>
///
/// <para>The bytes before <c>baseOffset</c> are the package's FIH header range and are never
/// touched — not by a write, not by <see cref="SetLength"/>, and not by the Create-mode
/// truncation, which clears the window only.</para>
/// </summary>
internal sealed class WindowStream : Stream
{
    private readonly FileStream file;
    private long position;
    private bool disposed;

    internal WindowStream(FileStream file, long baseOffset, bool truncateOnOpen)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        this.file = file;
        BaseOffset = baseOffset;
        if (truncateOnOpen && file.Length > baseOffset)
            file.SetLength(baseOffset);
        HighWaterMark = Math.Max(0, file.Length - baseOffset);
    }

    /// <summary>Where this window starts inside the underlying file.</summary>
    internal long BaseOffset { get; }

    /// <summary>
    /// The largest logical length this window has ever held. The inner image's final size: the
    /// assembler truncates the window down to the data end at the close of stage 1, so
    /// <see cref="Length"/> alone would not survive the package growing around it.
    /// </summary>
    internal long HighWaterMark { get; private set; }

    public override bool CanRead => !disposed && file.CanRead;

    public override bool CanSeek => !disposed;

    public override bool CanWrite => !disposed && file.CanWrite;

    public override long Length => Math.Max(0, file.Length - BaseOffset);

    public override long Position
    {
        get => position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long remaining = Length - position;
        if (remaining <= 0)
            return 0;

        int want = (int)Math.Min(buffer.Length, remaining);
        file.Position = BaseOffset + position;
        int read = file.Read(buffer[..want]);
        position += read;
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        file.Position = BaseOffset + position;
        file.Write(buffer);
        position += buffer.Length;
        HighWaterMark = Math.Max(HighWaterMark, position);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return position;
    }

    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        file.SetLength(BaseOffset + value);
        HighWaterMark = Math.Max(HighWaterMark, value);
    }

    public override void Flush() => file.Flush();

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            if (disposing)
                file.Dispose();
        }
        base.Dispose(disposing);
    }
}
