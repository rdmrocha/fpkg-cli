using Xunit;

namespace FpkgVirtualSource.Tests;

public class SceVersionTrailerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sceversion").FullName;

    public void Dispose()
    {
        SceVersionTrailer.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A .sceversion record: 00 00 len 00 08 name, then 16 bytes of version fields.</summary>
    private static byte[] Record(string name)
    {
        var r = new byte[name.Length + 21];
        r[2] = (byte)(name.Length + 17);
        r[4] = 8;
        for (int i = 0; i < name.Length; i++) r[5 + i] = (byte)name[i];
        for (int i = 0; i < 16; i++) r[5 + name.Length + i] = 0xAA;
        return r;
    }

    private string Write(string fileName, params byte[][] parts)
    {
        string path = Path.Combine(_dir, fileName);
        // PS5 SELF magic, so the CLI's own executable check would accept it too.
        var head = new byte[] { 0x54, 0x14, 0xF5, 0xEE };
        File.WriteAllBytes(path, parts.Aggregate(head, (a, b) => [.. a, .. b]));
        return path;
    }

    [Fact]
    public void RecordLength_matches_both_measured_oracles()
    {
        // the large title's eboot.bin and the reference fixture's eboot.bin, measured from the real binaries.
        Assert.Equal(27, SceVersionTrailer.RecordLength("eboot:"));
        Assert.Equal(42, SceVersionTrailer.RecordLength("libSceAmpr_stub_weak:"));
    }

    [Fact]
    public void Finds_the_trailing_record_when_it_names_the_module()
    {
        string p = Write("eboot.bin", Record("crt1:"), Record("eboot:"));
        Assert.True(SceVersionTrailer.TryFindTrailingSelfRecord(p, out int len));
        Assert.Equal(27, len);
    }

    [Fact]
    public void Ignores_a_trailing_record_that_names_something_else()
    {
        // the reference fixture's shape: a real table, but its last record is a library stub, not the
        // module. The library locates and rewrites that table in place, so nothing must be hidden.
        string p = Write("eboot.bin", Record("crt1:"), Record("libSceAmpr_stub_weak:"));
        Assert.False(SceVersionTrailer.TryFindTrailingSelfRecord(p, out _));
    }

    [Fact]
    public void Ignores_a_file_with_no_table_at_all()
    {
        string p = Write("eboot.bin", new byte[512]);
        Assert.False(SceVersionTrailer.TryFindTrailingSelfRecord(p, out _));
    }

    [Fact]
    public void Register_declines_a_file_without_the_record_and_hides_nothing()
    {
        SceVersionTrailer.Log = null;
        string p = Write("eboot.bin", Record("libc:"));
        Assert.False(SceVersionTrailer.Register(p));
        Assert.Equal(0, SceVersionTrailer.Count);
        using var s = SceVersionTrailer.OpenTrimmed(p);
        Assert.Equal(new FileInfo(p).Length, s.Length);
    }

    [Fact]
    public void A_registered_file_reads_short_by_exactly_the_record()
    {
        SceVersionTrailer.Log = null;
        string p = Write("eboot.bin", Record("crt1:"), Record("eboot:"));
        long full = new FileInfo(p).Length;
        Assert.True(SceVersionTrailer.Register(p));

        using var s = SceVersionTrailer.OpenTrimmed(p);
        Assert.Equal(full - 27, s.Length);

        // Reading to the end stops at the limit, and never returns the hidden bytes.
        var all = new MemoryStream();
        s.CopyTo(all);
        Assert.Equal(full - 27, all.Length);
        Assert.Equal(File.ReadAllBytes(p)[..(int)(full - 27)], all.ToArray());
    }

    [Fact]
    public void Seeking_within_and_past_the_limit_behaves_like_a_real_end_of_file()
    {
        SceVersionTrailer.Log = null;
        string p = Write("eboot.bin", Record("crt1:"), Record("eboot:"));
        SceVersionTrailer.Register(p);
        using var s = SceVersionTrailer.OpenTrimmed(p);

        s.Seek(-4, SeekOrigin.End);
        var tail = new byte[16];
        Assert.Equal(4, s.Read(tail, 0, 16));      // clamped to the limit, not the real end

        s.Seek(s.Length + 100, SeekOrigin.Begin);
        Assert.Equal(0, s.Read(tail, 0, 16));      // past the limit reads nothing
    }

    [Fact]
    public void An_unregistered_file_is_untouched_even_when_another_is_registered()
    {
        SceVersionTrailer.Log = null;
        string a = Write("eboot.bin", Record("eboot:"));
        string b = Write("other.bin", Record("other:"));
        SceVersionTrailer.Register(a);

        using var s = SceVersionTrailer.OpenTrimmed(b);
        Assert.Equal(new FileInfo(b).Length, s.Length);
    }
}
