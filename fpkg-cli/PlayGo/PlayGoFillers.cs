using System.Text;
using System.Text.Json;
using LibProsperoPkg.PlayGo;

namespace Fpkg.Cli.PlayGo;

/// <summary>
/// The 31 <c>playgo-languages/NN-xx-XX.bin</c> filler files, and the staging tree they are built
/// in. Naming and ordering follow Sony's own generator
/// (<c>create-gp5-from-folder.py</c>: <c>playgo-languages/{index:02d}-{language}.bin</c>, index
/// 1-based over a list whose FIRST element is the scenario's default language), so chunk N owns
/// <c>NN-&lt;language of chunk N&gt;.bin</c> and chunk 1 is the default language.
/// </summary>
internal static class PlayGoFillers
{
    internal const string Directory = "playgo-languages";

    /// <summary>
    /// Sony's own size: 1 MiB, exactly what <c>create-gp5-from-folder.py</c> writes, and what the
    /// <c>.gp5-assets/*/playgo-languages/</c> files retained from the Windows build measure. This
    /// is the DEFAULT again now that site 10 gives each filler its own stored copy rather than
    /// folding all 31 onto one.
    ///
    /// <para>
    /// What a megabyte of zeros COSTS depends on the Kraken backend, MEASURED on the reference fixture:
    /// BuiltIn encodes each 256 KiB zero block to a 16-byte stub, so a filler's whole footprint is
    /// 64 bytes and the 31 of them cost 1,984 bytes. The native RAD Oodle backend
    /// (PublishingToolsRequired) cannot: RAD emits a form the library's own managed KrakenDecoder
    /// does not decode, PprPfsKrakenTool's verify-or-store shim therefore stores every zero half
    /// raw (252 halves against a baseline of 4), and the 31 fillers cost the full 32,505,856 bytes.
    /// That gap is the Oodle shim's unimplemented bare-entropy case, not this feature.
    /// </para>
    /// </summary>
    internal const int ZeroPayloadSize = 1024 * 1024;

    /// <summary>
    /// The incompressible variant's size, kept behind <c>--playgo-filler-distinct</c>. 64 KiB is
    /// one PFS block: MEASURED, a 1 MiB incompressible filler gives aligned 1,048,576-byte extents
    /// and costs 32.6 MB on the reference fixture (661,004,926 -> 693,613,714), where a 64 KiB one gives
    /// extents of exactly 65,536 for 2.1 MB. This whole variant exists only as a fallback — see
    /// <see cref="Payload"/>.
    /// </summary>
    internal const int DistinctPayloadSize = 64 * 1024;

    /// <summary>The size a filler of the requested kind gets.</summary>
    internal static int PayloadSize(bool distinct) => distinct ? DistinctPayloadSize : ZeroPayloadSize;

    /// <summary>The languages in chunk order: index 0 is chunk 1.</summary>
    internal static IReadOnlyList<ProsperoPlayGoLanguages.Language> Order(string defaultLanguageCode)
    {
        var all = ProsperoPlayGoLanguages.Supported;
        // Language is a record STRUCT, so FirstOrDefault would hand back a zeroed value rather
        // than null when nothing matches — membership is tested first, the way the --sdk-version
        // and --playgo-languages paths in Program.cs already do for the same reason.
        bool Matches(ProsperoPlayGoLanguages.Language l) =>
            string.Equals(l.Code, defaultLanguageCode, StringComparison.OrdinalIgnoreCase);
        if (!all.Any(Matches))
            throw new InvalidDataException(
                $"playgo-scenario.json names '{defaultLanguageCode}' as the default language, which " +
                $"is not one of the {all.Count} languages the library knows.");
        var def = all.First(Matches);
        return [def, .. all.Where(l => l.Id != def.Id)];
    }

    internal static string FileName(int chunk, ProsperoPlayGoLanguages.Language language) =>
        $"{chunk:00}-{language.Code}.bin";

