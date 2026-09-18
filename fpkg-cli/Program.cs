using System.Buffers.Binary;
using System.IO.Enumeration;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using LibProsperoPkg;
using LibProsperoPkg.PKG;

namespace Fpkg.Cli;

/// <summary>
/// Native macOS front end for LibProsperoPkg. The shipped LibProsperoPkg.Gui is
/// WinForms and needs Microsoft.WindowsDesktop.App, which does not exist off
/// Windows; the library itself is plain net9.0, so it is driven directly here.
/// Option handling, validation and post-build verification mirror MainForm.
/// </summary>
internal static class Program
{
    private static readonly Regex ContentIdPattern =
        new(@"^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_00-[A-Z0-9]{16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Mirrors ProsperoContentVersion.TryParse: canonical NN.NNN.NNN, with the
    // legacy NN.NN API form accepted and expanded with a zero patch component.
    private static readonly Regex VersionPattern =
        new(@"^[0-9]{1,2}\.[0-9]{1,3}(\.[0-9]{1,3})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] PlayGoFiles = ["playgo-chunk.dat", "playgo-hash-table.dat", "playgo-ficm.dat"];

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0] switch
            {
                "keys"   => Keys(),
                "info"   => Info(args),
                "verify" => Verify(args),
                "extract" => Extract(args),
#if LIB_HAS_ARCHIVE_067
                "template" => Template(args),
#endif
                "version" => Version(),
                "build"  => Build(args),
                // Dispatched before anything that touches a LibProsperoPkg type, and
                // deliberately so: PatchCommand is about to rewrite that very assembly.
                // See the ordering note in PatchCommand.
                "patch"  => PatchCommand.Run(args.Skip(1).ToArray()),
                "repair-playgo" => RepairPlayGo.RepairPlayGoCommand.Run(args),
                "api"    => Api(args),
                var cmd  => Fail($"unknown command '{cmd}' (try: fpkg help)"),
            };
        }
        catch (RenamerLikeError ex)
        {
            return Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("canceled");
            return 130;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, ex);
        }
    }

    private static void Usage() => Console.WriteLine(
        $"""
        fpkg {CliVersion} — LibProsperoPkg on macOS and Linux

          fpkg version                  report this CLI's version and the LibProsperoPkg
                                        release actually loaded
          fpkg keys                     report whether publishing key material is wired in
          fpkg info <file.pkg>          detect container type and dump the CNT header/entries
          fpkg verify <file.pkg> [mode] structural FIH check (mode: Native | PlaintextNoAuth)
          fpkg verify <file.pkg> --quick | --full [--passcode <32>] [--workers <n>]
                                        the library's own verifiers. --quick checks
                                        segment ranges, FIH/CNT cross-references, entry
                                        digests, the outer superblock ICV and the SI
                                        directory; --full also decodes every outer block,
                                        NAPS chunk and inner file. Exit 1 on any issue.
          fpkg api [filter]             list the library's public surface (substring filter)
          fpkg patch [--oodle] [--ffpfsc]
                                        patch LibProsperoPkg in this folder. No flags applies
                                        every available patch: oodle (needs a RAD Oodle
                                        library in fpkg-tools/native/, skipped with a note
                                        when absent) and ffpfsc (container sources).
                                        --oodle alone FAILS if no Oodle library is present.
          fpkg repair-playgo <file.pkg> [--passcode <32>] [--out <path>|--in-place] [--dry-run]
                                        rewrites a 0.6.8-built package's PlayGo metadata to the
                                        0.6.9 shape without touching the payload. Reports and
                                        writes nothing unless --out or --in-place is given.
          fpkg extract <file.pkg> <dir> [--passcode <32>] [--raw] [--cnt] [--si]
                                        unpack the inner PPR-PFS files (default),
                                        --raw keeps files compressed, --cnt/--si add
                                        the CNT entries / debug SI ZIP
          fpkg extract <file.pkg> <dir> --rebuild-source [--passcode <32>]
                                        CNT entries only, written back to the sce_sys/
                                        paths a rebuild reads them from, with the
                                        container-generated and derived entries dropped.
                                        Never opens the inner PFS.
          fpkg template <file.pkg> <dir> [--passcode <32>]
                                        export a rebuildable additional-content template:
                                        the sce_sys inputs plus a GP5 project. Data-bearing
                                        AC packages only; the payload is NOT exported.
          fpkg build --source <dir> --out <dir> [options]

        build options
          --source <path>         source folder containing sce_sys/, or a .ffpfsc / .exfat
                                  container (containers need ./fpkg patch)      (required)
          --out <dir>             folder the finished *.pkg is written to     (required)
          --content-id <id>       36-char content id; read from param.json when omitted
          --version <NN.NNN.NNN>  content version (legacy NN.NN accepted);
                                  read from param.json, else 01.00
          --title <text>          title for a generated param.json; read from param.json
          --title-id <id>         9-char title id; derived from the content id when omitted
          --passcode <32 chars>   defaults to 32 zeroes
          --mode <name>           Application | Homebrew | AdditionalContentData
                                  | AdditionalContentNoData                   (default Application)
          --image-mode <name>     PlaintextNoAuth | Native            (default PlaintextNoAuth,
                                  matching the GUI; needs the A53 PPR read selector on
                                  the console. Use Native for publisher-compatible output.)
          --format <name>         DebugImage | MetadataContainer | RetailImage (default DebugImage)
          --playgo <1-255>        generated PlayGo chunk count      (library default: 100)
                                  Pass 1 for the publisher nwonly profile. Ignored when the
                                  source carries playgo-chunk.dat or playgo-scenario.json, or
                                  when a GP5 declares chunk_info. Chunk zero holds the game
                                  files; each selected language gets a small chunk of its own,
                                  and any chunk past the language count is empty by design.
          --playgo-languages <l>  comma-separated PlayGo language codes, or "all" (default all)
                                  Also picks the default language written into
                                  playgo-chunk.dat: en-US when present, else the first set.
          --app-drm <free|standard>
                                  applicationDrmType for an Application volume (default standard,
                                  matching the GUI). free sets drm_type 0 and emits no debug
                                  licence; standard sets drm_type 16 and generates one when the
                                  source has none.
          --ac-drm <free|entitlement>
                                  applicationDrmType for AdditionalContentData
                                                                       (default entitlement)
                                  free also OMITS license.dat and license.info entirely - one
                                  library predicate drives drm_type and the licence together.
          --compress              store the inner pfs_image.dat PFSC-compressed
          --temp-dir <dir>        holds the intermediate CNT/inner/outer images
                                  (default: the OS temp dir; must be outside --source)
          --sdk-version <n|0xHEX|keep>
                                  SDK to stamp: a major generation (1-11, resolved to that
                                  release's canonical value) or a full 64-bit executable
                                  identifier.                                  (default 1)
                                  Rewrites the .sceversion trailer in SELF binaries;
                                  param.json gets the canonical major/minor form.
                                  This ALSO SETS requiredSystemSoftwareVersion to the same
                                  version, which is how a dump demanding newer firmware than
                                  you have gets brought down: with the default of 1 a build
                                  declares firmware 1.00. "keep" leaves both the source's
                                  sdkVersion and its firmware requirement untouched.
          --kraken-backend <name> Auto | Oodle | BuiltIn | Automatic |
                                  Uncompressed                                   (default Auto)
                                  Auto picks Oodle when the native backend is available and
                                  BuiltIn when it is not, and says which it chose. It never
                                  stores uncompressed.
                                  Oodle is the native RAD encoder, bound from the Oodle
                                  library in fpkg-tools/native/ once ./fpkg patch has applied
                                  the oodle leg; it throws if unavailable.
                                  BuiltIn is the managed Kraken encoder and always works.
                                  Automatic is the LIBRARY's mode and is not the same as Auto:
                                  it falls back to storing every kernel-facing block
                                  UNCOMPRESSED (~3.75x larger, no warning) when the Oodle
                                  backend is missing. Prefer Auto.
                                  Oodle output is a generic Reduced-profile bitstream, not
                                  publisher-identical - see NOTES.md, "Native Oodle on macOS".
          --kraken-level <-4..9>  inner-image Kraken level, -4 fastest
                                  Default follows the backend: 7 for Oodle, 6 for BuiltIn.
                                  BuiltIn builds a suffix trie and runs a full DP parse at
                                  7 and above - measured 22.01s vs 5.60s for 1.06% on a
                                  679 MB dump. Oodle bypasses that machinery, so 7 is nearly
                                  free and is its optimum (8 and 9 are slower AND larger).
          --kraken-threads <n>    concurrent Kraken encoders, 0 = CPU count    (default 0)
          --no-deterministic      allow a random outer-PFS seed (deterministic is the default)
          --no-param-json         do not synthesise a missing param.json
          --no-verify             skip both post-build checks: the structural FIH inspection
                                  and the library's quick verifier (segment ranges, CNT and
                                  entry digests, PlayGo layout, outer superblock ICV, NAPS
                                  layout, inner inode metadata, SI directory). The quick pass
                                  costs about a second on a 650 MB package; a failure fails the
                                  build, though the package is still written so it can be
                                  inspected.
          --retain-sce-sys        keep every sce_sys file. By default license.*, playgo-*,
                                  origin-param.json and target-param.json are set aside for
                                  the build and put back afterwards: a retail dump's copies
                                  override what the builder regenerates for the new image.
                                  Required when building an exported AC template, whose
                                  licence and playgo files are deliberate inputs.
          --no-media-repair       pack sce_sys PNGs exactly as they are. By default icon0,
                                  pic0, pic1 and pic2 .png are validated (signature, chunk
                                  chain, CRC-32, IEND, no trailing bytes) and a corrupt one
                                  is set aside so the builder rebuilds it from its .dds;
                                  icon0 and pic0 have no reverse path in the library, so those
                                  two are regenerated here. Corrupt with no usable .dds fails.
          --v3                    alias for --pfs-format v3. Like the GUI's "PFS v3", this
                                  alone changes nothing measurable - see --shuffle-analysis.
          --pfs-format <v2|v3>    PFS compression metadata format             (default v2)
          --entitlement-key <hex> 32 hex chars (16 bytes) for AC/AL dev licences
          --gp5 <file.gp5>        build from a GP5 project instead of a loose tree
          --no-coalescing         disable outer-block coalescing (on by default)
          --no-relocation-align   disable relocation alignment adjustment (on by default)
          --shuffle-analysis      evaluate every shuffle during compression (PFSv3 only).
                                  EXPERIMENTAL: the only thing that actually shrinks a v3
                                  image (~2.7% on a real dump, ~5% on texture-heavy data,
                                  for ~4.3x the build time), but a shuffled image failed to
                                  load on a console here and the GUI leaves it off.
          --quiet                 suppress per-step progress output

        Press Ctrl+C during a build to cancel it cleanly.
        """);

    /// <summary>
    /// True when the native RAD Oodle backend can actually be reached for this build.
    /// Two things must hold, and checking only one of them gives a wrong answer:
    /// the loaded LibProsperoPkg must be the PATCHED copy (a stock assembly never calls
    /// into PprPfsKrakenTool at all, so a present Oodle library would be irrelevant), and
    /// the backend must be able to bind that Oodle library.
    /// </summary>
    private static bool NativeOodleAvailable()
    {
        try
        {
            bool patched = typeof(ProsperoBuildOptions).Assembly
                .GetReferencedAssemblies()
                .Any(a => a.Name == "PprPfsKrakenTool");
            if (!patched) return false;

            // Load it explicitly: the probe runs before the build, so nothing has caused
            // LibProsperoPkg to pull the backend in yet. LibraryResolver supplies it from
            // the release folder.
            var backend = Assembly.Load("PprPfsKrakenTool")
                .GetType("PprPfsKrakenTool.OodleBackend");
            return backend?.GetMethod("IsAvailable")?.Invoke(null, [null]) is true;
        }
        catch
        {
            // Missing assembly, missing Oodle library, ABI mismatch - all mean "not available",
            // and Auto falls back to BuiltIn rather than failing the build.
            return false;
        }
    }

