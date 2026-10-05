using System.Buffers.Binary;
using System.Security.Cryptography;
using Fpkg.Cli;
using Fpkg.Tests;
using Xunit;

/// <summary>
/// LibProsperoPkg 0.6.11 normalises the executables it visits itself (eboot.bin, *.prx, fakelib/,
/// sce_module/), byte for byte the way <see cref="SelfNormalize"/> does. fpkg's staging therefore
/// leaves exactly those to the library and keeps every other SELF. The oracle is the package: the
/// split must come out identical to fpkg normalising everything.
/// </summary>
public class SelfNormalizeScopeTests
{
    [Theory]
    [InlineData("eboot.bin", true)]
    [InlineData("sce_module/libfoo.prx", true)]
    [InlineData("sce_module/deep/x.sprx", true)]
    [InlineData("fakelib/libbar.sprx", true)]
    [InlineData("bin/plugin.PRX", true)]
    [InlineData("bin/tool.self", false)]
    [InlineData("bin/eboot.bin", false)]
    [InlineData("Eboot.bin", false)]
    [InlineData("Sce_module/x.sprx", false)]
    [InlineData("sce_module", false)]
    public void TheLibrarysScopeIsMatchedExactly(string relative, bool library) =>
        Assert.Equal(library, Program.SelfNormalizeStaging.NormalisedByLibrary(
            relative.Replace('/', Path.DirectorySeparatorChar)));

    private static byte[] Record(string name)
    {
        var r = new byte[name.Length + 21];
        r[2] = (byte)(name.Length + 17);
        r[4] = 8;
        for (int i = 0; i < name.Length; i++) r[5 + i] = (byte)name[i];
        for (int i = 0; i < 8; i++) { r[5 + name.Length + i] = 0x11; r[13 + name.Length + i] = 0x11; }
        return r;
    }

    private static void WriteSelf(string root, string relative, int body, int early, bool legacy)
    {
        var bytes = new byte[body];
        for (int i = 0x20; i < body; i++) bytes[i] = (byte)(i * 7);
        (legacy ? SelfNormalize.LegacyMagic : SelfNormalize.ProsperoMagic).CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x10), body + early);
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string stem = Path.GetFileNameWithoutExtension(relative);
        File.WriteAllBytes(path, [.. bytes, .. Record("crt1:"), .. Record(stem + ":")]);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Fact]
    public void LeavingTheLibrarysExecutablesToTheLibraryChangesNoByte()
    {
        string source = Directory.CreateTempSubdirectory("selfscope").FullName;
        Directory.CreateDirectory(Path.Combine(source, "sce_sys"));
        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), SyntheticPackage.ParamJson);
        WriteSelf(source, "eboot.bin", 0x4000, early: 3, legacy: false);
        WriteSelf(source, "sce_module/libfoo.prx", 0x3000, early: 7, legacy: false);
        WriteSelf(source, "fakelib/libbar.sprx", 0x2000, early: 0, legacy: true);
        WriteSelf(source, "bin/tool.self", 0x2400, early: 5, legacy: false);
        WriteSelf(source, "bin/old.elf", 0x2400, early: 0, legacy: true);

        string split = SyntheticPackage.BuildAt(source, "selfsplit",
            ["--temp-dir", Directory.CreateTempSubdirectory("selfsplit-t").FullName]);
        string forced = SyntheticPackage.BuildAt(source, "selfforced",
            ["--force-self-normalize", "--temp-dir", Directory.CreateTempSubdirectory("selfforced-t").FullName]);

        Assert.Equal(Sha256(forced), Sha256(split));
    }

    /// <summary>
    /// The old spelling must fail, not be ignored: ParseFlags accepts any --name, so a silently
    /// unknown flag would hand a script the default it asked to avoid.
    /// </summary>
    [Fact]
    public void TheOldFlagNameIsRefusedWithThePointerToTheNewOne()
    {
        var (exit, log) = SyntheticPackage.RunFpkg(
            ["build", "--source", "/nonexistent", "--out", "/nonexistent-out", "--no-self-normalize"]);
        Assert.Equal(1, exit);
        Assert.Contains("renamed --no-extra-self-normalize", log);
    }

    [Fact]
    public void TheNewFlagSkipsOnlyFpkgsPart()
    {
        string source = Directory.CreateTempSubdirectory("selfextra").FullName;
        Directory.CreateDirectory(Path.Combine(source, "sce_sys"));
        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), SyntheticPackage.ParamJson);
        WriteSelf(source, "eboot.bin", 0x4000, early: 3, legacy: false);
        WriteSelf(source, "bin/tool.self", 0x2400, early: 5, legacy: false);

        var (exit, log) = SyntheticPackage.RunFpkg(
            ["build", "--source", source, "--out", Directory.CreateTempSubdirectory("selfextra-o").FullName,
             "--temp-dir", Directory.CreateTempSubdirectory("selfextra-t").FullName, "--no-extra-self-normalize"]);
        Assert.True(exit == 0, log);
        Assert.DoesNotContain("self:   normalised", log);

        (exit, log) = SyntheticPackage.RunFpkg(
            ["build", "--source", source, "--out", Directory.CreateTempSubdirectory("selfextra-o2").FullName,
             "--temp-dir", Directory.CreateTempSubdirectory("selfextra-t2").FullName]);
        Assert.True(exit == 0, log);
        Assert.Contains("normalised 1 executable(s) the way the Windows SDK does: bin/tool.self", log);
    }
}
