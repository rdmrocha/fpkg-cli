using System.Security.Cryptography;
using System.Text.Json;
using LibProsperoPkg.PKG;
using LibProsperoPkg.PlayGo;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// <c>fpkg repair-playgo &lt;pkg&gt; [--passcode &lt;32&gt;] [--out &lt;path&gt;|--in-place] [--dry-run]</c>
/// — rewrites a 0.6.8-built package's PlayGo metadata (entries 4097, 8209 and 12288, plus the SI
/// segment's <c>playgo-chunk.dat</c> and CRC table) to the 0.6.9 shape without touching the
/// payload.
///
/// <para>
/// <b>Dry run is the default.</b> Nothing is written unless <c>--out</c> or <c>--in-place</c> is
/// given, and even then every refusal point has already run: the six NUMBERED guards below plus
/// two structural preconditions — a package with no SI segment, and <c>CntRepair</c>'s
/// requirement that the CNT region carry no padding past the end of its body. Eight in all, and
/// the whole repair is computed in memory first.
/// </para>
///
/// <para>
/// <b>That does NOT mean a refusal can never leave a half-written package behind.</b> It is true
/// of <c>--out</c>, which writes to a staging file and renames. It is NOT true of
/// <c>--in-place</c>: step 3 of <see cref="InPlaceWriter.Write"/> rebuilds the SI only after the
/// repaired CNT has been spliced and fsynced, so anything that fails from there on — an I/O error,
/// a full disk, the SI-growth guard, or a <c>SiRepair</c> refusal — lands on a package that is
/// already mid-repair. That is what the journal is for, and those refusals say so explicitly.
/// Guard 2 pre-screens every SI condition it can reach cheaply, precisely to keep the number of
/// refusals that can land there as small as possible.
/// </para>
///
/// <para>
/// The guards exist because the failure modes here are mostly SILENT. A wrong passcode, a
/// non-Application volume or a package carrying localised scenario names all produce output that
/// passes every digest check this CLI (or the library) can make, and fails only on a console.
/// Each guard converts one of those into a loud refusal.
/// </para>
/// </summary>
internal static class RepairPlayGoCommand
{
    private const uint ChunkDatId  = 4097;   // playgo-chunk.dat
    private const uint FicmId      = 8209;   // playgo-ficm.dat
    private const uint ScenarioId  = 12288;  // playgo-scenario.json
    private const uint EntryKeysId = 16;
    private const uint DigestsId   = 1;
    private const uint GeneralDigestsId = 128;

    /// <summary>The playgo-scenario.json member that carries the per-language presentation.</summary>
    private const string ScenariosMember = "scenarios";

    /// <summary>
    /// The publisher ENTRY_KEYS profile this repair was derived against: 0xB80 bytes, which is
    /// also what <c>CntReseal</c> keys its 64-KiB body rounding off.
    /// </summary>
    private const uint PublisherEntryKeysSize = 0xB80;   // 2944

    /// <summary>Compared OrdinalIgnoreCase, matching how <c>Program.ParseFlags</c> keys them.</summary>
    private static readonly HashSet<string> KnownFlags =
        new(["passcode", "out", "in-place", "dry-run", "work-dir", "verbose",
             "keep-backup", "restore"],
            StringComparer.OrdinalIgnoreCase);

    internal static int Run(string[] args)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Program.Fail("repair-playgo needs a package path");

        Dictionary<string, string?> flags;
        try { flags = Program.ParseFlags(args[1..]); }
        catch (ArgumentException ex) { return Program.Fail(ex.Message); }

        string path = Path.GetFullPath(args[1]);
        string passcode = flags.GetValueOrDefault("passcode") ?? new string('0', 32);
        string? outPath = flags.GetValueOrDefault("out");
        bool inPlace = flags.ContainsKey("in-place");
        bool dryRunRequested = flags.ContainsKey("dry-run");
        string? workDir = flags.GetValueOrDefault("work-dir");
        bool verbose = flags.ContainsKey("verbose");
        bool keepBackup = flags.ContainsKey("keep-backup");
        bool restore = flags.ContainsKey("restore");
        string? backupPath = flags.GetValueOrDefault("restore");

        // ParseFlags keys OrdinalIgnoreCase, so this must too: an ordinal comparison here would
        // reject --IN-PLACE as unknown after ParseFlags had happily accepted it.
        foreach (var key in flags.Keys)
            if (!KnownFlags.Contains(key))
                return Program.Fail($"unknown option --{key} (try: fpkg help)");

        if (passcode.Length != 32 || passcode.Any(c => c > '\x7f'))
            return Program.Fail("--passcode must be exactly 32 ASCII characters");
        if (inPlace && outPath is not null)
            return Program.Fail("--out and --in-place are mutually exclusive; pick one");
        if (flags.ContainsKey("out") && string.IsNullOrWhiteSpace(outPath))
            return Program.Fail("--out needs a path");
        if (dryRunRequested && (inPlace || outPath is not null))
            return Program.Fail("--dry-run cannot be combined with --out or --in-place");
        if (flags.ContainsKey("work-dir") && string.IsNullOrWhiteSpace(workDir))
            return Program.Fail("--work-dir needs a path");
        // A backup is the journal an in-place run already writes, kept instead of deleted. There
        // is no journal on an --out or dry run, so accepting the flag there would promise an undo
        // that was never written.
        if (keepBackup && !inPlace)
            return Program.Fail("--keep-backup only applies to --in-place; an --out run leaves the original untouched");
        if (restore && (inPlace || outPath is not null || dryRunRequested || keepBackup))
            return Program.Fail("--restore cannot be combined with --out, --in-place, --dry-run or --keep-backup");
        if (workDir is not null)
        {
            workDir = Path.GetFullPath(workDir);
            // A path that already exists as a FILE is a typo, not a directory to create:
            // CreateDirectory would throw an IOException naming neither the flag nor the intent.
            if (File.Exists(workDir))
                return Program.Fail(
                    $"--work-dir '{workDir}' is an existing file, not a directory.");
        }
        if (!File.Exists(path))
            return Program.Fail($"no such file: {path}");