#if HAS_VIRTUAL_SOURCE
    /// <summary>
    /// True when the LibProsperoPkg actually loaded carries the virtual-source (ffpfsc) leg.
    ///
    /// Deliberately asks the LOADED module, not a stamp file: the patcher imports the shim's
    /// entry points into the target module, so a module carrying the ffpfsc leg necessarily
    /// holds an assembly reference to FpkgVirtualSource and a module without it necessarily
    /// does not. That is the same technique <see cref="NativeOodleAvailable"/> uses for the
    /// Oodle leg (PprPfsKrakenTool), and unlike a stamp it cannot be wrong about the bytes that
    /// are running. <see cref="LibraryResolver.Selection"/>'s
    /// <see cref="PatchSelection.FfpfscApplied"/> is the stamp's own claim and agrees with this
    /// whenever the stamp is honest; the assembly reference is what is trusted.
    ///
    /// This matters because the failure it prevents is silent: with an unpatched
    /// <c>Populate</c>, TreeOverlay never runs, nothing populates the tree, and the build
    /// writes an EMPTY package that still passes the post-build verification.
    /// </summary>
    private static bool VirtualSourcePatchLoaded()
    {
        try
        {
            return typeof(ProsperoBuildOptions).Assembly
                .GetReferencedAssemblies()
                .Any(a => a.Name == "FpkgVirtualSource");
        }
        catch
        {
            return false;
        }
    }
#endif

    /// <summary>
    /// Why Auto did not pick Oodle. Three outcomes need to be distinguishable: the loaded
    /// LibProsperoPkg has no oodle leg at all (run ./fpkg patch), no RAD Oodle library was
    /// supplied (drop one in fpkg-tools/native/), or one was supplied and refused — the
    /// dangerous case, where a version whose structure layouts differ from the 2.9.16
    /// headers the binding was transcribed from is rejected rather than silently
    /// mis-encoding. The backend's own text is surfaced for the last two.
    /// </summary>
    private static string WhyNoOodle()
    {
        try
        {
            bool patched = typeof(ProsperoBuildOptions).Assembly
                .GetReferencedAssemblies()
                .Any(a => a.Name == "PprPfsKrakenTool");
            if (!patched)
                return "the loaded LibProsperoPkg carries no oodle leg; run ./fpkg patch";

            string? why = Assembly.Load("PprPfsKrakenTool")
                .GetType("PprPfsKrakenTool.OodleBackend")
                ?.GetProperty("Unavailability")?.GetValue(null) as string;
            if (why is null) return "native Oodle unavailable";

            // The "not found" case is the ordinary one and its full text lists every
            // directory searched, which is far too much for a one-line build note. The
            // binding-failed case is the rare and dangerous one, so that text is kept
            // whole: a user with the wrong Oodle version needs to be told exactly that.
            return why.StartsWith("no RAD Oodle library found", StringComparison.Ordinal)
                ? "no RAD Oodle library in fpkg-tools/native/; add one and re-run ./fpkg patch"
                : why;
        }
        catch (Exception)
        {
            return "native Oodle unavailable";
        }
    }

    /// <summary>
    /// Identifies the release actually bound at runtime. The library carries no version API,
    /// so this reports the three signals that do exist: the assembly's informational version
    /// (which embeds the upstream commit), the GUI's file version beside it (the number the
    /// release is published under), and a hash of the DLL for exactness.
    /// </summary>
    private static string DescribeLibrary()
    {
        var asm = typeof(ProsperoPackageBuilder).Assembly;
        string path = asm.Location;
        string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                         ?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "unknown";

        string release = "unknown";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            string gui = Path.Combine(dir ?? ".", "LibProsperoPkg.Gui.dll");
            if (File.Exists(gui))
            {
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(gui);
                release = v.FileVersion ?? release;
            }
        }
        catch (Exception) { /* diagnostics only; never fail a build over this */ }

        string sha = "?";
        try
        {
            using var fs = File.OpenRead(path);
            sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs))[..12];
        }
        catch (Exception) { }

        return $"release {release}, {info}, sha256 {sha}";
    }

    /// <summary>
    /// This CLI's own version, from ../VERSION at build time (see fpkg.csproj). The
    /// informational version carries a "+&lt;sha&gt;" source-revision suffix when built from a
    /// git tree; the number is what a user is being asked to compare against a release, so
    /// the suffix is cut here.
    /// </summary>
    internal static string CliVersion
    {
        get
        {
            string v = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            int plus = v.IndexOf('+');
            return plus < 0 ? v : v[..plus];
        }
    }

    private static int Version()
    {
        var asm = typeof(ProsperoPackageBuilder).Assembly;
        Console.WriteLine($"fpkg {CliVersion}");
        Console.WriteLine($"LibProsperoPkg: {DescribeLibrary()}");
        Console.WriteLine($"  loaded from:  {asm.Location}");
        // Report patches for whichever file was ACTUALLY loaded, not just whichever stamp
        // happens to exist on disk: LibraryResolver may have rejected a stale
        // LibProsperoPkg.patched.dll and fallen back to LibProsperoPkg.oodle.dll (or the
        // stock DLL), and that leftover patched.stamp must not be attributed to the wrong
        // assembly here.
        //
        // This reads LibraryResolver's own decision rather than re-parsing the stamp. There is
        // exactly one stamp reader (PatchStamp) and it is guarded; the second, unguarded copy
        // that used to live here threw on a malformed 'patches' array instead of reporting
        // "none" the way the resolver already did.
        var selection = LibraryResolver.Selection;
        string patches = selection.Path is string selected &&
                         string.Equals(selected, asm.Location, StringComparison.OrdinalIgnoreCase) &&
                         selection.Patches.Count > 0
            ? string.Join(", ", selection.Patches)
            : "none";
        Console.WriteLine($"  patches:      {patches}");
        return 0;
    }

    private static int Keys()
    {
        Console.WriteLine($"KeysAvailable: {ProsperoPackageBuilder.KeysAvailable}");
        return ProsperoPackageBuilder.KeysAvailable ? 0 : 2;
    }

    private static int Info(string[] args)
    {
        if (args.Length < 2) return Fail("info needs a package path");
        var path = Path.GetFullPath(args[1]);
        if (!File.Exists(path)) return Fail($"no such file: {path}");

        Console.WriteLine($"File:   {path}");
        Console.WriteLine($"Size:   {new FileInfo(path).Length:N0} bytes");
        Console.WriteLine($"Type:   {ProsperoPkgReader.DetectType(path)}");
        Console.WriteLine();
        Dump("pkg", ProsperoPkgReader.Read(path), 0);
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 2) return Fail("verify needs a package path");
        var pkg = Path.GetFullPath(args[1]);
        // verify has two argument shapes: a bare positional mode ("verify <pkg> PlaintextNoAuth")
        // and the 0.6.7 flags. ParseFlags rejects any non-"--" argument and skips its own first
        // element, so hand it the slice starting one BEFORE the first flag; with no flags at all
        // it must not see the positional mode.
        var firstFlag = Array.FindIndex(args, a => a.StartsWith("--", StringComparison.Ordinal));
        var flags = firstFlag < 1
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : ParseFlags(args[(firstFlag - 1)..]);

#if LIB_HAS_ARCHIVE_067
        // Bare `fpkg verify <pkg> [mode]` keeps its original meaning — the structural FIH check
        // this CLI has always done, and what a build runs on its own output. --quick and --full
        // are opt-in routes to the library's own verifiers, added in 0.6.7.
        if (flags.ContainsKey("quick") || flags.ContainsKey("full"))
        {
            var passcode = flags.GetValueOrDefault("passcode") ?? new string('0', 32);
            var full = flags.ContainsKey("full");
            var workers = flags.TryGetValue("workers", out var w) && int.TryParse(w, out var n) ? n : 0;

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

            Console.WriteLine($"Package: {pkg}");
            Console.WriteLine($"Mode:    {(full ? "full" : "quick")}\n");
            var result = full
                ? ProsperoPackageArchive.VerifyPackageFull(
                      pkg, passcode, cancellation.Token, null, line => Console.WriteLine($"  {line}"), workers)
                : ProsperoPackageArchive.VerifyPackageQuick(
                      pkg, passcode, cancellation.Token, null, line => Console.WriteLine($"  {line}"));

            Console.WriteLine();
            foreach (var check in result.Checks) Console.WriteLine($"  ok: {check}");
            foreach (var issue in result.Issues) Console.WriteLine($"  ISSUE: {issue}");
            // Reported after the library's own findings, and separately, because it is a warning
            // about a structurally valid package rather than one of its issues.
            var initialChunks = PlayGoInitialChunkProblem(pkg, passcode);
            if (initialChunks is not null) Console.WriteLine($"  WARNING: {initialChunks}");
            Console.WriteLine($"\n{result.Issues.Count} issue(s)" +
                              (initialChunks is null ? "" : ", 1 warning"));
            return result.Issues.Count == 0 ? 0 : 1;
        }
#else
        foreach (var unsupported in new[] { "quick", "full", "workers" })
            if (flags.ContainsKey(unsupported))
                return Fail($"--{unsupported} needs LibProsperoPkg 0.6.7 or newer");
#endif

        var expectPlaintextMarker = args.Length > 2 &&
            args[2].Equals("PlaintextNoAuth", StringComparison.OrdinalIgnoreCase);
        VerifyOutput(pkg, expectPlaintextMarker);
        return 0;
    }

