using System.Security.Cryptography;
using Xunit;

internal static class Bytes
{
    internal static void AssertEqual(byte[] expected, byte[] actual)
    {
        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
            if (expected[i] != actual[i])
                Assert.Fail($"first difference at 0x{i:X} — expected 0x{expected[i]:X2}, got 0x{actual[i]:X2} " +
                            $"(lengths {expected.Length} vs {actual.Length})");
        Assert.Equal(expected.Length, actual.Length);
    }

    internal static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    internal static string Sha256Range(string path, long offset, long length)
    {
        using var s = File.OpenRead(path);
        s.Position = offset;
        using var sha = SHA256.Create();
        var buf = new byte[81920];
        while (length > 0)
        {
            int n = s.Read(buf, 0, (int)Math.Min(buf.Length, length));
            if (n <= 0) throw new EndOfStreamException(path);
            sha.TransformBlock(buf, 0, n, null, 0);
            length -= n;
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