        // Restore is its own command, not a mode of the repair: it plans nothing, reads no CNT and
        // writes back the two regions the backup captured. It runs before RecoverIfNeeded on
        // purpose — a package with a retained backup is a COMPLETED repair, not an interrupted one,
        // and recovery has no business forming an opinion about it.
        if (restore)
        {
            string from = backupPath is null
                ? RepairJournal.BackupPathFor(path)
                : Path.GetFullPath(backupPath);
            if (!File.Exists(from))
                return Program.Fail(
                    $"no repair backup at '{from}'. A backup is written only by " +
                    "`--in-place --keep-backup`; pass --restore <path> if it is somewhere else.");
            var restoreProgress = new Progress(Console.Out, verbose, false, 1);
            try { RepairJournal.Restore(from, path, restoreProgress); }
            catch (InvalidDataException ex) { return Program.Fail(ex.Message); }
            restoreProgress.Finish();
            Console.WriteLine();
            Console.WriteLine($"restored: {path}");
            Console.WriteLine($"backup:   {from} (kept — delete it when you no longer need the undo)");
            return 0;
        }
        // Everything else in this command refuses rather than degrades; silently replacing an
        // existing output would be the one exception.
        if (outPath is not null && File.Exists(outPath))
            return Program.Fail(
                $"--out would overwrite an existing file: {Path.GetFullPath(outPath)}. " +
                "Remove it or choose another path.");

        bool write = inPlace || outPath is not null;

        // CREATED LAST, after every argument and package check above has passed. Creating it with
        // the other flag validation was tidier to read and left an empty directory behind on every
        // run that then refused — a flag that only relocates the journal or staging file has no
        // business leaving a permanent trace when the run never starts.
        if (workDir is not null)
        {
            try { Directory.CreateDirectory(workDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
            {
                return Program.Fail($"--work-dir '{workDir}' could not be created: {ex.Message}");
            }
        }

        if (workDir is not null && outPath is not null)
            WarnIfWorkDirIsOnAnotherDevice(workDir, Path.GetFullPath(outPath));

        try
        {
            return Repair(path, passcode, write ? (inPlace ? path : outPath!) : null, inPlace,
                          workDir, verbose, keepBackup);
        }
        // KeyNotFoundException and UnauthorizedAccessException are BACKSTOPS, not the design: the
        // lookups that could raise the first now route through CntEntryTable.Get and
        // TryGetValue, which refuse with a message naming what is missing. They are listed so that
        // a lookup added later without that care still produces an `error:` line rather than a
        // raw .NET stack trace.
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
                                      or InvalidOperationException or NotSupportedException
                                      or KeyNotFoundException or UnauthorizedAccessException)
        {
            return Program.Fail(ex.Message, ex);
        }
    }

    /// <summary>
    /// Warns — never refuses — when <c>--work-dir</c> and <c>--out</c> look like they land on
    /// different filesystems. The staging file exists so that the target appears atomically, by
    /// <see cref="File.Move(string,string,bool)"/> being a rename within one filesystem. Across
    /// devices that call degrades into a copy-then-delete, so a crash can leave a PARTIAL file at
    /// the target — exactly the window staging was there to close.
    ///
    /// <para>
    /// BEST EFFORT, and it says so. The check walks <see cref="DriveInfo.GetDrives"/> for the
    /// longest mount point that prefixes each path, which is the closest thing .NET offers to a
    /// device id. On macOS that is wrong in both directions: APFS volume groups present
    /// <c>/System/Volumes/Data</c> firmlinked under <c>/</c>, so two paths on ONE device can report
    /// different mount points, and a synthetic root can hide a real boundary. So an inconclusive
    /// answer warns rather than staying silent, and the wording never claims certainty.
    /// </para>
    /// </summary>
    private static void WarnIfWorkDirIsOnAnotherDevice(string workDir, string outPath)
    {
        string? tempMount = MountPointOf(workDir);
        string? outMount = MountPointOf(Path.GetDirectoryName(outPath) ?? ".");
        if (tempMount is not null && outMount is not null &&
            string.Equals(tempMount, outMount, StringComparison.Ordinal))
            return;

        Console.Error.WriteLine(
            "warning: --work-dir may be on a different filesystem than --out. If it is, the final " +
            "File.Move is a non-atomic copy rather than a rename, so an interruption can leave a " +
            "partial file at the output path. (Best effort: this cannot be determined reliably on " +
            "this platform.)");
    }