#if LIB_HAS_ARCHIVE_067
    /// <summary>
    /// Reports a PlayGo map whose files live outside the scenario's initial chunk set.
    ///
    /// The library's own verifier does not catch this. Its scenario check is
    /// <c>total &lt; 1 || total &gt; chunkCount || initial &gt; total</c>, so an initial count of 1
    /// is structurally legal and passes — which it is, per Publishing Tools, whose
    /// <c>--initial_chunk_count</c> is documented as "the number of initial chunks" and validated
    /// only as <c>1 &lt;= initial &lt;= len(sequence)</c>.
    ///
    /// It is nonetheless wrong for a package that is installed whole. initial_chunk_count names
    /// the leading run of the download order that has to be present before the title can start;
    /// anything in a later chunk is, as far as PlayGo is concerned, not downloaded yet. A build
    /// that spreads files across every chunk and then declares only chunk zero initial therefore
    /// tells the console that almost none of the game is available. That combination was produced
    /// for exactly one upstream release, and is the likeliest cause of a title that installs and
    /// then misbehaves.
    ///
    /// Returns null when there is nothing to say.
    /// </summary>
    internal static string? PlayGoInitialChunkProblem(string packagePath, string passcode)
    {
        byte[]? chunkDat, ficm;
        try
        {
            chunkDat = ProsperoPackageArchive.TryReadCntEntry(packagePath, passcode, 4097u);
            ficm = ProsperoPackageArchive.TryReadCntEntry(packagePath, passcode, 8209u);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return null;   // diagnostics only; the library's own checks own the hard failures
        }
        if (chunkDat is null || ficm is null || chunkDat.Length < 256 || ficm.Length < 16) return null;

        // Scenario attributes: section descriptor at 0xE0, 32 bytes per scenario, initial count at
        // +20 and the scenario's own chunk-list length at +22.
        int scenarioCount = BinaryPrimitives.ReadUInt16LittleEndian(chunkDat.AsSpan(14));
        int scenariosOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunkDat.AsSpan(224));
        if (scenarioCount < 1 || scenariosOffset <= 0) return null;
        if (scenariosOffset + scenarioCount * 32 > chunkDat.Length) return null;

        int smallestInitial = int.MaxValue, itsTotal = 0;
        for (int i = 0; i < scenarioCount; i++)
        {
            int record = scenariosOffset + i * 32;
            int initial = BinaryPrimitives.ReadUInt16LittleEndian(chunkDat.AsSpan(record + 20));
            int total = BinaryPrimitives.ReadUInt16LittleEndian(chunkDat.AsSpan(record + 22));
            if (initial < smallestInitial) { smallestInitial = initial; itsTotal = total; }
        }
        if (smallestInitial == int.MaxValue || smallestInitial >= itsTotal) return null;

        // FICM: 16-byte header, then two bytes per file with the chunk id in the first.
        uint mapOffset = BinaryPrimitives.ReadUInt32LittleEndian(ficm.AsSpan(8));
        uint mapSize = BinaryPrimitives.ReadUInt32LittleEndian(ficm.AsSpan(12));
        if (mapOffset != 16 || mapSize == 0 || 16 + (long)mapSize > ficm.Length) return null;

        int outside = 0, highest = 0;
        for (int i = 0; i < (int)mapSize; i += 2)
        {
            int chunk = ficm[16 + i];
            if (chunk > highest) highest = chunk;
            if (chunk >= smallestInitial) outside++;
        }
        if (outside == 0) return null;

        return $"PlayGo: {outside:N0} of {mapSize / 2:N0} file mappings sit in chunks at or above " +
               $"index {smallestInitial} (highest {highest}), but the scenario declares only " +
               $"{smallestInitial} of its {itsTotal} chunk(s) as initial. A package installed whole " +
               "still reports those files as not downloaded. Rebuild it.";
    }

    /// <summary>
    /// The library's own quick verifier, run over a package this CLI just built. Reports every
    /// check by name and returns false if any issue was raised.
    ///
    /// Deliberately the quick pass and not the full one: VerifyPackageFull calls
    /// VerifyPackageQuick first and then decodes every outer block, NAPS chunk and inner file, so
    /// the structural and metadata findings — PlayGo layout included — are identical between the
    /// two. What full adds is payload integrity, which is a property of data the build just wrote
    /// from a source it already read, so re-decoding it here buys much less than it costs on a
    /// large image. `fpkg verify &lt;pkg&gt; --full` is there when that is what you want.
    /// </summary>
    private static bool QuickVerify(string packagePath, string passcode)
    {
        Console.WriteLine();
        var result = ProsperoPackageArchive.VerifyPackageQuick(packagePath, passcode);
        foreach (var check in result.Checks) Console.WriteLine($"  ok: {check}");
        if (result.Issues.Count == 0) return true;
        foreach (var issue in result.Issues) Console.Error.WriteLine($"  ISSUE: {issue}");
        Console.Error.WriteLine(
            $"error: the package was written but failed verification with {result.Issues.Count} " +
            "issue(s). It is on disk so it can be inspected; do not use it.");
        return false;
    }

    /// <summary>
    /// Exports the CNT inputs needed to rebuild a data-bearing AC package, plus a GP5 project
    /// rooted at the exported folder. The inner-PFS payload is deliberately NOT exported — an AC
    /// package keeps its whole sce_sys in the CNT, so the CNT entries are the complete non-payload
    /// input and the data tree is yours to supply.
    /// </summary>
    private static int Template(string[] args)
    {
        if (args.Length < 3) return Fail("template needs a package path and an output directory");
        var pkg = Path.GetFullPath(args[1]);
        var outDir = Path.GetFullPath(args[2]);
        if (!File.Exists(pkg)) return Fail($"no such file: {pkg}");
        var flags = ParseFlags(args.Skip(2).ToArray());
        var passcode = flags.GetValueOrDefault("passcode") ?? new string('0', 32);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

        Console.WriteLine($"Package: {pkg}");
        Console.WriteLine($"Output:  {outDir}\n");
        var files = ProsperoPackageArchive.ExportAdditionalContentTemplate(
            pkg, outDir, passcode, cancellation.Token, null, name => Console.WriteLine($"  {name}"));

        Console.WriteLine($"\n{files.Count} file(s) exported.");
        Console.WriteLine(
            "The inner-PFS payload is not part of a template: copy your own data tree into this\n" +
            "folder, then build it with --gp5 <the .gp5> --retain-sce-sys. The exported\n" +
            "license.info carries the entitlement key and playgo-chunk.dat carries the chunk,\n" +
            "scenario and language counts, which is why the sce_sys sweep has to stay off.");
        return 0;
    }
#endif

    private static int Extract(string[] args)
    {
        if (args.Length < 3) return Fail("extract needs a package path and an output directory");
        var pkg = Path.GetFullPath(args[1]);
        var outDir = Path.GetFullPath(args[2]);
        if (!File.Exists(pkg)) return Fail($"no such file: {pkg}");
        var flags = ParseFlags(args.Skip(2).ToArray());
        var passcode = flags.GetValueOrDefault("passcode") ?? new string('0', 32);
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"Package: {pkg}");
        Console.WriteLine($"Type:    {ProsperoPkgReader.DetectType(pkg)}");

#if LIB_HAS_ARCHIVE_067
        // CNT entries only, mapped back to the sce_sys/ paths a rebuild would read them from,
        // with the container-generated and derived entries dropped. The inner PFS is never
        // opened, so this is fast even on a large package.
        if (flags.ContainsKey("rebuild-source"))
        {
            var rebuild = ProsperoPackageArchive.ExtractRebuildSourceFiles(
                pkg, Path.Combine(outDir, "source"), passcode, default, null,
                name => Console.WriteLine($"  {name}"));
            Console.WriteLine($"\n{rebuild.Count} rebuild-source file(s) — payload not included.");
            return 0;
        }
#else
        if (flags.ContainsKey("rebuild-source"))
            return Fail("--rebuild-source needs LibProsperoPkg 0.6.7 or newer");
#endif

        var inner = LibProsperoPkg.PKG.ProsperoPackageArchive.ExtractInnerFiles(
            pkg, Path.Combine(outDir, "inner"), passcode, !flags.ContainsKey("raw"));
        Console.WriteLine($"\ninner files: {inner.Count}");
        foreach (var f in inner.Take(40)) Console.WriteLine($"  {f}");
        if (inner.Count > 40) Console.WriteLine($"  ... {inner.Count - 40} more");

        if (flags.ContainsKey("cnt"))
        {
            var cnt = LibProsperoPkg.PKG.ProsperoPackageArchive.ExtractCntEntries(
                pkg, Path.Combine(outDir, "cnt"), passcode, true);
            Console.WriteLine($"\nCNT entries: {cnt.Count}");
        }
        if (flags.ContainsKey("si"))
        {
            var si = LibProsperoPkg.PKG.ProsperoPackageArchive.ExtractSiEntries(
                pkg, Path.Combine(outDir, "si"));
            Console.WriteLine($"SI entries: {si.Count}");
        }
        return 0;
    }

    private static int Build(string[] args)
    {
        var flags = ParseFlags(args);
        var source = Path.GetFullPath(Req(flags, "source")).TrimEnd(Path.DirectorySeparatorChar);
        var output = Path.GetFullPath(Req(flags, "out")).TrimEnd(Path.DirectorySeparatorChar);

        // Null/empty means the OS temp dir. The library requires it outside the source tree.
        // Resolved here, ahead of source validation, because a container source (below) needs
        // it to stage into.
        var tempDir = flags.GetValueOrDefault("temp-dir");
        tempDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(tempDir) ? Path.GetTempPath() : tempDir));
        // Checked against the ORIGINAL --source argument (a container's path, or the plain
        // source folder), before a container source below replaces `source` with its staging
        // root. The staging root lives inside tempDir by design, so running this check after
        // the swap would reject every container build.
        if (tempDir.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            (tempDir + Path.DirectorySeparatorChar).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Fail("the temporary folder cannot be inside the source folder");

#if HAS_VIRTUAL_SOURCE
        ContainerSource? containerSource = null;
#endif
        // The ORIGINAL --source argument, kept before a container source replaces `source` with
        // its staging root. Used as the stable recovery key for DrmTypePatch/SceSysQuarantine:
        // the staging root's name carries a fresh random handle every run, so keying on it made
        // RecoverAbandoned structurally unable to ever find a killed container build's leftovers.
        string recoveryKey = source;

        if (File.Exists(source) && !Directory.Exists(source))
        {
#if HAS_VIRTUAL_SOURCE
            // HAS_VIRTUAL_SOURCE only says FpkgVirtualSource.dll existed when this BINARY was
            // compiled. It says nothing about the LibProsperoPkg actually loaded right now, and
            // the two come apart in ordinary use: `./fpkg patch --oodle` after a full run rebuilds
            // LibProsperoPkg.patched.dll WITHOUT the ffpfsc leg while leaving the shim DLL (and
            // therefore the compile-time constant) in place; an upstream LibProsperoPkg.dll
            // update makes the stamp stale so the resolver falls back to stock; the patched DLL
            // can simply be deleted. In every one of those the container path would run with an
            // unpatched Populate, TreeOverlay would never fire, and the build would emit an
            // EMPTY package that still passes verification. So gate on the loaded module.
            if (!VirtualSourcePatchLoaded())
                return Fail($"--source is a file, which needs the virtual-source patch: {source}. " +
                            "Run ./fpkg patch to enable .ffpfsc and .exfat sources.");
            containerSource = ContainerSource.Open(source, tempDir);
            source = containerSource.SourceFolder;
#else
            return Fail($"--source is a file, which needs the virtual-source patch: {source}. " +
                        "Run ./fpkg patch to enable .ffpfsc and .exfat sources.");
#endif
        }
        else
        {
            if (!Directory.Exists(source)) return Fail($"source folder does not exist: {source}");
            if (!Directory.Exists(Path.Combine(source, "sce_sys")))
                return Fail($"source folder has no sce_sys/ subfolder: {source}");
        }
#if HAS_VIRTUAL_SOURCE
        using var _containerSource = containerSource;
#endif

        // A PKG written inside the source tree would end up inside its own image.
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            (output + Path.DirectorySeparatorChar).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Fail("the output folder cannot be inside the source folder");

        var meta = ReadSourceMetadata(source);
        var contentId = (flags.GetValueOrDefault("content-id") ?? meta.ContentId)?.Trim().ToUpperInvariant();
        var version = flags.GetValueOrDefault("version") ?? meta.Version ?? "01.00";
        var title = flags.GetValueOrDefault("title") ?? meta.Title ?? "";
        var passcode = flags.GetValueOrDefault("passcode") ?? new string('0', 32);

        if (contentId is null)
            return Fail("no --content-id given and sce_sys/param.json has no contentId");
        if (!ContentIdPattern.IsMatch(contentId))
            return Fail($"content id must use the UP0000-PPSA00000_00-XXXXXXXXXXXXXXXX form (36 chars): {contentId}");
        if (passcode.Length != 32 || passcode.Any(c => c > '\x7f'))
            return Fail("passcode must be exactly 32 ASCII characters");
        if (!VersionPattern.IsMatch(version))
            return Fail($"version must use the NN.NNN.NNN form (or legacy NN.NN), e.g. 01.008.001: {version}");

        // Left unset, the library's own default applies (64 as of 0.4). Pass --playgo 1 for the
        // verified publisher nwonly profile; the docs still call anything above one experimental.
        int? playGo = flags.TryGetValue("playgo", out var playGoText) ? int.Parse(playGoText!) : null;
        // 1..255 since 0.6.6 (the GUI's spinner maximum went 64 -> 255 in the same release);
        // ProsperoPlayGo.BuildMultiChunkDat enforces the same range.
        if (playGo is < 1 or > 255) return Fail("--playgo must be between 1 and 255");
#if !LIB_HAS_PLAYGO_COUNT
        if (flags.ContainsKey("playgo"))
            return Fail("--playgo needs a library with ProsperoBuildOptions.PlayGoChunkCount; this binary was built against an older release");
#endif

        // applicationDrmType for an Application volume. "standard" matches the 0.6.8 GUI, which
        // moved its combo back to index 1 after one release at Free. Until 0.6.6 the library had
        // no override at all and this CLI rewrote param.json on disk to force "standard"; that
        // whole mechanism is gone.
        ProsperoApplicationDrmType appDrm;
        {
            var text = flags.GetValueOrDefault("app-drm", "standard")!;
            if (!Enum.TryParse(text, ignoreCase: true, out appDrm)
                || !Enum.IsDefined(appDrm)
                || appDrm is not (ProsperoApplicationDrmType.Free or ProsperoApplicationDrmType.Standard))
                return Fail($"--app-drm must be free or standard: {text}");
        }
        // applicationDrmType for a data-bearing additional-content volume. "entitlement" matches
        // the GUI, whose additionalContentDrmSelection field initializer has been 1 since the
        // combo was introduced — this CLI defaulted it to free by mistake in 0.6.7. "free" also
        // omits license.dat / license.info entirely, because one library predicate drives
        // drm_type and the licence emission together.
        ProsperoAdditionalContentDrmType acDrm;
        {
            var text = flags.GetValueOrDefault("ac-drm", "entitlement")!;
            if (!Enum.TryParse(text, ignoreCase: true, out acDrm) || !Enum.IsDefined(acDrm))
                return Fail($"--ac-drm must be free or entitlement: {text}");
        }
#if !LIB_HAS_APP_DRM
        if (flags.ContainsKey("app-drm"))
            return Fail("--app-drm needs LibProsperoPkg 0.6.6 or newer");
#endif
#if !LIB_HAS_AC_DRM
        if (flags.ContainsKey("ac-drm"))
            return Fail("--ac-drm needs LibProsperoPkg 0.6.7 or newer");
#endif

        // PlayGo supported-language mask. Default ulong.MaxValue = every language, matching the
        // library and the GUI. The mask also picks the default language written at offset 0x24
        // of playgo-chunk.dat, so narrowing it changes more than one field.
        ulong playGoLanguages = ulong.MaxValue;
#if LIB_HAS_PLAYGO_LANGUAGES
        if (flags.TryGetValue("playgo-languages", out var langText) && !string.IsNullOrWhiteSpace(langText)
            && !langText.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var supported = LibProsperoPkg.PlayGo.ProsperoPlayGoLanguages.Supported;
            ulong mask = 0;
            foreach (var code in langText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Language is a record struct, so FirstOrDefault yields a zeroed value rather
                // than null; test membership first, exactly as the --sdk-version path does for
                // ProsperoSdkRelease.
                if (!supported.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)))
                    return Fail($"unknown PlayGo language '{code}'; known: {string.Join(", ", supported.Select(l => l.Code))}");
                mask |= supported.First(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)).Mask;
            }
            if (mask == 0) return Fail("--playgo-languages selected no languages");
            playGoLanguages = mask;
        }
