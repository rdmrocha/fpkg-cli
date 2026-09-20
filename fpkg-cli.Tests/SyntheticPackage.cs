using System.Diagnostics;
using System.Text.Json;

namespace Fpkg.Tests;

/// <summary>
/// A package built from generated data, for the tests that need a real one to read.
///
/// <para>Built once per test run by driving the CLI over a source tree this class writes, so the
/// package exercises the whole build path — inner PFS, NAPS spans, the PlayGo repair with its 31
/// language fillers, the CNT and the SI — rather than a hand-assembled approximation. It takes
/// about half a second and comes out around 3 MB.</para>
///
/// <para>The source is deterministic: a fixed RNG seed, fixed sizes, a nested directory, a file
/// whose name sorts differently under ordinal and case-insensitive comparison, and a zero-length
/// file. Every test reading it therefore sees the same package.</para>
/// </summary>
internal static class SyntheticPackage
{
    internal const string Passcode = "00000000000000000000000000000000";
    internal const string ContentId = "EP0001-PPSA00001_00-SYNTHETIC000000A";

    /// <summary>Files written into the source, with their sizes.</summary>
    internal static readonly (string Path, int Size)[] Contents =
    [
        ("Media/big.dat", 3_000_000),
        ("Media/small.dat", 1024),
        ("Media/Sub/nested.dat", 250_000),
        ("data.bin", 64_000),
        ("empty.txt", 0),
    ];

    private static readonly Lazy<string> Built = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The finished .pkg. Built on first use.</summary>
    internal static string Path => Built.Value;

    private static string Build()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                             "fpkg-synthetic-" + Guid.NewGuid().ToString("N")[..8]);
        string source = System.IO.Path.Combine(root, "src");
        string output = System.IO.Path.Combine(root, "out");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "sce_sys"));
        Directory.CreateDirectory(output);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        };

        File.WriteAllText(System.IO.Path.Combine(source, "sce_sys", "param.json"),
            JsonSerializer.Serialize(new
            {
                contentId = ContentId,
                titleId = "PPSA00001",
                masterVersion = "01.00",
                contentVersion = "01.000.000",
                applicationCategoryType = 0,
                localizedParameters = new
                {
                    defaultLanguage = "en-US",
                    enUS = new { titleName = "Synthetic" },
                },
            }).Replace("\"enUS\"", "\"en-US\""));

        // Fresh random for every block, not one block repeated: a repeated block compresses to
        // nothing and then no file is stored verbatim, which is exactly what the offset tests need
        // to find. Deterministic all the same -- one seed, a fixed file order, fixed sizes.
        var rng = new Random(7);
        var block = new byte[4096];
        foreach (var (relative, size) in Contents)
        {
            string path = System.IO.Path.Combine(source, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            for (int written = 0; written < size; written += block.Length)
            {
                rng.NextBytes(block);
                file.Write(block, 0, Math.Min(block.Length, size - written));
            }
        }

        Run(Cli(), ["build", "--source", source, "--out", output]);

        string[] built = Directory.GetFiles(output, "*.pkg");
        if (built.Length != 1)
            throw new InvalidOperationException(
                $"expected one .pkg in {output}, found {built.Length}");
        return built[0];
    }

    /// <summary>The repository's <c>fpkg</c> launcher, found by walking up from the test binary.</summary>
    private static string Cli()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "fpkg");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            $"no 'fpkg' launcher above {AppContext.BaseDirectory}; run the tests from the repository");
    }

    private static void Run(string program, string[] arguments)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"could not start {program}");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{program} {string.Join(' ', arguments)} exited {process.ExitCode}\n{output}\n{error}");
    }
}
