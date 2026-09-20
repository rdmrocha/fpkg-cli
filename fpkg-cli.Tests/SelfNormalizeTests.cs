using Fpkg.Cli;
using Xunit;

/// <summary>
/// The SELF repair, transcribed from Sony's generator. It rewrites executables, so the cases that
/// matter most are the ones where it must do nothing.
/// </summary>
public class SelfNormalizeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("selfnorm").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>One record: 00 00, u16 payload, 08, a colon-terminated name, the version twice.</summary>
    private static byte[] Record(string name)
    {
        var r = new byte[name.Length + 21];
        r[2] = (byte)(name.Length + 17);
        r[4] = 8;
        for (int i = 0; i < name.Length; i++) r[5 + i] = (byte)name[i];
        for (int i = 0; i < 8; i++) { r[5 + name.Length + i] = 0x11; r[13 + name.Length + i] = 0x11; }
        return r;
    }

    /// <summary>A SELF whose trailer starts <paramref name="early"/> bytes before the boundary.</summary>
    private string Build(string file, int bodyLength, int early, bool legacy = false)
    {
        byte[] trailer = [.. Record("crt1:"), .. Record("eboot:")];
        var body = new byte[bodyLength];
        for (int i = 0x20; i < body.Length; i++) body[i] = (byte)(i * 7);
        (legacy ? SelfNormalize.LegacyMagic : SelfNormalize.ProsperoMagic).CopyTo(body);
        // The header declares the boundary `early` bytes AFTER where the trailer really starts.
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            body.AsSpan(0x10), bodyLength + early);
        string path = Path.Combine(_dir, file);
        File.WriteAllBytes(path, [.. body, .. trailer]);
        return path;
    }

    [Fact]
    public void RecognisesAWellFormedTrailerChain()
    {
        Assert.True(SelfNormalize.IsCompleteSceVersion([.. Record("crt1:"), .. Record("libc:")]));
        Assert.False(SelfNormalize.IsCompleteSceVersion([]));
        Assert.False(SelfNormalize.IsCompleteSceVersion([.. Record("crt1:"), 0x01]));   // trailing junk
        Assert.False(SelfNormalize.IsCompleteSceVersion([.. Record("crt1:")[..^1]]));   // truncated
    }

    /// <summary>The name must be printable and colon-terminated, and the version doubled.</summary>
    [Fact]
    public void RejectsRecordsThatAreNotTheTrailer()
    {
        byte[] noColon = Record("crt1x"); Assert.False(SelfNormalize.IsCompleteSceVersion(noColon));
        byte[] unprintable = Record("crt1:"); unprintable[5] = 0x01;
        Assert.False(SelfNormalize.IsCompleteSceVersion(unprintable));
        byte[] mismatched = Record("crt1:"); mismatched[^1] = 0x22;
        Assert.False(SelfNormalize.IsCompleteSceVersion(mismatched));
    }

    [Theory]
    [InlineData(1)] [InlineData(8)] [InlineData(15)]
    public void MovesATrailerThatStartsEarlyToTheDeclaredBoundary(int early)
    {
        string path = Build("eboot.bin", 0x200, early);
        byte[] before = File.ReadAllBytes(path);
        var plan = SelfNormalize.PlanFor(path);
        Assert.NotNull(plan);
        Assert.Equal(early, plan!.Value.Padding);
        Assert.False(plan.Value.RewriteMagic);

        string outPath = Path.Combine(_dir, "out.bin");
        SelfNormalize.Write(path, outPath, plan.Value);
        byte[] after = File.ReadAllBytes(outPath);

        Assert.Equal(before.Length + early, after.Length);
        Assert.Equal(before[..0x200], after[..0x200]);                 // body untouched
        Assert.All(after[0x200..(0x200 + early)], b => Assert.Equal(0, b));
        Assert.Equal(before[0x200..], after[(0x200 + early)..]);       // trailer moved, not changed
    }

    /// <summary>A trailer already at the boundary is not a repair, and the file must not be rewritten.</summary>
    [Fact]
    public void LeavesAnAlreadyCorrectSelfAlone() =>
        Assert.Null(SelfNormalize.PlanFor(Build("ok.bin", 0x200, 0)));

    [Fact]
    public void IgnoresAFileThatIsNotASelf()
    {
        string path = Path.Combine(_dir, "data.bin");
        File.WriteAllBytes(path, new byte[0x400]);
        Assert.Null(SelfNormalize.PlanFor(path));
    }

    [Fact]
    public void IgnoresAFileTooShortToHaveAHeader()
    {
        string path = Path.Combine(_dir, "tiny.bin");
        File.WriteAllBytes(path, [.. SelfNormalize.ProsperoMagic, 0, 0, 0, 0]);
        Assert.Null(SelfNormalize.PlanFor(path));
    }

    /// <summary>A legacy PS4 magic is rewritten even when the trailer needs no move.</summary>
    [Fact]
    public void RewritesALegacyMagic()
    {
        string path = Build("legacy.bin", 0x200, 0, legacy: true);
        var plan = SelfNormalize.PlanFor(path);
        Assert.NotNull(plan);
        Assert.True(plan!.Value.RewriteMagic);

        string outPath = Path.Combine(_dir, "out2.bin");
        SelfNormalize.Write(path, outPath, plan.Value);
        byte[] after = File.ReadAllBytes(outPath);
        Assert.Equal(SelfNormalize.ProsperoMagic.ToArray(), after[..4]);
        Assert.Equal(new FileInfo(path).Length, after.Length);
    }

    /// <summary>A trailer more than 15 bytes early is outside the window Sony searches.</summary>
    [Fact]
    public void DoesNotReachBeyondTheFifteenByteWindow() =>
        Assert.Null(SelfNormalize.PlanFor(Build("far.bin", 0x200, 16)));
}