#else
        if (flags.ContainsKey("playgo-languages"))
            return Fail("--playgo-languages needs LibProsperoPkg 0.6.6 or newer");
#endif

        // Parsed here, defaulted later: the default depends on which backend actually
        // resolves, which Auto only decides further down.
        int? krakenLevelFlag = flags.TryGetValue("kraken-level", out var krakenLevelText)
                               && !string.IsNullOrWhiteSpace(krakenLevelText)
            ? int.Parse(krakenLevelText)
            : null;
        if (krakenLevelFlag is < -4 or > 9) return Fail("--kraken-level must be between -4 and 9");

        var krakenThreads = int.Parse(flags.GetValueOrDefault("kraken-threads", "0")!);
        if (krakenThreads is < 0 or > 256) return Fail("--kraken-threads must be between 0 and 256");

#if LIB_HAS_IMAGE_MODE
#if LIB_HAS_062_OPTIONS
        // --v3 is the useful pairing; v3 on its own only *enables* the extended features and
        // measured byte-identical to v2 on every corpus tested.
        bool v3Shorthand = flags.ContainsKey("v3");
        var pfsFormatText = flags.GetValueOrDefault("pfs-format", v3Shorthand ? "v3" : "v2")!;
        LibProsperoPkg.PFS.Compression.ProsperoPfsCompressionFormat pfsFormat = pfsFormatText.ToLowerInvariant() switch
        {
            "v2" or "version2" => LibProsperoPkg.PFS.Compression.ProsperoPfsCompressionFormat.Version2,
            "v3" or "version3" => LibProsperoPkg.PFS.Compression.ProsperoPfsCompressionFormat.Version3,
            _ => throw new ArgumentException($"--pfs-format must be v2 or v3: {pfsFormatText}"),
        };
        byte[]? entitlementKey = null;
        if (flags.TryGetValue("entitlement-key", out var ekText) && !string.IsNullOrWhiteSpace(ekText))
        {
            if (ekText.Length != 32 || !ekText.All(Uri.IsHexDigit))
                return Fail($"--entitlement-key must be 32 hex characters (16 bytes): {ekText}");
            entitlementKey = Convert.FromHexString(ekText);
        }
        string? gp5Path = flags.GetValueOrDefault("gp5");
        if (gp5Path is not null)
        {
            gp5Path = Path.GetFullPath(gp5Path);
            if (!File.Exists(gp5Path)) return Fail($"--gp5 project not found: {gp5Path}");
        }
        // --v3 is a plain alias for --pfs-format v3, exactly what the GUI's "PFS v3" does.
        // It deliberately does NOT imply --shuffle-analysis: the GUI leaves that checkbox
        // unticked, and a shuffled image failed to load on a console here.
        bool shuffleAnalysis = flags.ContainsKey("shuffle-analysis");
        if (v3Shorthand && flags.TryGetValue("pfs-format", out var explicitFormat)
            && explicitFormat is not null && !explicitFormat.StartsWith("v3", StringComparison.OrdinalIgnoreCase))
            return Fail($"--v3 conflicts with --pfs-format {explicitFormat}");
        // PFSv2 ignores shuffle analysis; refuse rather than silently dropping it.
        if (shuffleAnalysis && pfsFormat != LibProsperoPkg.PFS.Compression.ProsperoPfsCompressionFormat.Version3)
            return Fail("--shuffle-analysis requires --pfs-format v3");
        if (pfsFormat == LibProsperoPkg.PFS.Compression.ProsperoPfsCompressionFormat.Version3 && !shuffleAnalysis)
            Console.WriteLine(
                "  note:   PFSv3 without --shuffle-analysis measures byte-identical to v2 "
                + "(this matches the GUI default)");
        if (shuffleAnalysis)
            Console.WriteLine(
                "  warning: --shuffle-analysis is the only setting that makes PFSv3 smaller, but a "
                + "shuffled image failed to load on a console here. Treat it as experimental.");
#else
        foreach (var unsupported in new[] { "pfs-format", "entitlement-key", "gp5",
                                            "no-coalescing", "no-relocation-align", "shuffle-analysis" })
            if (flags.ContainsKey(unsupported))
                return Fail($"--{unsupported} needs LibProsperoPkg 0.6.2 or newer");
#endif
#if LIB_HAS_SDK_VERSIONS
        ulong? sdkVersion = null;
        // Default "1", matching the 0.6.7 GUI's SDK combo (index 1 = SDK 1.00). 0.6.6 briefly
        // defaulted to Auto; 0.6.7 reverted. "keep" is this CLI's escape hatch for leaving the
        // source's own sdkVersion and .sceversion trailers alone — the GUI has no equivalent
        // because its Auto entry (index 0) does exactly that.
        var sdkText = flags.GetValueOrDefault("sdk-version", "1");
        if (!string.IsNullOrWhiteSpace(sdkText)
            && !sdkText.Equals("keep", StringComparison.OrdinalIgnoreCase)
            && !sdkText.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (sdkText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(sdkText.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out var raw))
                    return Fail($"--sdk-version is not a 64-bit hex value: {sdkText}");
                sdkVersion = raw;
            }
            else if (int.TryParse(sdkText, out var major))
            {
                // A bare number is a major generation; resolve it to that release.
                // ProsperoSdkRelease is a record struct, so validate against the
                // published list rather than testing the return for null.
                var known = LibProsperoPkg.Content.ProsperoSdkVersions.Releases;
                if (!known.Any(r => r.Major == major))
                    return Fail($"no canonical SDK release for major {major}; known: " +
                                string.Join(", ", known.Select(r => r.Major)));
                var release = LibProsperoPkg.Content.ProsperoSdkVersions.GetByMajor(major);
                sdkVersion = release.ExecutableVersion;
                Console.WriteLine($"  sdk:    major {major} -> {release.Release} " +
                                  $"(exec 0x{release.ExecutableVersion:X16}, pkg 0x{release.PackageVersion:X16})");
            }
            else return Fail($"--sdk-version must be a major generation or 0xHEX: {sdkText}");
        }
#else
        if (flags.ContainsKey("sdk-version"))
            return Fail("--sdk-version needs LibProsperoPkg 0.5 or newer");
