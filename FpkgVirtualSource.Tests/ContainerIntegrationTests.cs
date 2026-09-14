using System.Security.Cryptography;
using Xunit;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// Runs against a real .ffpfsc built from a real dump. Skipped when the fixture is absent so
/// the suite still passes on a machine without one.
///
/// Build the fixture with MkPFS:
///   python3 -m venv /tmp/mkvenv &amp;&amp; /tmp/mkvenv/bin/pip install cryptography
///   cd ~/Developer/MkPFS &amp;&amp; /tmp/mkvenv/bin/python -m mkpfs pack folder /tmp/out2 /tmp/out2.ffpfsc
/// </summary>
public class ContainerIntegrationTests
{
    private const string Fixture = "/tmp/out2.ffpfsc";
    private const string SourceTree = "/tmp/out2";

    [SkippableFact]
    public void EveryFileMatchesTheOriginalTreeByteForByte()
    {
        Skip.IfNot(File.Exists(Fixture) && Directory.Exists(SourceTree),
                   $"needs {Fixture} and {SourceTree}");

        using var container = Container.Open(Fixture);
        string root = container.FindAppRoot()
            ?? throw new InvalidOperationException("no unique app root in the fixture");
        string prefix = root.Length == 0 ? "" : root + "/";

        int checked_ = 0;
        foreach (var entry in container.Files)
        {
            if (!entry.RelativePath.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string onDisk = Path.Combine(SourceTree, entry.RelativePath[prefix.Length..]);
            if (!File.Exists(onDisk)) continue;

            Assert.Equal(new FileInfo(onDisk).Length, entry.Length);
            using var s = container.Reader.OpenFile(entry);
            using var d = File.OpenRead(onDisk);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(d)),
                         Convert.ToHexString(SHA256.HashData(s)));
            checked_++;
        }
        Assert.True(checked_ > 0, "the fixture and the source tree share no files");
    }

    [SkippableFact]
    public void FindsTheAppRootAndItsParamJson()
    {
        Skip.IfNot(File.Exists(Fixture), $"needs {Fixture}");
        using var container = Container.Open(Fixture);
        string? root = container.FindAppRoot();
        Assert.NotNull(root);

        string handle = ContainerRegistry.Register(container);
        try
        {
            string rel = root!.Length == 0 ? "sce_sys/param.json" : root + "/sce_sys/param.json";
            using var s = VirtualSource.Open(VirtualSource.PathFor(handle, rel));
            using var r = new StreamReader(s);
            Assert.Contains("contentId", r.ReadToEnd());
        }
        finally { ContainerRegistry.Release(handle); }
    }
}
