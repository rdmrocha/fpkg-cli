using LibProsperoPkg.Util;

namespace FpkgVirtualSource;

/// <summary>
/// A seekable, read-only view of one exFAT file. Every read of the underlying source is
/// taken under <c>gate</c>, which the owning reader shares across all its streams and its
/// own FAT/directory reads: the source is a single, non-reentrant reader.
/// </summary>
internal sealed class ExfatFileStream : Stream
{
    private readonly IMemoryReader _source;
    private readonly object _gate;
    private readonly int[] _clusters;
    private readonly long _clusterSize;
    private readonly Func<int, long> _clusterOffset;
    private long _position;

    internal ExfatFileStream(IMemoryReader source, object gate, int[] clusters,
                             long clusterSize, long length, Func<int, long> clusterOffset)
    {
        _source = source;
        _gate = gate;
        _clusters = clusters;
        _clusterSize = clusterSize;
        _clusterOffset = clusterOffset;
        Length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = value < 0
            ? throw new ArgumentOutOfRangeException(nameof(value))
            : value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0 || _position >= Length) return 0;

        int total = 0;
        while (count > 0 && _position < Length)
        {
            long index = _position / _clusterSize;
            if (index >= _clusters.Length) break;
            long within = _position % _clusterSize;
            int chunk = (int)Math.Min(Math.Min(count, _clusterSize - within), Length - _position);

            lock (_gate)
                _source.Read(_clusterOffset(_clusters[(int)index]) + within, buffer, offset, chunk);

            offset += chunk; count -= chunk; total += chunk; _position += chunk;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("cannot seek before the start of the file");
        return _position = target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