#endif
#if LIB_HAS_KRAKEN_BACKEND
        // "Oodle" is this CLI's name for the library's PublishingToolsRequired; the old
        // spelling is still accepted so existing scripts keep working, but it is no longer
        // advertised. "Auto" is a CLI-level mode with no library equivalent: it uses the
        // native Oodle backend when it is actually available and the managed BuiltIn encoder
        // when it is not.
        //
        // Auto is deliberately NOT the library's Automatic. Automatic falls back to storing
        // every kernel-facing block uncompressed (~3.75x larger, silently) when the Oodle
        // backend cannot load, which is the degradation this whole backend exists to avoid.
        string backendName = flags.GetValueOrDefault("kraken-backend", "Auto")!;
        ProsperoKrakenBackend krakenBackend;
        string? backendNote = null;
        if (string.Equals(backendName, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            bool oodleReady = NativeOodleAvailable();
            krakenBackend = oodleReady
                ? ProsperoKrakenBackend.PublishingToolsRequired
                : ProsperoKrakenBackend.BuiltIn;
            backendNote = oodleReady
                ? "kraken: Auto selected the native Oodle backend."
                : "kraken: Auto selected the BuiltIn encoder (" + WhyNoOodle() + ").";
        }
        else if (string.Equals(backendName, "Oodle", StringComparison.OrdinalIgnoreCase))
        {
            krakenBackend = ProsperoKrakenBackend.PublishingToolsRequired;
        }
        else
        {
            krakenBackend = Enum.Parse<ProsperoKrakenBackend>(backendName, true);
        }

        int krakenLevel = krakenLevelFlag ?? DefaultKrakenLevel(krakenBackend);
        if (!flags.ContainsKey("quiet"))
        {
            string levelNote = krakenLevelFlag is null ? $" Level defaults to {krakenLevel}." : string.Empty;
            if (backendNote is not null) Console.Error.WriteLine(backendNote + levelNote);
            else if (levelNote.Length > 0)
                Console.Error.WriteLine($"kraken: level defaults to {krakenLevel} for the {krakenBackend} backend.");
        }
#else
        // No backend to choose from, so no cliff to steer around.
        int krakenLevel = krakenLevelFlag ?? 7;
        if (flags.ContainsKey("kraken-backend"))
            return Fail("--kraken-backend needs LibProsperoPkg 0.4 or newer");
#endif
        var imageMode = Enum.Parse<ProsperoPublisherImageMode>(flags.GetValueOrDefault("image-mode", "PlaintextNoAuth")!, true);
#else
        if (flags.ContainsKey("image-mode"))
            return Fail("--image-mode needs a library with ProsperoPublisherImageMode; this binary was built against an older release");
#endif

        try
        {
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            return Fail($"could not create the output or temporary folder: {ex.Message}");
        }

        var options = new ProsperoBuildOptions
        {
            SourceFolder = source,
            OutputFolder = output,
            ContentId = contentId,
            PrimaryId = contentId,
            TitleId = flags.GetValueOrDefault("title-id") ?? contentId.Substring(7, 9),
            Title = title,
            Version = version,
            Passcode = passcode,
            Mode = Enum.Parse<ProsperoPackageMode>(flags.GetValueOrDefault("mode", "Application")!, true),
            OutputFormat = Enum.Parse<ProsperoOutputFormat>(flags.GetValueOrDefault("format", "DebugImage")!, true),
            UsePublisherPprNaps = true,
#if LIB_HAS_062_OPTIONS
            PfsCompressionFormat = pfsFormat,
            EntitlementKey = entitlementKey,
            SourceMode = gp5Path is null ? ProsperoSourceMode.Folder : ProsperoSourceMode.Gp5Project,
            ProjectFilePath = gp5Path,
            EnableOuterBlockCoalescing = !flags.ContainsKey("no-coalescing"),
            EnableRelocationAlignmentAdjustment = !flags.ContainsKey("no-relocation-align"),
            EnableShufflePatternAnalysis = shuffleAnalysis,
#endif
#if LIB_HAS_SDK_VERSIONS
            SdkVersionOverride = sdkVersion,
#endif
#if LIB_HAS_KRAKEN_BACKEND
            KrakenBackend = krakenBackend,
#endif
#if LIB_HAS_IMAGE_MODE
            PublisherImageMode = imageMode,
#endif
#if LIB_HAS_PLAYGO_COUNT
            PlayGoChunkCount = playGo ?? new ProsperoBuildOptions().PlayGoChunkCount,
#endif
#if LIB_HAS_PLAYGO_LANGUAGES
            PlayGoLanguageMask = playGoLanguages,
#endif
            CompressInnerImage = flags.ContainsKey("compress"),
            DeterministicBuild = !flags.ContainsKey("no-deterministic"),
            GenerateParamJsonIfMissing = !flags.ContainsKey("no-param-json"),
        };

        // Both overrides are volume-scoped in the library: it reads ApplicationDrmTypeOverride
        // only for Application and AdditionalContentDrmTypeOverride only for
        // AdditionalContentData. Setting the irrelevant one is harmless but misleading in the
        // log, so only the applicable one is assigned.
#if LIB_HAS_APP_DRM
        if (options.Mode == ProsperoPackageMode.Application)
            options.ApplicationDrmTypeOverride = appDrm;
#endif
#if LIB_HAS_AC_DRM
        if (options.Mode == ProsperoPackageMode.AdditionalContentData)
            options.AdditionalContentDrmTypeOverride = acDrm;
#endif

#if LIB_HAS_BUILD_TUNING
        options.TemporaryDirectory = tempDir;
        options.KrakenCompressionLevel = krakenLevel;
        options.KrakenMaxDegreeOfParallelism = krakenThreads;

        // Mirrors the 0.2 GUI's Cancel button: stop the build without leaving it mid-write.
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;               // don't let the CLR kill us mid-build
            if (cancellation.IsCancellationRequested) return;
            Console.Error.WriteLine("canceling the build…");
            cancellation.Cancel();
        };
        options.CancellationToken = cancellation.Token;
#else
        foreach (var unsupported in new[] { "temp-dir", "kraken-level", "kraken-threads" })
            if (flags.ContainsKey(unsupported))
                return Fail($"--{unsupported} needs LibProsperoPkg 0.2; this binary was built against a release without it");
#endif

        Console.WriteLine(meta.ContentId is null
            ? "Metadata: sce_sys/param.json not found — a minimal one will be generated."
            : $"Metadata loaded: {meta.ContentId}{(string.IsNullOrWhiteSpace(meta.Title) ? "" : $" — {meta.Title}")}");
        Console.WriteLine(PlayGoStatus(source, options.PlayGoChunkCount, gp5Path));
        // Only meaningful when the count actually comes from the flag; when the source or a GP5
        // supplies it, PlayGoChunkCount is never consulted and checking it rejects good builds.
#if !LIB_HAS_PLAYGO_LANGUAGE_CHUNKS
        // Only meaningful while chunk extents came from SplitMainExtent, which divided the image
        // evenly and therefore produced a zero-length extent for every chunk the image could not
        // fill. BuildLanguageChunkLayout replaced that: chunk zero keeps the bulk of the image,
        // each selected language gets a small extent, and every chunk past the language count is
        // deliberately zero-length. Refusing those would reject ordinary builds.
#if HAS_VIRTUAL_SOURCE
        long? measuredSourceBytes = containerSource?.ContentBytes;
#else
        long? measuredSourceBytes = null;
#endif
        if (PlayGoCountComesFromFlag(source, gp5Path)
            && PlayGoChunkCountProblem(source, options.PlayGoChunkCount, measuredSourceBytes) is string chunkProblem)
            return Fail(chunkProblem);
#endif
#if LIB_HAS_IMAGE_MODE
        Console.WriteLine($"Building {options.Mode} / {options.OutputFormat} / image={imageMode}");
#else
        Console.WriteLine($"Building {options.Mode} / {options.OutputFormat}");
#endif
        Console.WriteLine($"  library: {DescribeLibrary()}");
        Console.WriteLine($"  source: {source}");
        Console.WriteLine($"  output: {output}");
#if LIB_HAS_BUILD_TUNING
        Console.WriteLine($"  temp:   {tempDir}");
        Console.WriteLine($"  kraken: level {krakenLevel} (-4 fast, 9 maximum), " +
                          $"{(krakenThreads == 0 ? Math.Max(1, Environment.ProcessorCount) : krakenThreads)} worker(s)");
#endif
#if LIB_HAS_KRAKEN_BACKEND
        Console.WriteLine($"  kraken backend: {krakenBackend}");
#endif
#if LIB_HAS_APP_DRM
        if (options.Mode == ProsperoPackageMode.Application)
            Console.WriteLine($"  drm:    applicationDrmType {appDrm.ToString().ToLowerInvariant()}" +
                              (appDrm == ProsperoApplicationDrmType.Free
                                  ? " (drm_type 0; no debug licence is emitted)"
                                  : " (drm_type 16; a debug licence is generated when the source has none)"));
#endif
#if LIB_HAS_AC_DRM
        if (options.Mode == ProsperoPackageMode.AdditionalContentData)
            Console.WriteLine($"  drm:    additional-content {acDrm.ToString().ToLowerInvariant()}" +
                              (acDrm == ProsperoAdditionalContentDrmType.Free
                                  ? " (drm_type 0; license.dat and license.info are OMITTED)"
                                  : " (drm_type 16; a debug licence is generated when the source has none)"));
#endif
#if LIB_HAS_PLAYGO_LANGUAGES
        if (playGoLanguages != ulong.MaxValue)
        {
            var picked = LibProsperoPkg.PlayGo.ProsperoPlayGoLanguages.Supported
                .Where(l => (l.Mask & playGoLanguages) != 0).Select(l => l.Code).ToArray();
            Console.WriteLine($"  playgo languages: {picked.Length} ({string.Join(" ", picked)}), " +
                              $"default {LibProsperoPkg.PlayGo.ProsperoPlayGoLanguages.DefaultLanguage(playGoLanguages).Code}");
        }
#endif
#if LIB_HAS_SHA3_DIAGNOSTICS
        Console.WriteLine($"  sha3:   {LibProsperoPkg.Util.ProsperoSha3.BackendName}");
#endif
        Console.WriteLine();
#if LIB_HAS_SHA3_DIAGNOSTICS
        if (LibProsperoPkg.Util.ProsperoSha3.AccelerationAdvice is string sha3Advice)
            Console.WriteLine($"warning: {sha3Advice}\n");
