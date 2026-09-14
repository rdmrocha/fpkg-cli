using System.Security.Cryptography;

namespace Fpkg.Cli;

/// <summary>
/// <c>fpkg patch [--oodle] [--ffpfsc]</c> — applies the dnlib patches to one copy of
/// LibProsperoPkg and records which went in.
///
/// This was patch.sh, which needed the repo, the .NET SDK and two <c>dotnet run</c>
/// invocations. The whole point of moving it here is that a user can unzip the
/// distribution into any LibProsperoPkg release folder and run <c>./fpkg patch</c> with
/// nothing else installed, so both patchers are invoked in-process and the shim assemblies
/// they need are the ones shipped in fpkg-tools/bin/.
///
/// ORDERING, which is the subtle part. This runs inside a process whose LibraryResolver has
/// already chosen which LibProsperoPkg to load, and it is about to overwrite that very file.
/// Two things keep that safe, and both are deliberate:
///   * Nothing on this path touches a LibProsperoPkg type. LibraryResolver's module
///     initializer only reads stamps and registers hooks — it does not load the library —
///     and Main dispatches to this command before any command that would. dnlib reads the
///     stock DLL as data, which is unaffected by whether the CLR has it mapped.
///   * The result is staged in a temp directory and moved into place with a rename, never
///     written over the live file. A rename replaces the directory entry; any existing
///     mapping keeps the old inode. So even a future caller that has already loaded
///     LibProsperoPkg.patched.dll cannot hit ETXTBSY or corrupt a mapped image.
///
/// Every run starts from the STOCK LibProsperoPkg.dll, never from an existing patched copy,
/// so a single-patch run can never doubly-patch an already-patched module. The stamp records
/// only the patches applied by THIS run.
/// </summary>
internal static class PatchCommand
{
    internal static int Run(string[] args)
    {
        bool doOodle = true, doFfpfsc = true;
        if (args.Length > 0)
        {
            doOodle = doFfpfsc = false;
            foreach (string arg in args)
                switch (arg)
                {
                    case "--oodle": doOodle = true; break;
                    case "--ffpfsc": doFfpfsc = true; break;
                    default: return Usage();
                }
        }

        // A bare "--oodle" is an explicit request for exactly that one patch, so a missing
        // Oodle library is a hard failure rather than the best-effort skip the default
        // (no flags, or both flags together) uses.
        bool strictOodle = doOodle && !doFfpfsc;

        string? releaseFolder = LibraryResolver.FindReleaseFolder();
        if (releaseFolder is null)
            return Fail("LibProsperoPkg.dll was not found. Unzip fpkg and fpkg-tools/ into " +
                        "the release folder that holds LibProsperoPkg.dll and run ./fpkg patch there.");

        string stock = Path.Combine(releaseFolder, PatchStamp.StockDllName);
        string outDll = Path.Combine(releaseFolder, PatchStamp.UnifiedDllName);
        string stampPath = Path.Combine(releaseFolder, PatchStamp.UnifiedStampName);

        string work = Directory.CreateTempSubdirectory("fpkg-patch-").FullName;
        var applied = new List<string>();
        try
        {
            string stage = Path.Combine(work, "stage.dll");
            File.Copy(stock, stage);

            if (doOodle)
            {
                // The Oodle leg is only worth applying when there is something for it to
                // bind to. OodleLibrary is the single definition of where the user's RAD
                // Oodle build is looked for, shared with the backend that will load it at
                // build time, so the two can never disagree about "is Oodle available".
                string? oodle = PprPfsKrakenTool.OodleLibrary.Find();
                if (oodle is not null)
                {
                    string backend = Locate(releaseFolder, "PprPfsKrakenTool.dll")
                        ?? throw new InvalidOperationException(
                            "PprPfsKrakenTool.dll is missing from this install; " +
                            "re-unzip the distribution.");

                    // tools/OodlePatcher takes <source> [backendDll] and always writes its
                    // output as LibProsperoPkg.oodle(.dll|.stamp) beside <source> — it does
                    // not take an output path.
                    int rc = OodlePatcher.OodlePatcherProgram.Run([stage, backend]);
                    if (rc != 0) return rc;
                    File.Move(Path.Combine(work, "LibProsperoPkg.oodle.dll"), stage, overwrite: true);
                    File.Delete(Path.Combine(work, "LibProsperoPkg.oodle.stamp"));
                    applied.Add("oodle");
                    Console.Error.WriteLine($"oodle: bound to {oodle}");
                }
                else if (strictOodle)
                {
                    Console.Error.WriteLine("error: no RAD Oodle library was found.");
                    Console.Error.WriteLine("  " + PprPfsKrakenTool.OodleLibrary.DescribeSearch());
                    Console.Error.WriteLine("  Copy the Oodle Core library out of an OodleUE 2.9.16 SDK");
                    Console.Error.WriteLine("  into fpkg-tools/native/ (see NOTES.md).");
                    return 1;
                }
                else
                {
                    Console.Error.WriteLine(
                        "note: skipping oodle — no RAD Oodle library found. Drop " +
                        $"{PprPfsKrakenTool.OodleLibrary.FileNames.FirstOrDefault() ?? "the Oodle Core library"} " +
                        "into fpkg-tools/native/ and re-run ./fpkg patch to enable it.");
                }
            }

            if (doFfpfsc)
            {
                // The patcher imports the shim's entry points into the target module and
                // requires the shim to sit beside its OUTPUT, so it is staged alongside.
                string shim = Locate(releaseFolder, "FpkgVirtualSource.dll")
                    ?? throw new InvalidOperationException(
                        "FpkgVirtualSource.dll is missing from this install; re-unzip the distribution.");
                File.Copy(shim, Path.Combine(work, "FpkgVirtualSource.dll"), overwrite: true);

                string next = Path.Combine(work, "next.dll");
                int rc = VirtualSourcePatcher.VirtualSourcePatcherProgram.Run([stage, next]);
                if (rc != 0) return rc;
                File.Move(next, stage, overwrite: true);
                applied.Add("ffpfsc");
            }

            // Rename, not copy: see the ordering note above.
            File.Move(stage, outDll, overwrite: true);
            string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stock)));
            File.WriteAllText(stampPath,
                $"{{\"source\":\"{sha}\",\"patches\":[{string.Join(",", applied.Select(p => $"\"{p}\""))}]}}\n");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception) { /* best effort */ }
        }

        Console.WriteLine($"patched {PatchStamp.UnifiedDllName}: " +
                          (applied.Count == 0 ? "(nothing)" : string.Join(", ", applied)));

        if (!applied.Contains("ffpfsc"))
        {
            Console.Error.WriteLine(
                $"note:  this {PatchStamp.UnifiedDllName} carries NO ffpfsc leg, so container");
            Console.Error.WriteLine(
                "       sources (--source <file.ffpfsc>) will be REFUSED at build time.");
            Console.Error.WriteLine("       Run ./fpkg patch with no flags to apply every patch again.");
        }
        return 0;
    }

    /// <summary>
    /// Where the shipped support assemblies are found: the CLI's own directory
    /// (fpkg-tools/bin/, where the distribution puts them) first, then the release folder,
    /// which is where an in-repo checkout has historically kept its copies.
    /// </summary>
    private static string? Locate(string releaseFolder, string fileName)
    {
        foreach (string candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, fileName),
                     Path.Combine(releaseFolder, fileName),
                 })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: fpkg patch [--oodle] [--ffpfsc]");
        Console.Error.WriteLine("  no flags        apply every available patch");
        Console.Error.WriteLine("  --oodle         apply only the Oodle patch (fails if no Oodle library)");
        Console.Error.WriteLine("  --ffpfsc        apply only the virtual-source patch");
        Console.Error.WriteLine("  both flags      same as no flags");
        return 2;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }
}