    /// <summary>The longest mounted volume whose root is a prefix of <paramref name="path"/>.</summary>
    private static string? MountPointOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? best = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                string root = drive.RootDirectory.FullName;
                // A PREFIX match is not enough: "/Volumes/Foo" prefixes "/Volumes/FooBar/x", which
                // is a different volume. The match must land on a path separator (or be the root
                // itself, which already ends in one).
                if (!full.StartsWith(root, StringComparison.Ordinal))
                    continue;
                if (!root.EndsWith(Path.DirectorySeparatorChar) &&
                    full.Length > root.Length &&
                    full[root.Length] != Path.DirectorySeparatorChar)
                    continue;
                if (best is null || root.Length > best.Length)
                    best = root;
            }
            return best;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;   // inconclusive: the caller warns
        }
    }

    /// <summary>
    /// Crash recovery, run before anything else: a journal beside the package means a previous
    /// in-place repair was interrupted, so this puts the package back and stops. Returns <c>0</c>
    /// when a journal was found and handled — the caller must exit with it — and <c>-1</c> when
    /// there was nothing to do. Any other non-negative value is likewise the caller's exit code:
    /// the journal was found, could not safely be acted on, and the run must stop.
    ///
    /// <para>
    /// It restores and then STOPS rather than restoring and retrying. Retrying would hide the
    /// interruption from the user entirely, and if a bug caused it, would walk straight back into
    /// it.
    /// </para>
    ///
    /// <para>
    /// Under <paramref name="toOutFile"/> a handled journal still returns NON-ZERO: recovery stops
    /// the run, so an <c>--out</c> invocation that took this path produced no output file, and
    /// exiting 0 would tell a shell <c>&amp;&amp;</c> chain otherwise.
    /// </para>
    ///
    /// <para>
    /// Under <paramref name="dryRun"/> it only reports. "A dry run writes nothing" is a property
    /// users rely on to inspect a package safely, and recovery is the one thing here that would
    /// otherwise mutate a file before the mode was ever consulted — it runs ahead of every guard by
    /// design. So a dry run names the interruption, says the package is not in its repaired state,
    /// and exits non-zero without touching the package or the journal.
    /// </para>
    /// </summary>
    internal static int RecoverIfNeeded(string target, string? workDir, Progress progress,
                                        bool dryRun = false, bool toOutFile = false)
    {
        // THE MARKER FIRST, and its path is the only one here that does not depend on this run's
        // flags. Recomputing the journal path from --work-dir is right only when the recovering run
        // is given the same flags as the run that crashed, which is not something to rely on: /tmp
        // is cleared on reboot, and the flag is easy to forget or to change. When that recomputed
        // path came up empty the command fell through to the detector, which reads entries
        // 4097/8209 out of the already-REPAIRED CNT, found nothing wrong, printed "Nothing to
        // repair" and exited 0 — on a package with a stale SI. The marker is written beside the
        // package before the journal is, so it is found whatever flags this run carries.
        string markerPath = RepairJournal.MarkerPathFor(target);
        bool markerPresent = File.Exists(markerPath);
        string? recorded = markerPresent ? RepairJournal.ReadMarker(markerPath) : null;

        string journalPath = recorded ?? RepairJournal.PathFor(target, workDir);
        var journal = RepairJournal.TryRead(journalPath);

        // A marker with no usable journal is the unrecoverable case, and the one that must never be
        // quiet. The package was left mid-repair and the data that would undo it is gone. There is
        // nothing safe to do, so this says exactly that and stops — it does not "helpfully" fall
        // through to a detector that would call the package healthy.
        if (markerPresent && journal is null)
            return Program.Fail(
                $"'{target}' was left MID-REPAIR by an interrupted in-place run, and its recovery " +
                $"journal is gone. The marker '{markerPath}' records the journal as having been at " +
                $"'{journalPath}'; there is no readable journal there now. The package is NOT in a " +
                "usable state — it carries a repaired CNT with a stale SI — and it cannot be " +
                "restored from here, because the only copy of its original CNT and SI was in that " +
                "journal. If you still have the journal (a --work-dir that was cleared on reboot is " +
                "the usual cause), put it back at that path and re-run. Otherwise restore the " +
                "package from a backup. Do not delete the marker: it is the only remaining record " +
                "that this package is damaged.");

        // Absent, foreign, truncated or torn: nothing this can safely undo. A file that is present
        // but unreadable is deliberately NOT deleted here — it is not ours to destroy, and
        // InPlaceWriter refuses on File.Exists alone, so the run still stops with a message naming
        // the path rather than writing over a package whose history is unknown.
        if (journal is null)
            return -1;

        // Both branches below act destructively — one overwrites the package, the other destroys
        // the only recovery data there is — so the journal must be proven to BELONG to this package
        // before either runs. RepairJournal.Restore makes the same check itself, but the discard
        // branch never reaches Restore, and deleting another package's journal is exactly as
        // unrecoverable as restoring from it.
        if (!CryptographicOperations.FixedTimeEquals(RepairJournal.ComputeIdentity(target),
                                                     journal.Identity))
            return Program.Fail(
                $"the repair journal '{journalPath}' was written for a different package than " +
                $"'{target}'. Restoring from it would destroy this package and discarding it would " +
                "destroy the other's only way back, so this refuses to do either. Move or delete " +
                "the journal by hand once you know which package it belongs to.");

        // Everything above this line only reads. Everything below it writes — so a dry run stops
        // here. Placed after the identity check so a dry run still reports the more accurate of the
        // two problems when the journal turns out to belong to another package.
        if (dryRun)
            return Program.Fail(
                $"a previous in-place repair of '{target}' was interrupted: the journal " +
                $"'{journalPath}' is still on disk, so the package is in neither its original nor " +
                "its repaired state. Re-run without --dry-run to restore it from the journal. " +
                "Nothing was written, and no repair can be planned until the package is whole.");

        // WHICH SIDE of the write did the crash fall on? Get this wrong and the answer destroys the
        // user's package, so the discriminator is chosen with care.
        //
        // NOT PlayGoInitialChunkProblem. Asking "does this package still need repairing?" is the
        // obvious test and it is backwards precisely where it matters: a repair interrupted between
        // steps 2 and 4 has the REPAIRED CNT on disk with a stale SI, so the detector reads entries
        // 4097/8209 out of the repaired CNT, finds nothing wrong, and reports "completed" — on
        // exactly the damaged file that most needs restoring. That answer would delete the journal
        // and leave a broken package with no way back.
        //
        // The file's LENGTH instead. SetLength is the last act of step 4, so:
        //   length == TargetLength -> steps 2-4 did not run to completion  -> RESTORE
        //   length != TargetLength -> the repair ran through; the journal outlived a SUCCESS
        //                             (a File.Delete that failed at step 5) -> DISCARD, never roll back
        // Length is also the one property a half-written CNT cannot forge: step 2 writes the
        // repaired CNT into its own footprint, byte for byte, and changes nothing about the size.
        //
        // This is only sound because the rebuilt SI never GROWS — if it did, the file would pass
        // TargetLength during the SI write and a crash there would read as "completed" and get its
        // journal discarded. That is not left to chance: InPlaceWriter refuses outright when the
        // rebuilt SI is longer than the original, and its comment explains why. If that guard is
        // ever relaxed, this discriminator is wrong and must change with it.
        //
        // The residual risk is the other way round: a rebuilt SI that happens to be EXACTLY as long
        // as the original, which reads as "interrupted" and restores a good repair — annoying, fully
        // recoverable, and the right direction to be wrong in.
        long actualLength = new FileInfo(target).Length;

        if (actualLength != journal.TargetLength)
        {
            File.Delete(journalPath);
            DeleteMarker(markerPath);
            Console.WriteLine($"Package: {target}");
            Console.WriteLine("A repair journal was left behind by a repair that DID complete " +
                              $"(the package is {actualLength:N0} bytes, not the pre-repair " +
                              $"{journal.TargetLength:N0}).");
            Console.WriteLine("The journal has been removed. The package itself was not touched — " +
                              "restoring it would have undone a successful repair.");
            return OutFileWasNotProduced(toOutFile, restored: false);
        }

        RepairJournal.Restore(journalPath, target, progress);
        progress.Finish();
        // Only after the restore is on the device: while the journal exists the recovery is
        // repeatable, and deleting it first would make an interrupted recovery unrecoverable.
        File.Delete(journalPath);
        DeleteMarker(markerPath);

        Console.WriteLine($"Package: {target}");
        Console.WriteLine("A previous in-place repair of this package was interrupted before it " +
                          "finished.");
        Console.WriteLine("The package has been restored to its pre-repair state and the journal " +
                          "removed.");
        Console.WriteLine("Nothing else was done. Re-run the repair when you are ready.");
        return OutFileWasNotProduced(toOutFile, restored: true);
    }

    /// <summary>
    /// Best effort, and deliberately: the marker is a signpost, not recovery data. Failing to remove
    /// it after a successful recovery must not turn that success into an error — the next run finds
    /// a marker with no journal and refuses, which is noisy but safe, whereas failing the recovery
    /// itself would leave a package that IS recoverable looking as though it is not.
    /// </summary>
    private static void DeleteMarker(string markerPath)
    {
        try { File.Delete(markerPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"warning: could not remove the marker '{markerPath}': {ex.Message}. Delete it by " +
                "hand, or the next run will refuse believing this package is still mid-repair.");
        }
    }

    /// <summary>
    /// Recovery's exit code. Zero when the run had nothing else to deliver, NON-ZERO under
    /// <c>--out</c>: recovery acts and then stops, so that run produced no <c>--out</c> file at all.
    /// Returning 0 made <c>fpkg repair-playgo … --out FIXED.pkg &amp;&amp; install FIXED.pkg</c>
    /// proceed onto a file that was never written — or, worse, a stale one from an earlier run. It
    /// matches what the <c>--dry-run</c> branch above already does, and for the same reason.
    /// </summary>
    private static int OutFileWasNotProduced(bool toOutFile, bool restored)
    {
        if (!toOutFile)
            return 0;

        Console.Error.WriteLine(
            "error: NO --out file was produced. This run did nothing but deal with the repair " +
            "journal left behind by an earlier --in-place run, and it stops there rather than " +
            "carrying on with a repair." +
            (restored
                ? " Note that the INPUT package was modified: it has been restored to its " +
                  "pre-repair state, which is the only thing that could safely be done with it."
                : " The input package was not modified."));
        Console.Error.WriteLine("error: re-run the command to produce the --out file.");
        return 1;
    }

    /// <summary>
    /// Stage counts, one per mode. The five shared stages (reading package, parsing the entry
    /// table, recovering PlayGo values, generating replacement entries, resealing the CNT) run in
    /// every mode; the write stages after them do not run for a dry run, and the two writers do not
    /// run the same ones. <see cref="Progress"/> prints <c>[n/total]</c>, so the total has to be the
    /// one for the mode actually being run — a dry run that announced 8 stages and stopped at 5
    /// would read as a failure, and an in-place run announcing the staged writer's 8 would print
    /// <c>[9/8]</c>.
    ///
    /// <para>
    /// 5 + 3 for <c>--out</c> (two staging passes around the CRC table) and 5 + 4 for
    /// <c>--in-place</c>, whose extra stage is the journal <see cref="InPlaceWriter.Write"/> makes
    /// durable before it touches a byte of the package.
    /// </para>
    /// <para>
    /// The read and the parse are TWO stages, not one. A single stage spanning both reports an
    /// elapsed time that attributes to neither, and — the reason it was split — it cannot tell a
    /// banner printed before the work from one printed between the read and the parse: both leave
    /// seconds on the clock. Split, "reading package" reports ~0 ms the moment its banner moves
    /// after <see cref="PackageRegions.Load"/>, which is a difference a test can see.
    /// </para>
    /// </summary>
    private const int DryRunStages = 5;
    private const int WriteStages = 8;
    private const int InPlaceWriteStages = 9;

    /// <summary>
    /// Recovery restores and then STOPS, so it runs exactly one stage and none of the repair's.
    /// It gets its own Progress for that reason alone: sharing the repair's would have printed
    /// "[1/7]" on a run that was always going to end after the first stage.
    /// </summary>
    private const int RecoveryStages = 1;

    private static int Repair(string path, string passcode, string? target, bool inPlace,
                              string? workDir = null, bool verbose = false,
                              bool keepBackup = false)
    {
        // BEFORE every guard, deliberately. The guards ask whether this package is a suitable
        // subject for the repair; this asks whether the file on disk is intact at all, and a
        // half-written package has no business being judged on its suitability first.
        bool isTty = !Console.IsOutputRedirected;

        // target is null exactly when this is a dry run, which is also how the stage count below is
        // chosen — the two must not disagree about the mode.
        int recovery = RecoverIfNeeded(path, workDir,
                                       new Progress(Console.Out, verbose, isTty, RecoveryStages),
                                       dryRun: target is null,
                                       toOutFile: target is not null && !inPlace);
        if (recovery >= 0)
            return recovery;

        var progress = new Progress(Console.Out, verbose, isTty,
                                    totalStages: target is null ? DryRunStages
                                               : inPlace ? InPlaceWriteStages
                                               : WriteStages);

        // FIRST, and before any of the three slow calls below. PlayGoInitialChunkProblem,
        // PackageRegions.Load and CntEntryTable.Parse each take seconds on a 661 MB package, and
        // until this line moved ahead of them the command sat silent through all three.
        Console.WriteLine($"Package:    {path}");

        // UNCONDITIONAL, not under --verbose. If the run is interrupted, this line is the user's
        // record of where the recovery data went — and the moment they most need it is the moment
        // they can no longer ask the command. The marker beside the package records the same path
        // for the machine's benefit; this records it for the human's.
        string? journalPath = inPlace ? RepairJournal.PathFor(path, workDir) : null;
        if (journalPath is not null)
            Console.WriteLine($"Journal:    {journalPath}");

        int Refuse(string message)
        {
            progress.Finish();
            return Program.Fail(message);
        }

        progress.Stage("reading package");
        var regions = PackageRegions.Load(path, progress);

        progress.Stage("parsing the entry table");
        var table = CntEntryTable.Parse(regions.Cnt, passcode);
        string contentId = CntHeader.ReadContentId(regions.Cnt);

        // ---- Guard 1: the passcode -------------------------------------------------------
        // FIRST, and deliberately: every guard below reads entries that Parse has already
        // decrypted, and a wrong passcode turns those into garbage rather than into an error.
        // CheckPasscode needs only ENTRY_KEYS and the content id, so no parsed Header is needed.
        //
        // NOT a decrypt/re-encrypt round trip. For an entry whose DataSize is already 16-aligned
        // that pair is a pure involution — a wrong key yields wrong plaintext and re-encrypting it
        // under the same wrong key returns the original ciphertext exactly. On this package
        // entries 1024, 1025 and 1026 "round-trip successfully" under a deliberately wrong
        // passcode; only 8224 and 8225 detect it. Such a guard passes on any package whose
        // encrypted entries all happen to be 16-aligned.
        var entryKeysMeta = MetaEntry.Read(new MemoryStream(table[EntryKeysId].MetaBytes()));
        var pkg = new Pkg { EntryKeys = KeysEntry.Read(entryKeysMeta, new MemoryStream(regions.Cnt)) };
        pkg.Header.content_id = contentId;
        if (!pkg.CheckPasscode(passcode))
            return Refuse(
                "the passcode does not match this package. Re-encrypting with it would silently " +
                "corrupt the five protected entries while every digest still verified — the " +
                "damage would appear only on a console. Pass the package's real --passcode.");

        // ---- Nothing to repair -----------------------------------------------------------
        // GATED ON THE MARKER, belt and braces. RecoverIfNeeded above already refuses whenever a
        // marker is present, so this cannot normally be reached with one on disk — but "Nothing to
        // repair, exit 0" is the single most dangerous sentence this command can print, because a
        // package left mid-repair reads as healthy here (the detector inspects the already-repaired
        // CNT). It must be impossible to reach it on a damaged package by any route.
        var problem = Program.PlayGoInitialChunkProblem(path, passcode);
        if (problem is null && File.Exists(RepairJournal.MarkerPathFor(path)))
            return Refuse(
                $"'{path}' looks repaired, but the marker " +
                $"'{RepairJournal.MarkerPathFor(path)}' says an in-place repair of it is still in " +
                "progress. A package interrupted mid-repair looks exactly like this — its CNT is " +
                "repaired and its SI is stale — so this refuses rather than reporting it healthy.");
        if (problem is null)
        {
            progress.Finish();
            Console.WriteLine("Nothing to repair: this package's PlayGo map does not place files " +
                              "outside the scenario's initial chunk set.");
            return 0;
        }

        // ---- Guard 6: Application volumes only -------------------------------------------
        // PlayGoEntries passes publisherNwonly: true and includePublisherLabels: true as
        // literals. They are correct for this package, and the spec records them as ASSUMED true
        // for an Application volume and unconfirmed elsewhere. Nothing else enforces that shape,
        // so a non-Application package would regenerate WRONG bytes carrying VALID digests.
        // The expected value is derived from the library, never hard-coded.
        uint contentType = CntHeader.U32(regions.Cnt, CntHeader.ContentType);
        uint applicationContentType = ProsperoPkgBuilder.ContentTypeFor(ProsperoVolumeType.Application);
        if (contentType != applicationContentType)
            return Refuse(
                $"this package's content_type is {contentType} (0x{contentType:X}), not the " +
                $"Application value {applicationContentType} (0x{applicationContentType:X}). " +
                "The repair's PlayGo generation flags (publisherNwonly, includePublisherLabels) " +
                "are only validated for an Application volume; widening the scope needs a " +
                "confirmed non-Application oracle first, not a guess.");

        // ---- Guard 5: the publisher ENTRY_KEYS profile -----------------------------------
        uint entryKeysSize = (uint)table[EntryKeysId].Payload.Length;
        if (entryKeysSize != PublisherEntryKeysSize)
            return Refuse(
                $"ENTRY_KEYS is {entryKeysSize} bytes, not {PublisherEntryKeysSize}. This is not " +
                "the PS5 publisher key profile this repair was derived against, and the body " +
                "rounding the reseal picks depends on it.");

        // ---- Guard 2: the SI segment ------------------------------------------------------
        // SiRepair.Rebuild throws on pfsimage.xml too, but only after the CNT work is done;
        // refusing here keeps the message user-facing and the ordering honest.
        if (regions.Si.Length == 0)
            return Refuse(
                "this package has no SI segment, so its playgo-chunk.dat and CRC table cannot be " +
                "rebuilt alongside the CNT's.");
        var siMembers = SiRepair.ReadMembers(regions.Si);
        foreach (var member in siMembers.OrderBy(m => m.Key, StringComparer.Ordinal))
            progress.Detail($"SI member {member.Key,-44} {member.Value.Length,9:N0} bytes");
        if (siMembers.ContainsKey(SiRepair.PfsImageXmlPath))
            return Refuse(
                $"the SI segment carries '{SiRepair.PfsImageXmlPath}', which encodes the full entry " +
                "table and every digest. Reproducing it is unverified, so the repair would leave " +
                "the package describing itself incorrectly.");
        // EVERY remaining structural condition SiRepair.Rebuild needs, asked here rather than only
        // there. Rebuild runs at step 3 of an in-place write — after the repaired CNT is on the
        // device — so a missing naps_meta_18 or a mismatched naps_meta_3xx discovered inside it is a
        // refusal that lands on an already-mutated package. Discovered here it costs nothing. The
        // members are the ones already read above, so this adds no I/O.
        try { SiRepair.EnsureRebuildable(siMembers); }
        catch (InvalidDataException ex) { return Refuse(ex.Message); }

        // ---- Guard 4: generic scenario presentation --------------------------------------
        progress.Stage("recovering PlayGo values");
        var recovered = PlayGoRecovery.From(table[ChunkDatId].Payload);
        DumpRecovered(progress, recovered);
        EnsureScenarioJsonIsGeneric(table[ScenarioId].Payload, recovered);

        // ---- Guard 3: the body must not move ----------------------------------------------
        // CntRepair.Repair asks the exact question (does the reseal's body_size change?) and
        // throws naming both numbers and the unimplemented body_size-bump fallback. It writes
        // nothing, so reaching it before any output is opened is what makes the refusal safe.
        progress.Stage("generating replacement entries");
        var rebuilt = PlayGoEntries.Build(recovered, table[FicmId].Payload, progress);
        progress.Detail($"playgo-chunk.dat     {rebuilt.ChunkDat.Length,9:N0} bytes");
        progress.Detail($"playgo-ficm.dat      {rebuilt.Ficm.Length,9:N0} bytes");
        progress.Detail($"playgo-scenario.json {rebuilt.ScenarioJson.Length,9:N0} bytes");

        progress.Stage("resealing the CNT");
        var repaired = CntRepair.Repair(regions.Cnt, contentId, passcode, progress);
        var recoveredAfter = PlayGoRecovery.From(repaired.NewChunkDat);
        // Re-parsed HERE rather than inside ReportPlan: it is the slowest thing the report needs,
        // and leaving it there kept the command silent immediately after the "Digests that would
        // be recomputed" heading — the second silent gap the user reported.
        var newTable = CntEntryTable.Parse(repaired.Cnt, passcode);
        progress.Finish();

        ReportPlan(contentId, problem, regions, table, newTable, recovered, recoveredAfter,
                   rebuilt, repaired, siMembers);

        if (target is null)
        {
            Console.WriteLine();
            Console.WriteLine("dry run: nothing written. Pass --out <path> or --in-place to write.");
            return 0;
        }

        // The two modes take different write paths, and every guard above has already run for
        // both. --in-place rewrites the package itself under a journal, needing no free space
        // beside it and leaving no temporary file; --out stages into a temporary and renames.
        if (inPlace)
            InPlaceWriter.Write(regions, repaired, contentId, target, journalPath!, progress,
                                backupPath: keepBackup ? RepairJournal.BackupPathFor(target) : null);
        else
            WriteRepaired(regions, repaired, contentId, target, workDir: workDir,
                          progress: progress);
        progress.Finish();
        Console.WriteLine();
        Console.WriteLine($"written: {target}");
        return 0;
    }

    /// <summary>
    /// Guard 4, exposed so its negative path is testable without a doctored package.
    ///
    /// <para>
    /// <b>Not a whole-file byte-compare.</b> That was the obvious implementation and it is wrong:
    /// measured on the test package, the stored file is 1,981 bytes and
    /// <c>BuildScenarioJson</c> produces 2,293 for the same inputs. The whole difference is two
    /// members 0.6.9 ADDS — <c>chunkDefaultLanguage</c> and <c>chunkSupportedLanguages</c> — and
    /// the scenario presentation is identical. A whole-file compare would therefore refuse every
    /// package, including the one this repair exists for.
    /// </para>
    /// <para>
    /// The rule is instead: every member the stored file declares must be reproduced by
    /// <c>BuildScenarioJson</c> with an identical raw value, and the regenerated file may only
    /// ADD members. The per-scenario blocks carry a title and a description for every selected
    /// language, so a package holding real localised scenario names trips this — which is the
    /// CORRECT outcome: regeneration would replace those names with generic "Scenario #N"
    /// labels, and this repair has no way to carry the source presentation through. The stored
    /// file is not malformed; it holds something that cannot survive regeneration.
    /// </para>
    /// <para>
    /// The expected bytes come from the same call <see cref="PlayGoEntries.Build"/> makes, so
    /// this guard and the regeneration cannot disagree about what "generic" means.
    /// </para>
    /// </summary>
    internal static void EnsureScenarioJsonIsGeneric(byte[] scenarioJson, PlayGoRecovery r)
    {
        byte[] generic = ProsperoPlayGo.BuildScenarioJson(r.ScenarioCount, r.LanguageMask, r.DefaultScenarioId);

        using var stored = JsonDocument.Parse(scenarioJson);
        using var regenerated = JsonDocument.Parse(generic);

        foreach (var member in stored.RootElement.EnumerateObject())
        {
            if (!regenerated.RootElement.TryGetProperty(member.Name, out var want))
                throw new InvalidOperationException(
                    $"playgo-scenario.json declares '{member.Name}', which the regenerated file " +
                    "would drop. Losing a member the package already relies on is not a repair, " +
                    "so this refuses rather than writing a file that silently says less.");

            if (string.Equals(member.Value.GetRawText(), want.GetRawText(), StringComparison.Ordinal))
                continue;

            throw new InvalidOperationException(member.Name == ScenariosMember
                ? "this package carries custom scenario presentation; regenerating " +
                  "playgo-scenario.json would replace it with generic 'Scenario #N' labels. The " +
                  "stored file is not malformed — it holds per-language scenario titles or " +
                  "descriptions that this repair cannot preserve, so it refuses rather than " +
                  "discarding them. (The comparison is over raw JSON text, so a file written " +
                  "with different whitespace or member ordering trips this too, with the same " +
                  "outcome and a different cause.)"
                : $"playgo-scenario.json's '{member.Name}' is {member.Value.GetRawText()} in this " +
                  $"package but would be regenerated as {want.GetRawText()}. The repair derives " +
                  "that value from playgo-chunk.dat, so a disagreement means the two files do not " +
                  "describe the same layout; refusing rather than picking one.");
        }
    }

    /// <summary>
    /// The whole recovered record, under <c>--verbose</c> only. The summary in the plan report
    /// prints the six values a reader usually wants; this prints every field, including the
    /// scenario labels and the per-chunk language masks the regeneration is keyed to.
    /// </summary>
    private static void DumpRecovered(Progress progress, PlayGoRecovery r)
    {
        progress.Detail($"contentId          {r.ContentId}");
        progress.Detail($"chunkCount         {r.ChunkCount}");
        progress.Detail($"scenarioCount      {r.ScenarioCount}");
        progress.Detail($"defaultScenarioId  {r.DefaultScenarioId}");
        progress.Detail($"defaultLanguageId  {r.DefaultLanguageId}");
        progress.Detail($"languageMask       0x{r.LanguageMask:X}");
        progress.Detail($"extentCount        {r.ExtentCount}");
        progress.Detail($"dataSize           {r.DataSize:N0} bytes");
        progress.Detail($"tailSize           {r.TailSize:N0} bytes");
        progress.Detail($"totalSize          {r.TotalSize:N0} bytes");
        progress.Detail($"scenarioLabels     {string.Join(", ", r.ScenarioLabels)}");
    }

    private static void ReportPlan(
        string contentId, string problem,
        PackageRegions regions, CntEntryTable table, CntEntryTable newTable,
        PlayGoRecovery before, PlayGoRecovery after, PlayGoEntries rebuilt,
        CntRepairResult repaired, IReadOnlyDictionary<string, byte[]> siMembers)
    {
        Console.WriteLine();
        Console.WriteLine($"Content ID: {contentId}");
        // Why this package needs repairing at all, in the same words `verify --quick` uses.
        Console.WriteLine($"Problem:    {problem}");
        Console.WriteLine();

        Console.WriteLine("Recovered from playgo-chunk.dat");
        Console.WriteLine($"  chunks={before.ChunkCount} scenarios={before.ScenarioCount} " +
                          $"extents {before.ExtentCount} -> {after.ExtentCount}");
        Console.WriteLine($"  language mask 0x{before.LanguageMask:X}, default scenario " +
                          $"{before.DefaultScenarioId}, default language {before.DefaultLanguageId}");
        Console.WriteLine($"  data {before.DataSize:N0} + tail {before.TailSize:N0} = " +
                          $"{before.TotalSize:N0} bytes");
        Console.WriteLine();

        Console.WriteLine("CNT entries to be regenerated");
        Line(ChunkDatId, "playgo-chunk.dat", table[ChunkDatId].Payload.Length, rebuilt.ChunkDat.Length);
        Line(FicmId, "playgo-ficm.dat", table[FicmId].Payload.Length, rebuilt.Ficm.Length);
        Line(ScenarioId, "playgo-scenario.json", table[ScenarioId].Payload.Length, rebuilt.ScenarioJson.Length);
        Console.WriteLine();

        Console.WriteLine("Body layout");
        Console.WriteLine($"  slack at the end of the body   {repaired.SlackBefore:N0} bytes");
        Console.WriteLine($"  net change in entry sizes      {repaired.NetDelta:+#,#;-#,#;0} bytes");
        Console.WriteLine($"  body_size                      unchanged " +
                          $"(0x{CntHeader.U64(regions.Cnt, CntHeader.BodySize):X})");
        Console.WriteLine();

        Console.WriteLine("Digests that would be recomputed");
        foreach (var (name, offset, length) in new (string, int, int)[]
                 {
                     ("header sc_entries1_hash", CntHeader.ScEntries1Hash, 32),
                     ("header sc_entries2_hash", CntHeader.ScEntries2Hash, 32),
                     ("header digest_table_hash", CntHeader.DigestTableHash, 32),
                     ("header body_digest", CntHeader.BodyDigest, 32),
                     ("header pfs_image_digest", CntHeader.PfsImageDigest, 32),
                     ("header desc_digest", CntHeader.DescDigest, 64),
                     ("header package_digest", CntHeader.PackageDigest, 32),
                 })
        {
            bool changed = !regions.Cnt.AsSpan(offset, length)
                .SequenceEqual(repaired.Cnt.AsSpan(offset, length));
            Console.WriteLine($"  {(changed ? "changed  " : "unchanged")} {name}");
        }

        Console.WriteLine($"  changed   {ChangedDigestRows(table, newTable)} of " +
                          $"{table[DigestsId].Payload.Length / 32} per-entry rows in DIGESTS");
        bool generalDigestsChanged = !table[GeneralDigestsId].Payload.AsSpan()
            .SequenceEqual(newTable[GeneralDigestsId].Payload);
        Console.WriteLine($"  {(generalDigestsChanged ? "changed  " : "unchanged")} " +
                          $"GENERAL_DIGESTS ({GeneralDigestsId})");
        // Same idiom SiRepair.Rebuild uses for its own required members: a package whose SI has no
        // CRC table for this content id is one the repair cannot rebuild, and saying so here —
        // before anything is written — beats an indexer's bare KeyNotFoundException.
        string crcPath = $"config/{contentId}/playgo-chunk.crc";
        byte[] crc = siMembers.TryGetValue(crcPath, out var stored)
            ? stored
            : throw new InvalidDataException(
                $"the SI segment has no '{crcPath}' member, so there is no CRC table for this " +
                "package's content id to recompute. repair-playgo rebuilds that table rather than " +
                "creating one, so it refuses rather than inventing a member the package never had.");
        Console.WriteLine($"  changed   SI {crcPath} ({crc.Length / 4:N0} entries)");
        Console.WriteLine($"  changed   SI {SiRepair.ChunkDatPath}");

        static void Line(uint id, string name, int from, int to) =>
            Console.WriteLine($"  {id,-6} {name,-22} {from,7:N0} -> {to,-7:N0}" +
                              (from == to ? " (unchanged)" : ""));
    }

    /// <summary>
    /// DIGESTS' payload is re-parsed from the resealed container rather than recomputed here, so
    /// the count reported is the one that would really be written.
    /// </summary>
    private static int ChangedDigestRows(CntEntryTable before, CntEntryTable after)
    {
        byte[] a = before[DigestsId].Payload, b = after[DigestsId].Payload;
        int rows = Math.Min(a.Length, b.Length) / 32, changed = 0;
        for (int i = 0; i < rows; i++)
            if (!a.AsSpan(i * 32, 32).SequenceEqual(b.AsSpan(i * 32, 32))) changed++;
        return changed;
    }

    /// <summary>
    /// The <c>--out</c> writer. Writes in two passes into a temporary file BESIDE the target, and
    /// renames onto the target only once the write has fully succeeded.
    ///
    /// <para>
    /// Two passes because the SI's CRC table is computed over the REPAIRED mount image (FIH +
    /// outer PFS + repaired CNT), so those bytes have to exist on disk before the SI can be
    /// rebuilt. The first pass writes an empty SI; its contents cannot affect the CRC, which
    /// covers only <c>[0, CntOffset + Cnt.Length)</c>. That pass is NOT fsynced: only
    /// <c>BuildChunkCrc</c>, in this same process, reads it back, and the page cache serves that
    /// read. The second pass keeps the flush, which is what makes the rename safe.
    /// </para>
    /// <para>
    /// Only the FIRST pass writes the package. The second used to call
    /// <see cref="PackageRegions.WriteTo"/> again, which begins with a create-and-truncate, so the
    /// whole payload was rewritten to swap a ~660 KB SI tail: on a 90 GB package, ~180 GB written
    /// for 90 GB of output, seen by the user who reported it as the temporary growing to 80 GB,
    /// vanishing and regrowing. It now seeks, exactly as <see cref="InPlaceWriter"/> does, via
    /// <see cref="PackageRegions.OverwriteSi"/>. No journal is involved: the file being edited is
    /// the staging temporary, and the target is untouched until the rename below.
    /// </para>
    /// <para>
    /// The payload is copied through verbatim, once — never decoded, recompressed or modified — so
    /// the repair still needs free space beside the target roughly equal to the package's own
    /// size. <c>--in-place</c> needs none of it: it writes ~64 MB (the journal, plus the CNT and
    /// SI in their own footprint) whatever the package's size.
    /// </para>
    /// <para>
    /// The rename refuses to clobber, which closes the TOCTOU window between the existence check
    /// in <see cref="Run"/> and this rename. It used to take an <c>overwrite</c> flag, set only by
    /// <c>--in-place</c>; that mode now goes through <see cref="InPlaceWriter"/>, so nothing here
    /// has any business replacing a file that already exists.
    /// </para>
    /// </summary>
    /// <param name="openStagingFile">
    /// How the staging temporary is opened, for both passes. Null means the real filesystem, which
    /// is what production uses; a test passes a byte-counting stream through it to assert that this
    /// path writes the payload once rather than twice.
    /// </param>
    internal static void WriteRepaired(PackageRegions regions, CntRepairResult repaired,
                                       string contentId, string target,
                                       string? workDir, Progress progress,
                                       Func<string, FileMode, Stream>? openStagingFile = null)
    {
        // The invariant the seek in pass 2 rests on, asserted as InPlaceWriter asserts it: pass 1
        // writes repaired.Cnt at CntOffset, and pass 2 puts the SI at CntOffset + Cnt.Length. If
        // the repaired CNT were not exactly as long as the region it replaces, that offset would
        // land inside the CNT or leave a hole, and every digest would be computed over the wrong
        // bytes.
        if (repaired.Cnt.LongLength != regions.Cnt.LongLength)
            throw new InvalidOperationException(
                $"the repaired CNT is {repaired.Cnt.LongLength:N0} bytes but the region it must occupy is " +
                $"{regions.Cnt.LongLength:N0}; the staged writer places the SI at the end of the CNT, " +
                "so a CNT that changed length would put it at the wrong offset.");

        // The staging file sits beside the target by default, so that File.Move is a rename within
        // one filesystem. --work-dir moves it elsewhere on the user's say-so; Run has already
        // created that directory and warned about the rename it may weaken.
        string directory = Path.GetDirectoryName(Path.GetFullPath(target)) ?? ".";
        Directory.CreateDirectory(directory);
        string tmp = Path.Combine(workDir ?? directory,
                                  Path.GetFileName(target) + ".repair-playgo.tmp");

        try
        {
            progress.Stage("staging pass 1 (repaired CNT, empty SI)");
            regions.WriteTo(tmp, repaired.Cnt, [], flushToDisk: false, progress: progress,
                            openDestination: openStagingFile is null
                                ? null
                                : path => openStagingFile(path, FileMode.Create));

            byte[] newSi;
            progress.Stage("computing the mount CRC table");
            using (var mountFile = File.OpenRead(tmp))
            {
                // BuildChunkCrc reduces over the whole mount image — the longest single step of the
                // repair. It logs its own lines, which Progress.Detail surfaces under --verbose;
                // the percentage comes from the read position, so a quiet run is not silent either.
                using var mount = new ReadingProgressStream(mountFile, progress);
                newSi = SiRepair.Rebuild(regions.Si, contentId, repaired.NewChunkDat,
                                         mount, regions.CntOffset + repaired.Cnt.Length,
                                         progress);
            }

            // Named for what it now does. It no longer restages anything: the payload and the CNT
            // pass 1 wrote stay on disk untouched and only the SI tail is written.
            progress.Stage("writing the rebuilt SI onto the staged file");
            regions.OverwriteSi(tmp, repaired.Cnt.LongLength, newSi, progress: progress,
                                openStaged: openStagingFile is null
                                    ? null
                                    : path => openStagingFile(path, FileMode.Open));
            CopyFileMode(regions.SourcePath, tmp);
            File.Move(tmp, target, overwrite: false);
        }
        catch
        {
            // The target is untouched until the Move above.
            try { File.Delete(tmp); } catch (Exception) { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Carries the source package's Unix permissions onto the temporary file before the rename.
    /// Without it <c>--in-place</c> silently WIDENS its target: the temp is created with the
    /// process umask's default (typically 0644) while the test package on disk is 0700, and after
    /// the rename the mode is the temp's, not the original's. No-op on platforms with no Unix
    /// file mode, and best effort — a permissions failure must not lose a completed repair.
    /// </summary>
    private static void CopyFileMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(source);
            if (mode != UnixFileMode.None) File.SetUnixFileMode(destination, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            // Best effort: the repaired bytes matter more than the mode bits.
        }
    }
}