#endif

        var quiet = flags.ContainsKey("quiet");

        // An AC template exported by `fpkg template` (or the 0.6.7 GUI) is the one source tree
        // where the quarantine is exactly wrong: the export deliberately keeps license.dat /
        // license.info, because that is where the entitlement key lives, and playgo-chunk.dat /
        // playgo-scenario.json, because that is where the chunk, scenario and language counts
        // live. Sweeping them would delete the very files that make it a template. Refuse rather
        // than silently changing behaviour based on folder contents — the --playgo trap is what
        // that looks like when it goes wrong.
        if (!flags.ContainsKey("retain-sce-sys") && LooksLikeExportedTemplate(source))
            return Fail(
                "this source looks like an exported additional-content template (a top-level .gp5 " +
                "beside sce_sys/license.info and sce_sys/playgo-chunk.dat). Those files are the " +
                "template's entitlement key and PlayGo counts, and the default sce_sys sweep would " +
                "remove them. Re-run with --retain-sce-sys.");

        // Sweep aside the sce_sys files a retail dump carries that would otherwise override
        // what the builder regenerates for the new image.
        SceSysQuarantine.RecoverAbandoned(tempDir, source, recoveryKey);
        using var sceSysQuarantine = flags.ContainsKey("retain-sce-sys")
            ? null
            : SceSysQuarantine.Apply(tempDir, source, recoveryKey);

        // Runs after the quarantine: the sweep can remove files this would otherwise inspect,
        // and a regenerated icon0.png must not then be swept.
        MediaRepair.RecoverAbandoned(tempDir, source, recoveryKey);
        using var mediaRepair = flags.ContainsKey("no-media-repair")
            ? null
            : MediaRepair.Apply(tempDir, source, recoveryKey);

        var result = ProsperoPackageBuilder.Build(options, quiet ? null : line => Console.WriteLine($"  {line}"));

        Console.WriteLine();
        Console.WriteLine($"Output: {result.OutputPath}");
        foreach (var warning in result.Warnings) Console.WriteLine($"  warning: {warning}");

        if (!flags.ContainsKey("no-verify") && options.OutputFormat == ProsperoOutputFormat.DebugImage)
        {
            Console.WriteLine();
#if LIB_HAS_IMAGE_MODE
            VerifyOutput(result.OutputPath, imageMode == ProsperoPublisherImageMode.PlaintextNoAuth);
#else
            VerifyOutput(result.OutputPath, expectPlaintextMarker: false);
#endif
#if LIB_HAS_ARCHIVE_067
            // VerifyOutput above is a structural FIH inspection — container type, signed byte,
            // outer-PFS mode, seed marker. It says nothing about the CNT entries, and 0.6.8's
            // release note ("please verify your images") exists precisely because upstream's own
            // post-build step is the same shallow check: the GUI calls VerifyPackageQuick only
            // from two buttons on its Extract tab, never after a build. So a wrong PlayGo map is
            // produced, announced as "Verification passed", and only found if someone thinks to
            // go looking. Running it here costs about a second on a 650 MB package and turns
            // that into a build failure at the moment it is caused.
            if (!QuickVerify(result.OutputPath, passcode)) return 1;
#endif
        }

        // Native Oodle diagnostics, when the patched library and its backend are loaded.
        var backendType = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "PprPfsKrakenTool")
            ?.GetType("PprPfsKrakenTool.OodleBackend");
        if (backendType?.GetMethod("Counters")?.Invoke(null, null) is ITuple t)
        {
            Console.Error.WriteLine(
                $"oodle: halves stored raw {t[0]}, encoder failures {t[1]}, blocks rejected {t[2]}");
            if (t[1] is long encoderFailures && encoderFailures > 0)
                Console.Error.WriteLine(
                    $"WARNING: the native Oodle encoder failed on {encoderFailures} half(es); " +
                    "output for those halves was stored UNCOMPRESSED rather than silently " +
                    "degrading. Check the Oodle library in fpkg-tools/native/.");
        }

        return 0;
    }

    /// <summary>
    /// Structural check of a finalized debug image: FIH magic, debug signed byte,
    /// publisher outer-PFS mode, and the plaintext marker when that mode was used.
    /// </summary>
    private static void VerifyOutput(string packagePath, bool expectPlaintextMarker)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("no final PKG at that path", packagePath);

        var detected = ProsperoPkgReader.DetectType(packagePath);
        using var stream = File.OpenRead(packagePath);
        if (stream.Length < 4096) throw new InvalidDataException("the output file is too small to contain an FIH");

        Span<byte> header = stackalloc byte[48];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("\u007fFIH"u8))
            throw new InvalidDataException("the output file does not contain an FIH header");

        var signedByte = header[5];
        if (signedByte != 0)
            throw new InvalidDataException($"expected debug FIH signed byte 0x00, got 0x{signedByte:X2}");

        var superblock = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32, 8)));
        if (superblock < 0 || superblock + 896 > stream.Length)
            throw new InvalidDataException("FIH contains an invalid outer superblock offset");

        stream.Position = superblock + 28;
        Span<byte> modeBytes = stackalloc byte[2];
        stream.ReadExactly(modeBytes);
        var pfsMode = BinaryPrimitives.ReadUInt16LittleEndian(modeBytes);
        if (pfsMode != 0x000D)
            throw new InvalidDataException($"expected publisher outer PFS mode 0x000D, got 0x{pfsMode:X4}");

        stream.Position = superblock + 880;
        var markerBytes = new byte[16];
        stream.ReadExactly(markerBytes);
        string? marker = null;
        if (expectPlaintextMarker)
        {
            marker = Encoding.ASCII.GetString(markerBytes);
            if (marker != "PPRPLAIN-NOAUTH!")
                throw new InvalidDataException("the outer PFS does not contain the PLAINTEXT_NOAUTH marker");
        }

        stream.Position = 0;
        var sha = Convert.ToHexString(SHA256.HashData(stream));

        Console.WriteLine("Verification passed");
        Console.WriteLine($"  container:   {detected?.ToString() ?? "Unknown"}");
        Console.WriteLine($"  size:        {stream.Length:N0} bytes");
        Console.WriteLine($"  signed byte: 0x{signedByte:X2} (debug)");
        Console.WriteLine($"  outer PFS:   0x{pfsMode:X4}");
        if (marker is not null) Console.WriteLine($"  marker:      {marker}");
        Console.WriteLine($"  sha256:      {sha}");
    }


    /// <summary>
    /// Moves the sce_sys files that a retail dump carries but a rebuilt package must not
    /// inherit into a quarantine folder under the temp directory for the duration of the
    /// build, then moves them back.
    ///
    /// Why each one has to go:
    ///   license.dat / license.info  the builder only generates a fresh debug license when the
    ///                               source has neither; otherwise it packs the dump's retail
    ///                               license, which is bound to the original account.
    ///   playgo-*                    playgo-chunk.dat, playgo-hash-table.dat and playgo-ficm.dat
    ///                               describe the *old* image layout. They are regenerated from
    ///                               the new PFS, but a source copy wins and then disagrees with
    ///                               the image. playgo-scenario.json is swept for the same reason
    ///                               (it is preserved verbatim as CNT entry 0x3000).
    ///   origin-param.json /         delta-patch artifacts. They are not CNT entries, so they ride
    ///   target-param.json           into the inner PFS as loose sce_sys files.
    ///
    /// A quarantine abandoned by a killed process is drained on the next run.
    /// </summary>
    private sealed class SceSysQuarantine : IDisposable
    {
        private static readonly string[] Patterns =
            ["license.*", "playgo-*", "origin-param.json", "target-param.json"];

        // Keyed on recoveryKey — the ORIGINAL --source argument — not on the folder whose files
        // are being moved. Identical for a folder source. For a container source the folder is
        // <temp-dir>/fpkg-vsrc-<random handle>, freshly named on every run, so a key derived
        // from it could never match a previous run's and RecoverAbandoned was structurally
        // incapable of draining a killed container build's quarantine. Restoring into a
        // re-staged sce_sys is safe: the staged copy came from the same container, so the
        // recovered files are the same bytes the new staging already wrote.
        private static string QuarantineFor(string backupDirectory, string recoveryKey)
        {
            byte[] key = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(recoveryKey)));
            return Path.Combine(backupDirectory, $"fpkg-scesys-{Convert.ToHexString(key)[..16]}");
        }

        private static bool Matches(string fileName) => Patterns.Any(pattern =>
            FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true));

        private readonly string sceSys;
        private readonly string quarantine;
        private readonly bool active;

        private SceSysQuarantine(string sceSys, string quarantine, bool active)
        {
            this.sceSys = sceSys;
            this.quarantine = quarantine;
            this.active = active;
        }

        /// <summary>Puts back anything an interrupted build left quarantined.</summary>
        internal static void RecoverAbandoned(string backupDirectory, string sourceFolder, string recoveryKey)
        {
            string quarantine = QuarantineFor(backupDirectory, recoveryKey);
            if (!Directory.Exists(quarantine)) return;
            int restored = Restore(quarantine, Path.Combine(sourceFolder, "sce_sys"));
            if (restored > 0)
                Console.WriteLine($"  note:   restored {restored} sce_sys file(s) from an interrupted build");
        }

        internal static SceSysQuarantine Apply(string backupDirectory, string sourceFolder, string recoveryKey)
        {
            string sceSys = Path.Combine(sourceFolder, "sce_sys");
            string quarantine = QuarantineFor(backupDirectory, recoveryKey);
            if (!Directory.Exists(sceSys)) return new SceSysQuarantine(sceSys, quarantine, active: false);

            var doomed = Directory
                .EnumerateFiles(sceSys, "*", SearchOption.AllDirectories)
                .Where(path => Matches(Path.GetFileName(path)))
                .ToList();
            if (doomed.Count == 0) return new SceSysQuarantine(sceSys, quarantine, active: false);

            Directory.CreateDirectory(quarantine);
            var moved = new List<string>();
            try
            {
                foreach (string path in doomed)
                {
                    string relative = Path.GetRelativePath(sceSys, path);
                    string target = Path.Combine(quarantine, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(path, target, overwrite: true);
                    moved.Add(relative);
                }
            }
            catch (Exception)
            {
                Restore(quarantine, sceSys);
                throw;
            }

            moved.Sort(StringComparer.Ordinal);
            Console.WriteLine(
                $"  sce_sys: set aside {moved.Count} stale file(s) for this build: {string.Join(", ", moved)}");
            return new SceSysQuarantine(sceSys, quarantine, active: true);
        }

        private static int Restore(string quarantine, string sceSys)
        {
            if (!Directory.Exists(quarantine)) return 0;
            int count = 0;
            foreach (string path in Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(sceSys, Path.GetRelativePath(quarantine, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(path, target, overwrite: true);
                count++;
            }
            Directory.Delete(quarantine, recursive: true);
            return count;
        }

        public void Dispose()
        {
            if (!active) return;
            try
            {
                Restore(quarantine, sceSys);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"warning: could not restore the sce_sys files set aside for this build: {ex.Message}. " +
                    $"They are still in {quarantine}.");
            }
        }
    }

    /// <summary>
    /// Sets aside structurally corrupt sce_sys PNGs so the build does not pack them, and
    /// regenerates the two the library cannot recover on its own.
    ///
    /// 0.6.6 added "restoration of full-screen PNG images if they are missing from the dump",
    /// but the restore sits in the ELSE of a TryGetValue: it only fires when the PNG is ABSENT,
    /// and only for pic1.png and pic2.png. A present-but-corrupt file is read with
    /// File.ReadAllBytes and packed verbatim, unchecked, in every release through 0.6.7.
    /// Seen in the wild: a dump whose pic2.png is 532 bytes of high-entropy data with no PNG
    /// signature at all, sitting next to a perfectly good pic2.dds.
    ///
    /// So the fix is to make the file absent. That is enough for pic1/pic2, which the library
    /// then rebuilds from their DDS. icon0.png and pic0.png have a DDS sibling in DdsMedia but
    /// are NOT in the library's reverse path, so quarantining them alone would silently drop the
    /// icon; those two are regenerated here instead, with the same DecodeDdsToPng call the
    /// library uses for pic1 (preserveAlpha is true only for pic2.png).
    ///
    /// Anything corrupt with no usable DDS fails the build rather than shipping a broken image.
    ///
    /// The regeneration is done here rather than delegated to the library even for pic1/pic2,
    /// because ProsperoDdsEncoder.DecodeDdsToPng decodes the DDS with BCnEncoder (managed) but
    /// then encodes the PNG with Magick.NET, whose native half ships only as
    /// runtimes/win-x64/native/Magick.Native-Q8-x64.dll. Off Windows its type initializer throws,
    /// so the library's own "restore pic1/pic2 when missing" path turns a missing image into a
    /// failed build instead of a restored one. BcDecoder alone is cross-platform, and PNG
    /// encoding is a zlib stream plus three chunks, so both halves are done in managed code and
    /// the Magick dependency never comes up. That also means a merely MISSING pic1/pic2 is filled
    /// in here, pre-empting the library path entirely.
    /// </summary>
    private sealed class MediaRepair : IDisposable
    {
        // Every sce_sys PNG the library will pack as a CNT entry that also has a DDS sibling.
        // save_data.png and the icon0_NN.png variants have no DDS and so are validated but never
        // regenerated; they are not listed here because a missing one is not recoverable anyway.
        private static readonly string[] Checked = ["icon0.png", "pic0.png", "pic1.png", "pic2.png"];

        // The two the library rebuilds by itself, given the DDS. Quarantining is sufficient.
        private static readonly string[] LibraryRestores = ["pic1.png", "pic2.png"];

        private static string FolderFor(string backupDirectory, string recoveryKey)
        {
            byte[] key = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(recoveryKey)));
            return Path.Combine(backupDirectory, $"fpkg-media-{Convert.ToHexString(key)[..16]}");
        }

        private readonly string sceSys;
        private readonly string folder;
        private readonly List<string> generated;
        private readonly bool active;

        private MediaRepair(string sceSys, string folder, List<string> generated, bool active)
        {
            this.sceSys = sceSys;
            this.folder = folder;
            this.generated = generated;
            this.active = active;
        }

        /// <summary>Puts back anything an interrupted build left set aside.</summary>
        internal static void RecoverAbandoned(string backupDirectory, string sourceFolder, string recoveryKey)
        {
            string folder = FolderFor(backupDirectory, recoveryKey);
            if (!Directory.Exists(folder)) return;
            string sceSys = Path.Combine(sourceFolder, "sce_sys");
            int restored = 0;
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                string target = Path.Combine(sceSys, Path.GetFileName(path));
                try { File.Move(path, target, overwrite: true); restored++; }
                catch (Exception ex) { Console.Error.WriteLine($"warning: could not restore {target}: {ex.Message}"); }
            }
            if (restored > 0)
                Console.WriteLine($"  note:   restored {restored} sce_sys image(s) from an interrupted build");
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }

        internal static MediaRepair Apply(string backupDirectory, string sourceFolder, string recoveryKey)
        {
            string sceSys = Path.Combine(sourceFolder, "sce_sys");
            string folder = FolderFor(backupDirectory, recoveryKey);
            if (!Directory.Exists(sceSys)) return new MediaRepair(sceSys, folder, [], active: false);

            // (name, path, reason, present). A missing pic1/pic2 is "repairable" too: leaving it
            // absent hands the library its Magick-backed restore, which throws off Windows.
            var work = new List<(string Name, string Path, string Reason, bool Present)>();
            foreach (string name in Checked)
            {
                string path = Path.Combine(sceSys, name);
                if (File.Exists(path))
                {
                    if (PngCodec.IsValid(path, out string reason)) continue;
                    work.Add((name, path, reason, true));
                }
                else if (LibraryRestores.Contains(name, StringComparer.OrdinalIgnoreCase)
                         && File.Exists(Path.Combine(sceSys, Path.ChangeExtension(name, ".dds"))))
                {
                    // Only when there is actually a DDS to build from. A source with no pic1 and
                    // no pic1.dds simply has no pic1 — the library skips it and so do we; it is
                    // not a defect to report, let alone to fail on.
                    work.Add((name, path, "missing", false));
                }
            }
            if (work.Count == 0) return new MediaRepair(sceSys, folder, [], active: false);

            // Refuse before touching anything if any of them has no way back.
            var unrecoverable = work
                .Where(c => !File.Exists(Path.Combine(sceSys, Path.ChangeExtension(c.Name, ".dds"))))
                .ToList();
            if (unrecoverable.Count > 0)
                throw new RenamerLikeError(
                    "corrupt sce_sys image(s) with no DDS to rebuild from: " +
                    string.Join("; ", unrecoverable.Select(c => $"{c.Name} ({c.Reason})")) +
                    ". Replace them in the source, or pass --no-media-repair to pack them as they are.");

            Directory.CreateDirectory(folder);
            var generated = new List<string>();
            var moved = new List<string>();
            try
            {
                foreach (var (name, path, reason, present) in work)
                {
                    if (present)
                    {
                        File.Move(path, Path.Combine(folder, name), overwrite: true);
                        moved.Add(name);
                    }
                    string dds = Path.Combine(sceSys, Path.ChangeExtension(name, ".dds"));
                    // preserveAlpha mirrors the library: RGBA for pic2.png, RGB for the rest.
                    byte[] png = DdsToPng(File.ReadAllBytes(dds),
                                          preserveAlpha: name.Equals("pic2.png", StringComparison.OrdinalIgnoreCase));
                    File.WriteAllBytes(path, png);
                    generated.Add(name);
                    Console.WriteLine($"  media:  {name} is {reason}; regenerated {png.Length:N0} bytes from {Path.GetFileName(dds)}");
                }
            }
            catch (Exception)
            {
                foreach (string name in generated) TryDeleteQuietly(Path.Combine(sceSys, name));
                foreach (string name in moved)
                {
                    try { File.Move(Path.Combine(folder, name), Path.Combine(sceSys, name), overwrite: true); }
                    catch (Exception ex) { Console.Error.WriteLine($"warning: could not restore {name}: {ex.Message}"); }
                }
                throw;
            }
            return new MediaRepair(sceSys, folder, generated, active: true);
        }

        /// <summary>
        /// BCnEncoder for the DDS half (managed, cross-platform) and <see cref="PngCodec"/> for
        /// the other, so nothing here touches Magick.NET's win-x64 native library.
        /// </summary>
        private static byte[] DdsToPng(byte[] dds, bool preserveAlpha)
        {
            if (dds.Length < 128 || !dds.AsSpan(0, 4).SequenceEqual("DDS "u8))
                throw new InvalidDataException("Invalid DDS header.");
            using var input = new MemoryStream(dds, writable: false);
            var decoded = new BCnEncoder.Decoder.BcDecoder().Decode2D(input);
            int width = decoded.Width, height = decoded.Height;
            int channels = preserveAlpha ? 4 : 3;

            var pixels = new byte[checked(width * height * channels)];
            int w = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var c = decoded.Span[y, x];
                    pixels[w++] = c.r; pixels[w++] = c.g; pixels[w++] = c.b;
                    if (preserveAlpha) pixels[w++] = c.a;
                }
            return PngCodec.Encode(width, height, pixels, preserveAlpha);
        }

        public void Dispose()
        {
            if (!active) return;
            // Drop our generated stand-ins first, so moving the originals back cannot be blocked
            // by a file we put there.
            foreach (string name in generated) TryDeleteQuietly(Path.Combine(sceSys, name));
            int restored = 0;
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                string target = Path.Combine(sceSys, Path.GetFileName(path));
                try { File.Move(path, target, overwrite: true); restored++; }
                catch (Exception ex) { Console.Error.WriteLine($"warning: could not restore {target}: {ex.Message}"); }
            }
            if (restored > 0) Console.WriteLine($"  media:  restored {restored} original file(s) to the source");
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }

        private static void TryDeleteQuietly(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

    }