    /// <summary>
    /// The filler's bytes. By DEFAULT 1 MiB of zeros, byte-identical across all 31 files — Sony's
    /// literal form, restored in 0.6.9-fix1 once the IL dedup bypass made it usable.
    ///
    /// <para>
    /// Why it was not usable before: Sony's encoder handles zeros per FILE — the spans of one
    /// filler collapse onto one 16-byte stub, but each file gets its own (oracle: 31 footprints,
    /// 65,536 apart). LibProsperoPkg de-duplicates by CONTENT across the whole image, so all 124
    /// spans of 31 byte-identical zero fillers pointed at ONE stored copy at 189,438,943, a
    /// location that also carries chunk-0 payload; identical fillers owned no package bytes of
    /// their own and no extent could be derived. <c>ProsperoNapsBuildOptions</c> exposes no switch
    /// for it, so the bypass had to be an IL patch — site 10, which grants
    /// <c>PlayGoFillerDedup</c>'s exact allow-list its own stored copy per block and leaves every
    /// other file de-duplicating normally.
    /// </para>
    /// <para>
    /// <paramref name="distinct"/> selects the fallback that shipped while the bypass did not
    /// exist: a deterministic INCOMPRESSIBLE per-language stream of <see cref="DistinctPayloadSize"/>
    /// bytes, which forces distinct storage through content rather than through the patch. It is a
    /// DEVIATION from Sony's zeros — decoded-content parity for these 31 files is lost — recorded
    /// in docs/drift-register.md as drift #24/#25, and it is kept only so the old behaviour can be
    /// recovered with <c>--playgo-filler-distinct</c> if the patch ever has to be dropped.
    /// </para>
    /// </summary>
    internal static byte[] Payload(int chunk, ProsperoPlayGoLanguages.Language language, bool distinct)
    {
        var bytes = new byte[PayloadSize(distinct)];
        if (!distinct) return bytes;

        // SplitMix64, written out rather than taken from Random: System.Random's sequence is not
        // contractually stable across runtime versions and this has to be reproducible for the
        // determinism check in §7.5. Seeded from the language id alone, so the payload is a pure
        // function of the chunk it belongs to.
        ulong state = 0x9E3779B97F4A7C15UL * (ulong)(language.Id + 1) + 0xBF58476D1CE4E5B9UL;
        for (int at = 0; at < bytes.Length; at += 8)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            BitConverter.TryWriteBytes(bytes.AsSpan(at, 8), z ^ (z >> 31));
        }
        return bytes;
    }

    /// <summary>
    /// The default language, read from the source's own <c>sce_sys/playgo-scenario.json</c>.
    /// Absent, en-US: that is what the library's generated scenario JSON uses, and it is the value
    /// the the reference fixture and the large title sources both carry.
    /// </summary>
    internal static string DefaultLanguageCode(string sourceFolder)
    {
        string path = ScenarioJsonPath(sourceFolder);
        if (!File.Exists(path)) return "en-US";
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.TryGetProperty("scenarioDefaultLanguage", out var v) && v.GetString() is string code
            ? code
            : "en-US";
    }

    internal static string ScenarioJsonPath(string sourceFolder) =>
        Path.Combine(sourceFolder, "sce_sys", "playgo-scenario.json");

    /// <summary>
    /// Builds a staging root that presents <paramref name="source"/> plus the fillers, without
    /// writing a single byte into <paramref name="source"/>.
    ///
    /// <para>
    /// Directories are recreated as real directories and files are HARD-LINKED, so the tree costs
    /// no data blocks at all — the 84 GB packages this has to work on cannot be copied. A hard link
    /// only works within one filesystem, so anything that cannot be linked is copied instead; for a
    /// large source that means passing <c>--temp-dir</c> on the source's own volume.
    /// <para>
    /// Symlinks were tried first and are WRONG here, measured: the library sizes each node with
    /// <c>FSFile(path, length)</c> from a length that comes back as the LINK's own size, so a build
    /// from a symlinked tree died with "File '/Media/RuntimeInitializeOnLoads.json' ended after 13
    /// of 147 bytes" — 147 being the length of the target path. A hard link is the same inode, so
    /// no such disagreement is possible.
    /// </para>
    /// <para>
    /// Files under <c>sce_sys/</c> are copied rather than linked, because the sce_sys quarantine
    /// and the media repair both move and replace files there, and a hard link would let a
    /// rewrite-in-place reach the user's own file.
    /// </para>
    /// </summary>
    /// <summary>
    /// Writes the 31 fillers into <c>&lt;source&gt;/playgo-languages/</c> and returns a handle that
    /// deletes them again.
    /// <para>
    /// Earlier this mirrored the whole source tree with hard links and added the fillers to the
    /// copy. That is the wrong shape: hard links cannot cross volumes, so a --temp-dir on another
    /// filesystem silently degraded to copying the source -- 149 GB for the large title -- to add 31
    /// files. The source folder is already the thing the builder reads, and this CLI already
    /// writes into it and restores afterwards for the sce_sys quarantine and the media repair.
    /// This is the same contract: touch it, then put it back.
    /// </para>
    /// A marker beside the backup directory records the path, so a run killed mid-build has its
    /// fillers removed on the next one rather than leaving them to be packed silently.
    /// </summary>
    internal sealed class Staged : IDisposable
    {
        private readonly string _languages;
        private readonly string _marker;
        internal Staged(string languages, string marker) { _languages = languages; _marker = marker; }

        public void Dispose()
        {
            try { if (System.IO.Directory.Exists(_languages)) System.IO.Directory.Delete(_languages, recursive: true); }
            catch (IOException) { }
            try { if (File.Exists(_marker)) File.Delete(_marker); } catch (IOException) { }
        }
    }

    /// <summary>Removes a filler directory left behind by a killed run.</summary>
    internal static void RecoverAbandoned(string backupDirectory)
    {
        if (!System.IO.Directory.Exists(backupDirectory)) return;
        foreach (string marker in System.IO.Directory.EnumerateFiles(backupDirectory, "fpkg-fillers-*.path"))
        {
            try
            {
                string languages = File.ReadAllText(marker).Trim();
                if (languages.Length > 0 && System.IO.Directory.Exists(languages)
                    && Path.GetFileName(languages) == Directory)
                    System.IO.Directory.Delete(languages, recursive: true);
                File.Delete(marker);
            }
            catch (IOException) { }
        }
    }

    internal static Staged Stage(string source, string backupDirectory, bool distinct,
                                 Action<string>? log = null, bool force = false)
    {
        // A source that already has the directory is refused, not merged into: we would be
        // describing files we did not place, and deleting them afterwards would destroy the
        // user's own data.
        string languages = Path.Combine(source, Directory);
        if (force && System.IO.Directory.Exists(languages))
        {
            // Destructive and therefore explicit: only ever reached via --playgo-fillers-force,
            // and it says what it removed. Recovers a folder left by a run killed so hard that
            // even the marker did not survive.
            int had = System.IO.Directory.GetFiles(languages).Length;
            System.IO.Directory.Delete(languages, recursive: true);
            log?.Invoke($"removed an existing {Directory}/ ({had} file(s)) before regenerating it " +
                        "(--playgo-fillers-force)");
        }
        if (System.IO.Directory.Exists(languages))
            throw new InvalidDataException(
                $"the source already has a '{Directory}/' directory. This command generates that " +
                "directory's contents and would be describing files it did not place, so it refuses " +
                "rather than mixing the two. Pass --playgo-fillers-force to delete and " +
                "regenerate it, which is what a run killed mid-build leaves behind.");

        System.IO.Directory.CreateDirectory(backupDirectory);
        string marker = Path.Combine(backupDirectory,
            $"fpkg-fillers-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(source))))[..16]}.path");
        File.WriteAllText(marker, languages);

        System.IO.Directory.CreateDirectory(languages);
        var handle = new Staged(languages, marker);
        try
        {
            var order = Order(DefaultLanguageCode(source));

            // Sony's fillers are byte-identical, so without the dedup bypass all 31 would share one
            // stored copy and no language chunk would own package bytes to derive an extent from.
            // The allow-list is named here, from the paths actually written, and it is the ONLY
            // thing site 10 will bypass.
            FpkgVirtualSource.PlayGoFillerDedup.Reset();
            for (int chunk = 1; chunk <= order.Count; chunk++)
            {
                var language = order[chunk - 1];
                string name = FileName(chunk, language);
                File.WriteAllBytes(Path.Combine(languages, name), Payload(chunk, language, distinct));
                if (!distinct) FpkgVirtualSource.PlayGoFillerDedup.Register($"{Directory}/{name}");
            }
            log?.Invoke($"staged {order.Count} language filler(s) in {languages} " +
                        $"(chunk 1 = {order[0].Code}, " +
                        $"{(distinct ? $"per-language pattern, {DistinctPayloadSize:N0} bytes"
                                     : $"zeros, {ZeroPayloadSize:N0} bytes, dedup bypass on " +
                                       $"{FpkgVirtualSource.PlayGoFillerDedup.Count} path(s)")})");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }


    /// <summary>
    /// POSIX <c>link(2)</c>. .NET has no hard-link API — <c>File.CreateSymbolicLink</c> is the only
    /// link it offers, and a symlink is precisely what does not work here — so this is the one
    /// interop call in the feature. A non-zero return (a different filesystem, or a source that is
    /// not linkable) is not an error: the caller copies instead.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link",
        CharSet = System.Runtime.InteropServices.CharSet.Ansi, SetLastError = true)]
    private static extern int Link(string existing, string created);
}