#if HAS_VIRTUAL_SOURCE
    /// <summary>
    /// A container used as a build source. Stages sce_sys into the temp directory so every
    /// unpatched metadata path in the library keeps reading real files, registers the container
    /// so patched paths resolve, and hands the builder the staging root as its source folder.
    /// </summary>
    private sealed class ContainerSource : IDisposable
    {
        private readonly FpkgVirtualSource.Container container;
        private readonly string handle;

        private ContainerSource(FpkgVirtualSource.Container container, string handle, string root)
        {
            this.container = container;
            this.handle = handle;
            SourceFolder = root;
        }

        internal string SourceFolder { get; }

        /// <summary>
        /// Total bytes of the files inside the container, for checks that need the size of what
        /// will actually be packed. SourceFolder cannot answer that: it is a staging root holding
        /// only the sce_sys copy the unpatched metadata paths read, so measuring it under-reports
        /// the payload by whatever the container carries.
        /// </summary>
        internal long ContentBytes => container.Files.Sum(f => f.Length);

        internal static ContainerSource Open(string path, string temporaryDirectory)
        {
            var container = FpkgVirtualSource.Container.Open(path);
            // Tracked so the catch below can undo exactly as much as was actually done. Handing
            // the container to the registry transfers ownership of it (Release disposes it), so
            // disposing it directly after registering would leave the registry holding a live
            // handle onto a disposed container — and the half-staged root behind on disk.
            string? handle = null;
            string? root = null;
            try
            {
                string appRoot = container.FindAppRoot() ?? throw new RenamerLikeError(NoAppRootMessage(path, container));

                handle = FpkgVirtualSource.ContainerRegistry.Register(container);
                root = Path.Combine(temporaryDirectory, "fpkg-vsrc-" + handle);
                Directory.CreateDirectory(root);

                string prefix = appRoot.Length == 0 ? "sce_sys/" : appRoot + "/sce_sys/";
                long staged = 0;
                foreach (var entry in container.Files.Where(f =>
                             f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    string target = Path.Combine(root, "sce_sys",
                        entry.RelativePath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var src = container.Reader.OpenFile(entry);
                    using var dst = File.Create(target);
                    src.CopyTo(dst, 1 << 20);
                    staged += entry.Length;
                }

                File.WriteAllText(Path.Combine(root, FpkgVirtualSource.TreeOverlay.MarkerFileName), handle);
                Console.WriteLine($"  source: {Path.GetFileName(path)} " +
                                  $"(app root '{(appRoot.Length == 0 ? "/" : appRoot)}', " +
                                  $"{container.Files.Count:N0} files, {staged:N0} bytes of sce_sys staged)");
                return new ContainerSource(container, handle, root);
            }
            catch
            {
                if (root is not null)
                    try { Directory.Delete(root, recursive: true); } catch (Exception) { /* best effort */ }
                // Release disposes the container and drops the registration; only dispose the
                // container directly when it never reached the registry.
                if (handle is not null) FpkgVirtualSource.ContainerRegistry.Release(handle);
                else container.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The message for a container with no usable app root. Zero candidates and several
        /// candidates are genuinely different problems, and the shared wording used to print
        /// "Candidates: " followed by nothing for the zero case.
        /// </summary>
        private static string NoAppRootMessage(string path, FpkgVirtualSource.Container container)
        {
            var candidates = container.Files
                .Where(f => f.RelativePath.EndsWith("sce_sys/param.json", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.RelativePath)
                .ToList();
            string name = Path.GetFileName(path);
            return candidates.Count == 0
                ? $"{name} contains no sce_sys/param.json, so it has no app root to build from. " +
                  "A source container must hold one folder with a sce_sys/param.json in it " +
                  "(the volume root itself counts)."
                : $"{name} has no single app root (a folder containing sce_sys/param.json): " +
                  $"{candidates.Count} candidates found, and only one is allowed. " +
                  "Candidates: " + string.Join(", ", candidates);
        }

        public void Dispose()
        {
            FpkgVirtualSource.ContainerRegistry.Release(handle);
            // Dispose runs on the failure path too, often while a build exception is unwinding.
            // Anything thrown here would replace that exception with a cleanup error, so every
            // failure to delete the staging root is swallowed, not just IOException — an
            // UnauthorizedAccessException on a read-only staged file used to escape.
            try { Directory.Delete(SourceFolder, recursive: true); } catch (Exception) { }
        }
    }
#endif

#if LIB_HAS_KRAKEN_BACKEND
    /// <summary>
    /// The default Kraken level, which follows the backend that actually resolved rather
    /// than being a fixed number.
    ///
    /// From 0.6.2 the managed BuiltIn encoder crosses a cliff at level 7: it builds a
    /// compacted suffix trie over every block and switches the parser from greedy to a full
    /// DP, then keeps the best of four candidate parses. On a 679 MB dump that is 22.01s
    /// against 5.60s at level 6, for 1.06%. The native Oodle backend replaces
    /// ProsperoReducedKrakenEncoder.EncodeBlock outright, so none of that machinery runs and
    /// level 7 costs it 0.86s - and 7 is its measured optimum, because 8 and 9 come out both
    /// slower and larger. Pre-0.6.2 libraries have no cliff at all (levels 4-9 are
    /// byte-identical), so they keep the historical 7.
    ///
    /// This is the CLI's second deliberate divergence from the GUI, which always uses 7.
    /// See NOTES.md, "Kraken level: the cliff is at 7".
    /// </summary>
    private static int DefaultKrakenLevel(ProsperoKrakenBackend backend) =>
        backend == ProsperoKrakenBackend.PublishingToolsRequired
            ? 7
#if LIB_HAS_062_OPTIONS
            : 6;
#else
            : 7;
#endif
#endif

    private sealed class RenamerLikeError(string message) : Exception(message);

    private record SourceMetadata(string? ContentId, string? Version, string? Title);

    /// <summary>Pulls content id, version and title out of sce_sys/param.json, as the GUI does on folder selection.</summary>
    private static SourceMetadata ReadSourceMetadata(string source)
    {
        var paramJson = Path.Combine(source, "sce_sys", "param.json");
        if (!File.Exists(paramJson)) return new SourceMetadata(null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(paramJson, Encoding.UTF8));
            var root = doc.RootElement;
            var contentId = root.TryGetProperty("contentId", out var cid) ? cid.GetString() : null;
            var version = root.TryGetProperty("contentVersion", out var ver) ? NormalizeContentVersion(ver.GetString()) : null;
            return new SourceMetadata(contentId, version, ReadLocalizedTitle(root));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not read {paramJson}: {ex.Message}");
            return new SourceMetadata(null, null, null);
        }
    }

    private static string? ReadLocalizedTitle(JsonElement root)
    {
        if (!root.TryGetProperty("localizedParameters", out var localized) || localized.ValueKind != JsonValueKind.Object)
            return null;

        var defaultLanguage = localized.TryGetProperty("defaultLanguage", out var lang) ? lang.GetString() : null;
        if (defaultLanguage is not null &&
            localized.TryGetProperty(defaultLanguage, out var entry) &&
            entry.TryGetProperty("titleName", out var name))
            return name.GetString();

        foreach (var property in localized.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("titleName", out var any))
                return any.GetString();

        return null;
    }

    private static string? NormalizeContentVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return null;
        return $"{Math.Clamp(major, 0, 99):00}.{Math.Clamp(minor, 0, 99):00}";
    }

    /// <summary>
    /// Describes which rung of the library's chunk-count precedence a build will actually land
    /// on. 0.6.7 made that three deep, where it used to be two:
    ///
    ///   1. sce_sys/playgo-chunk.dat and/or playgo-scenario.json  (ProsperoPlayGo.ReadSourceCounts)
    ///   2. the GP5 project's &lt;chunk_info chunk_count= scenario_count=&gt;   (new in 0.6.7)
    ///   3. the fallback count, i.e. --playgo or the library default
    ///
    /// Rung 1 is why --playgo looks like it does nothing on a retail dump; the quarantine sweeps
    /// those files aside precisely so the flag reaches rung 3.
    /// </summary>
    /// <summary>
    /// True when the chunk count actually comes from <c>--playgo</c> (or the library default)
    /// rather than from the source or a GP5 — i.e. the build lands on rung 3. Shared by the
    /// status line and the zero-extent guard so the two cannot disagree, which they did: the
    /// guard was rejecting a fallback count the build was never going to use.
    /// </summary>
    private static bool PlayGoCountComesFromFlag(string source, string? gp5Path)
    {
        var sceSys = Path.Combine(source, "sce_sys");
        if (File.Exists(Path.Combine(sceSys, "playgo-chunk.dat"))) return false;
        if (File.Exists(Path.Combine(sceSys, "playgo-scenario.json"))) return false;
        return gp5Path is null || !Gp5DeclaresChunkInfo(gp5Path, out _, out _);
    }

    private static string PlayGoStatus(string source, int chunks, string? gp5Path)
    {
        var present = PlayGoFiles.Where(n => File.Exists(Path.Combine(source, "sce_sys", n))).ToArray();
        if (!PlayGoCountComesFromFlag(source, gp5Path))
        {
            if (present.Length == PlayGoFiles.Length)
                return "PlayGo: all three prepared files found — the builder will preserve their own layout.";
            if (File.Exists(Path.Combine(source, "sce_sys", "playgo-chunk.dat")))
                return "PlayGo: the source's playgo-chunk.dat supplies the chunk and scenario counts; --playgo is ignored.";
            if (File.Exists(Path.Combine(source, "sce_sys", "playgo-scenario.json")))
                return "PlayGo: the source's playgo-scenario.json supplies the counts; --playgo is ignored.";
            Gp5DeclaresChunkInfo(gp5Path!, out var gc, out var gs);
            return $"PlayGo: counts taken from the GP5 project — {gc} chunk(s) / {gs} scenario(s); --playgo is ignored.";
        }
        if (present.Length == 0)
            return $"PlayGo: automatic layout — 1 scenario / {chunks} chunk(s); no separate files required.";
        var missing = string.Join(", ", PlayGoFiles.Except(present, StringComparer.OrdinalIgnoreCase));
        return $"PlayGo: incomplete set found; these will be generated: {missing}";
    }

    /// <summary>
    /// Reads a GP5's chunk_info without pulling in the library's XML deserialiser, so the status
    /// line works even on a release that predates the GP5 fallback.
    /// </summary>
    /// <summary>
    /// True for the exact shape ExportAdditionalContentTemplate produces: a .gp5 at the top level
    /// next to a sce_sys/ holding both a licence (the entitlement-key source) and playgo-chunk.dat
    /// (the count and language-mask source). All three have to be present, so an ordinary dump
    /// that merely happens to carry a GP5 is not mistaken for one.
    /// </summary>
    private static bool LooksLikeExportedTemplate(string source)
    {
        if (!Directory.EnumerateFiles(source, "*.gp5", SearchOption.TopDirectoryOnly).Any()) return false;
        var sceSys = Path.Combine(source, "sce_sys");
        return File.Exists(Path.Combine(sceSys, "license.info"))
            && File.Exists(Path.Combine(sceSys, "playgo-chunk.dat"));
    }

    private static bool Gp5DeclaresChunkInfo(string gp5Path, out int chunks, out int scenarios)
    {
        chunks = scenarios = 0;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(gp5Path);
            var info = doc.Descendants("chunk_info").FirstOrDefault();
            if (info is null) return false;
            return int.TryParse(info.Attribute("chunk_count")?.Value, out chunks)
                 & int.TryParse(info.Attribute("scenario_count")?.Value, out scenarios);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return false;
        }
    }

#if !LIB_HAS_PLAYGO_LANGUAGE_CHUNKS
    /// <summary>
    /// 0.6.4 refused a chunk count the image cannot fill:
    ///
    ///   if (blocks &lt; chunkCount) throw "The PlayGo main extent has N blocks and cannot be
    ///                                      split into M non-empty chunks."
    ///
    /// 0.6.6 deleted that guard and 0.6.7 did not restore it, so SplitMainExtent now silently
    /// hands every chunk past the block count a zero-length main extent. Measured on 0.6.7: a
    /// 3 MB source (61 blocks of 64 KiB) with the current default of 100 chunks builds clean and
    /// produces 39 empty chunks. The default moved 64 -> 100 in 0.6.6, so this is easier to hit
    /// than it was. Re-checked here because the CLI knows the source size before the build.
    /// </summary>
    private static string? PlayGoChunkCountProblem(string source, int chunks, long? measuredBytes)
    {
        if (chunks <= 1) return null;
        long bytes;
        if (measuredBytes is long known) bytes = known;
        else
        {
            try
            {
                bytes = new DirectoryInfo(source)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;   // can't measure it; let the build proceed rather than guess
            }
        }
        var blocks = bytes / 65536;
        if (blocks >= chunks) return null;
        return $"--playgo {chunks} exceeds what this source can fill: {bytes:N0} bytes is {blocks} " +
               $"block(s) of 64 KiB, so {chunks - blocks} chunk(s) would get a zero-length main " +
               $"extent, which the library does not check and would build anyway. Use --playgo " +
               $"{Math.Max(1, blocks)} or fewer.";
    }
#endif

    private static int Api(string[] args)
    {
        var filter = args.Length > 1 ? args[1] : null;
        var types = typeof(ProsperoPackageBuilder).Assembly
            .GetExportedTypes()
            .Where(t => filter is null || t.FullName!.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName);

        foreach (var t in types)
        {
            Console.WriteLine(t.FullName);
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is not MethodInfo { IsSpecialName: true })
                .OrderBy(m => m.Name);
            foreach (var m in members) Console.WriteLine($"    {Describe(m)}");
        }
        return 0;
    }

    private static string Describe(MemberInfo m) => m switch
    {
        MethodInfo mi => $"{Short(mi.ReturnType)} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"))})",
        PropertyInfo pi => $"{Short(pi.PropertyType)} {pi.Name} {{ {(pi.CanRead ? "get; " : "")}{(pi.CanWrite ? "set; " : "")}}}",
        FieldInfo fi => $"{Short(fi.FieldType)} {fi.Name}",
        ConstructorInfo ci => $"ctor({string.Join(", ", ci.GetParameters().Select(p => Short(p.ParameterType)))})",
        _ => m.Name,
    };

    private static string Short(Type t) =>
        t.IsGenericType
            ? $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(Short))}>"
            : t.Name;

    /// <summary>Prints a result object's public state without hard-coding member names.</summary>
    private static void Dump(string label, object? value, int depth)
    {
        var pad = new string(' ', depth * 2);
        if (value is null) { Console.WriteLine($"{pad}{label}: <null>"); return; }

        var t = value.GetType();
        if (depth > 3 || t.IsPrimitive || value is string or decimal or DateTime or DateTimeOffset or Enum or Guid)
        {
            Console.WriteLine($"{pad}{label}: {value}");
            return;
        }

        if (value is byte[] bytes)
        {
            var head = Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length)));
            Console.WriteLine($"{pad}{label}: byte[{bytes.Length}] {head}{(bytes.Length > 32 ? "…" : "")}");
            return;
        }

        if (value is System.Collections.IEnumerable seq)
        {
            var items = seq.Cast<object?>().ToList();
            Console.WriteLine($"{pad}{label}: [{items.Count}]");
            foreach (var (item, i) in items.Select((x, i) => (x, i)).Take(64))
                Dump($"[{i}]", item, depth + 1);
            if (items.Count > 64) Console.WriteLine($"{pad}  … {items.Count - 64} more");
            return;
        }

        Console.WriteLine($"{pad}{label}: {t.Name}");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            object? v;
            try { v = p.GetValue(value); } catch (Exception ex) { v = $"<{ex.GetBaseException().Message}>"; }
            Dump(p.Name, v, depth + 1);
        }
    }

    internal static Dictionary<string, string?> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected argument '{args[i]}'");
            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            flags[key] = hasValue ? args[++i] : null;
        }
        return flags;
    }

    private static string Req(Dictionary<string, string?> flags, string key) =>
        flags.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ArgumentException($"--{key} is required");

    internal static int Fail(string message, Exception? ex = null)
    {
        Console.Error.WriteLine($"error: {message}");
        if (ex is not null && Environment.GetEnvironmentVariable("FPKG_TRACE") == "1")
            Console.Error.WriteLine(ex);
        return 1;
    }
}
