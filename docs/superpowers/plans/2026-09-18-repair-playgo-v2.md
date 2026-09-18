# `fpkg repair-playgo` v2 — journalled in-place writes, `--temp-dir`, staged progress

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `--in-place` genuinely in-place — writing ~64 MB instead of ~1.32 GB and needing no spare disk — while staying crash-recoverable, and give the command real progress output.

**Architecture:** Two invariants the repair already enforces make a true in-place write possible: the outer PFS never changes, and the CNT region's length is fixed (`body_size` unchanged is a guard; `Cnt.Length == body_offset + body_size` is a precondition). So the repaired CNT drops back into its own footprint and only the trailing SI changes length. The crash window that opens between writing the CNT and writing the SI is closed by journalling the two regions that change (~64 MB) to a sidecar before touching the package, and restoring from it on the next run if it is still there.

**Tech Stack:** C# / .NET 10, LibProsperoPkg 0.6.9 (referenced, not modified), xunit + `Xunit.SkippableFact`.

**Spec:** `docs/repair-playgo-spec.md`. The v1 plan is `docs/superpowers/plans/2026-09-18-repair-playgo.md` — read its Global Constraints; they all still bind.

---

## Global Constraints

Everything from the v1 plan still applies. In addition:

- **The acceptance gate must stay green and must stay byte-exact.** `--in-place` and `--out` must produce *identical bytes*, and both must equal the oracle. If a change makes them differ, stop — do not adjust the comparison.
- **All eight refusal points still run before any mutation**, including before the journal is written. A package that will be refused must never see its journal created.
- **The journal is written and flushed to disk before the package is opened for writing.** No exceptions, no reordering.
- **Never `--force` past a journal.** Its presence means a previous run died mid-write; the file is in an unknown state and must be restored first.
- Tests needing the 661 MB package or the oracle use `[SkippableFact]` + `Skip.IfNot(...)`. A silently-skipping test is a defect.
- No reflection into private members of LibProsperoPkg.
- Commit each task; end every commit message with:
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`

### Measured geometry of the test package

```
file                 661,006,510            repaired: 661,005,150   (−1,360)
outer PFS            offset 0x10000   size 596,705,280   NEVER CHANGES
CNT region           offset 596,770,816  size 63,569,920  LENGTH INVARIANT
SI (last region)     offset 660,340,736  size 665,774     repaired: 664,414
journal payload      CNT + SI = 63,569,920 + 665,774 = 64,235,694  (~64.2 MB)
```

`Cnt.Length` is invariant because `CntRepair` refuses any relayout that changes `body_size`, and refuses a CNT region carrying padding past its body. Both guards already exist and are tested.

---

## File Structure

New, under `fpkg-cli/RepairPlayGo/`:

| file | responsibility |
|---|---|
| `Progress.cs` | staged progress reporting: stage lines, throttled percentages, `--verbose` detail, TTY vs pipe |
| `RepairJournal.cs` | the sidecar: write, validate, restore, delete |
| `InPlaceWriter.cs` | the journalled in-place write sequence |

Modified:

| file | change |
|---|---|
| `RepairPlayGoCommand.cs` | `--temp-dir`, `--verbose`; journal recovery; route `--in-place` to `InPlaceWriter`; progress through every stage |
| `PackageRegions.cs` | `WriteTo` takes an optional progress callback |
| `CntReseal.cs` | `Seal` takes an optional progress callback (the body digest is 63 MB) |
| `SiRepair.cs` | `Rebuild` forwards a progress callback to `BuildChunkCrc` |
| `docs/repair-playgo-spec.md`, `CHANGELOG.md`, `dist/README.md` | document all of it |

New tests: `ProgressTests.cs`, `RepairJournalTests.cs`, `InPlaceWriterTests.cs`, plus additions to `AcceptanceTests.cs`.

---

### Task 1: Progress reporting

**Files:**
- Create: `fpkg-cli/RepairPlayGo/Progress.cs`
- Test: `fpkg-cli.Tests/ProgressTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  ```csharp
  internal sealed class Progress
  {
      internal Progress(TextWriter output, bool verbose, bool isTty, int totalStages);
      /// <summary>Starts a stage. Closes the previous one with its elapsed time.</summary>
      internal void Stage(string name);
      /// <summary>Throttled percentage within the current stage. Safe to call per 80 KiB block.</summary>
      internal void Report(long done, long total);
      /// <summary>Detail printed only under --verbose.</summary>
      internal void Detail(string line);
      /// <summary>Closes the final stage.</summary>
      internal void Finish();
  }
  ```

Throttle: emit at most every 250 ms **or** every 5 percentage points, whichever is sooner. On a TTY rewrite the line with `\r`; when piped, print a new line per emission so logs stay readable. Never emit 0% or a duplicate percentage.

- [ ] **Step 1: Write the failing test**

```csharp
public class ProgressTests
{
    [Fact]
    public void StagesAreNumberedAndClosedWithElapsedTime()
    {
        var sw = new StringWriter();
        var p = new Progress(sw, verbose: false, isTty: false, totalStages: 2);
        p.Stage("reading package");
        p.Stage("resealing");
        p.Finish();

        string s = sw.ToString();
        Assert.Contains("[1/2] reading package", s);
        Assert.Contains("[2/2] resealing", s);
        Assert.Equal(2, Regex.Matches(s, @"\d+(\.\d+)?\s*(ms|s)\b").Count);
    }

    [Fact]
    public void DetailIsSuppressedUnlessVerbose()
    {
        var quiet = new StringWriter();
        new Progress(quiet, verbose: false, isTty: false, totalStages: 1).Detail("inner value 42");
        Assert.DoesNotContain("inner value 42", quiet.ToString());

        var loud = new StringWriter();
        new Progress(loud, verbose: true, isTty: false, totalStages: 1).Detail("inner value 42");
        Assert.Contains("inner value 42", loud.ToString());
    }

    [Fact]
    public void PercentagesAreThrottledAndMonotonic()
    {
        var sw = new StringWriter();
        var p = new Progress(sw, verbose: false, isTty: false, totalStages: 1);
        p.Stage("writing");
        for (long i = 0; i <= 1000; i++) p.Report(i, 1000);
        p.Finish();

        var pcts = Regex.Matches(sw.ToString(), @"(\d+)%").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(pcts);
        Assert.True(pcts.Count <= 21, $"expected at most 21 emissions at 5% granularity, got {pcts.Count}");
        Assert.Equal(pcts.OrderBy(x => x), pcts);            // monotonic
        Assert.Equal(pcts.Distinct(), pcts);                 // no duplicates
        Assert.DoesNotContain(0, pcts);
    }

    [Fact]
    public void ReportBeforeAnyStageDoesNotThrow()
    {
        var p = new Progress(new StringWriter(), verbose: false, isTty: false, totalStages: 1);
        p.Report(1, 2);      // must be a no-op, not a crash
        p.Finish();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter Progress`
Expected: FAIL — `Progress` does not exist.

- [ ] **Step 3: Implement `Progress.cs`**

Hold `Stopwatch` per stage; track `lastEmitTicks` and `lastPercent`. `Report` computes `(int)(done * 100 / total)`, returns early when `total <= 0`, when no stage is open, when the percent is unchanged, or when both throttles say no. On `isTty`, write `\r  {name} {pct,3}%` with no newline and clear the line on `Stage`/`Finish`; otherwise write `  {name} {pct}%` plus a newline.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter Progress`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/Progress.cs fpkg-cli.Tests/ProgressTests.cs
git commit -m "feat: staged progress reporting with throttled percentages"
```

---

### Task 2: The repair journal

**Files:**
- Create: `fpkg-cli/RepairPlayGo/RepairJournal.cs`
- Test: `fpkg-cli.Tests/RepairJournalTests.cs`

**Interfaces:**
- Consumes: `Progress`.
- Produces:
  ```csharp
  internal sealed record RepairJournal(
      long TargetLength, long CntOffset, long CntLength, long SiLength, byte[] Identity)
  {
      internal const string Suffix = ".repair-playgo.journal";
      internal static string PathFor(string target, string? tempDir);
      /// <summary>SHA-256 of file[0, 65536) — the FIH block, which the repair never touches.</summary>
      internal static byte[] ComputeIdentity(string packagePath);
      /// <summary>Writes and FLUSHES TO DISK. Returns when the journal is durable.</summary>
      internal static void Write(string journalPath, string packagePath, PackageRegions regions,
                                 Progress progress);
      /// <summary>Null when absent or unreadable; never throws on a torn journal.</summary>
      internal static RepairJournal? TryRead(string journalPath);
      /// <summary>Restores the CNT and SI regions and truncates. Throws if the identity disagrees.</summary>
      internal static void Restore(string journalPath, string target, Progress progress);
  }
  ```

**On-disk layout** — all integers little-endian:

```
 0   8   magic "FPKGJRN1"
 8   8   TargetLength    original file length
16   8   CntOffset
24   8   CntLength
32   8   SiLength
40  32   Identity        SHA-256 of the PACKAGE's first 65,536 bytes
72   .   Cnt             CntLength bytes, the ORIGINAL CNT region
 .   .   Si              SiLength bytes, the ORIGINAL SI region
 .  32   Digest          SHA-256 over everything preceding it
```

The identity is deliberately taken over a region the repair **never modifies**, so it still matches after a partial write. Hashing the CNT would not — a half-finished run has the *repaired* CNT on disk.

- [ ] **Step 1: Write the failing test**

```csharp
public class RepairJournalTests
{
    private static readonly string Passcode = new string('0', 32);

    [SkippableFact]
    public void RoundTripsAndRestoresAMutatedPackage()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var before = Bytes.Sha256(copy);
            var progress = new Progress(TextWriter.Null, false, false, 1);

            RepairJournal.Write(journal, copy, regions, progress);
            var read = RepairJournal.TryRead(journal);
            Assert.NotNull(read);
            Assert.Equal(new FileInfo(copy).Length, read!.TargetLength);
            Assert.Equal(regions.CntOffset, read.CntOffset);
            Assert.Equal(regions.Cnt.Length, read.CntLength);
            Assert.Equal(regions.Si.Length, read.SiLength);

            // Corrupt the CNT and the tail the way a half-finished repair would.
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write))
            {
                fs.Position = regions.CntOffset;
                fs.Write(new byte[4096]);
                fs.SetLength(fs.Length - 1_000);
            }
            Assert.NotEqual(before, Bytes.Sha256(copy));

            RepairJournal.Restore(journal, copy, progress);
            Assert.Equal(before, Bytes.Sha256(copy));
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }

    [SkippableFact]
    public void RefusesToRestoreOverADifferentPackage()
    {
        Skip.IfNot(TestPackage.Exists && Oracle.Available, "artifacts not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var other = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            File.Copy(Oracle.Path, other);
            RepairJournal.Write(journal, copy, PackageRegions.Load(copy),
                                new Progress(TextWriter.Null, false, false, 1));
            var ex = Assert.Throws<InvalidDataException>(
                () => RepairJournal.Restore(journal, other, new Progress(TextWriter.Null, false, false, 1)));
            Assert.Contains("different package", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(copy); File.Delete(other); File.Delete(journal); }
    }

    [Fact]
    public void ATornJournalReadsAsNullRatherThanThrowing()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + RepairJournal.Suffix);
        try
        {
            File.WriteAllBytes(p, "FPKGJRN1"u8.ToArray());     // header only, no body, no digest
            Assert.Null(RepairJournal.TryRead(p));
            File.WriteAllBytes(p, new byte[8]);                // wrong magic
            Assert.Null(RepairJournal.TryRead(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void PathForHonoursTempDir()
    {
        Assert.Equal(Path.Combine("/tmp/x", "a.pkg" + RepairJournal.Suffix),
                     RepairJournal.PathFor("/pkgs/a.pkg", "/tmp/x"));
        Assert.Equal(Path.Combine("/pkgs", "a.pkg" + RepairJournal.Suffix),
                     RepairJournal.PathFor("/pkgs/a.pkg", null));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter RepairJournal`
Expected: FAIL — `RepairJournal` does not exist.

- [ ] **Step 3: Implement `RepairJournal.cs`**

`Write` streams the CNT and SI from `regions` (both already in memory), hashing as it goes with an `IncrementalHash`, appends the digest, then `Flush(flushToDisk: true)` before returning. `TryRead` validates magic, length and digest, returning `null` on any mismatch. `Restore` re-reads the journal, verifies `ComputeIdentity(target)` equals the stored identity — throwing `InvalidDataException` naming both paths and saying the journal belongs to a **different package** — then writes CNT at `CntOffset`, SI after it, `SetLength(TargetLength)`, and flushes to disk.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter RepairJournal`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/RepairJournal.cs fpkg-cli.Tests/RepairJournalTests.cs
git commit -m "feat: journal the CNT and SI regions so an in-place repair is recoverable"
```

---

### Task 3: The journalled in-place writer

**Files:**
- Create: `fpkg-cli/RepairPlayGo/InPlaceWriter.cs`
- Modify: `fpkg-cli/RepairPlayGo/SiRepair.cs` (progress callback on `Rebuild`)
- Test: `fpkg-cli.Tests/InPlaceWriterTests.cs`

**Interfaces:**
- Consumes: `RepairJournal`, `Progress`, `PackageRegions`, `CntRepairResult`, `SiRepair`.
- Produces:
  ```csharp
  internal static class InPlaceWriter
  {
      internal static void Write(PackageRegions regions, CntRepairResult repaired,
                                 string contentId, string target, string journalPath,
                                 Progress progress);
  }
  ```

**The sequence, and it must be exactly this order:**

1. `RepairJournal.Write(journalPath, target, regions, progress)` — durable before anything else.
2. Open `target` with `FileMode.Open, FileAccess.ReadWrite`. Seek to `regions.CntOffset`. Write `repaired.Cnt` (63,569,920 bytes, reporting progress). `Flush(flushToDisk: true)`.
3. `SiRepair.Rebuild(regions.Si, contentId, repaired.NewChunkDat, <the same open stream>, regions.CntOffset + repaired.Cnt.Length, progress)` — the CRC now reads the *real* file, which after step 2 already is the repaired mount image. No temp file is involved.
4. Seek to `regions.CntOffset + repaired.Cnt.Length`. Write the new SI. `SetLength(that offset + newSi.Length)`. `Flush(flushToDisk: true)`.
5. `File.Delete(journalPath)`.

Assert before step 2 that `repaired.Cnt.Length == regions.Cnt.Length`; if they ever differ the whole design is void, so throw `InvalidOperationException` naming both. That invariant is guaranteed by `CntRepair`'s two guards, and this is the one place that depends on it absolutely.

- [ ] **Step 1: Write the failing test**

```csharp
public class InPlaceWriterTests
{
    private static readonly string Passcode = new string('0', 32);

    [SkippableFact]
    public void ProducesTheSameBytesAsTheStagedWriterAndLeavesNoJournal()
    {
        Skip.IfNot(TestPackage.Exists && Oracle.Available, "artifacts not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var repaired = CntRepair.Repair(regions.Cnt, CntHeader.ReadContentId(regions.Cnt), Passcode);

            InPlaceWriter.Write(regions, repaired, CntHeader.ReadContentId(regions.Cnt),
                                copy, journal, new Progress(TextWriter.Null, false, false, 4));

            Assert.False(File.Exists(journal), "the journal must be gone after a successful write");
            Assert.Equal(Bytes.Sha256(Oracle.Path), Bytes.Sha256(copy));
            Assert.Equal(new FileInfo(Oracle.Path).Length, new FileInfo(copy).Length);
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }

    [SkippableFact]
    public void TheOuterPayloadIsNeverRewritten()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var regions = PackageRegions.Load(copy);
            var payloadBefore = Bytes.Sha256Range(copy, regions.OuterPfsOffset, regions.OuterPfsSize);
            var repaired = CntRepair.Repair(regions.Cnt, CntHeader.ReadContentId(regions.Cnt), Passcode);

            InPlaceWriter.Write(regions, repaired, CntHeader.ReadContentId(regions.Cnt),
                                copy, journal, new Progress(TextWriter.Null, false, false, 4));

            Assert.Equal(payloadBefore, Bytes.Sha256Range(copy, regions.OuterPfsOffset, regions.OuterPfsSize));
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter InPlaceWriter`
Expected: FAIL — `InPlaceWriter` does not exist.

- [ ] **Step 3: Give `SiRepair.Rebuild` a progress callback and an already-open stream**

It already takes a `Stream mountImage`; add `Progress progress` and forward a logger into `ProsperoPlayGo.BuildChunkCrc(stream, length, CancellationToken.None, line => progress.Detail(line))`. Do not change its existing behaviour or the CRC length rule.

- [ ] **Step 4: Implement `InPlaceWriter.cs` following the five-step sequence above**

- [ ] **Step 5: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter InPlaceWriter`
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add fpkg-cli/RepairPlayGo/InPlaceWriter.cs fpkg-cli/RepairPlayGo/SiRepair.cs fpkg-cli.Tests/InPlaceWriterTests.cs
git commit -m "feat: write the repair in place, journalled, without staging the payload"
```

---

### Task 4: Crash recovery

**Files:**
- Modify: `fpkg-cli/RepairPlayGo/RepairPlayGoCommand.cs`
- Test: `fpkg-cli.Tests/InPlaceWriterTests.cs` (add)

**Interfaces:**
- Consumes: `RepairJournal`.
- Produces: `internal static int RecoverIfNeeded(string target, string? tempDir, Progress progress)` — returns `0` when a journal was found and restored (caller exits), `-1` when there was nothing to do.

Behaviour: if a journal exists for the target, restore it, print plainly what happened — that a previous run was interrupted, that the package has been restored to its pre-repair state, and that the repair can be re-run — and exit 0 **without** attempting the repair. Restoring and then immediately retrying would hide the interruption and, if the cause was a bug, repeat it.

This runs before the guards, because it is about the file's integrity rather than its suitability.

- [ ] **Step 1: Write the failing test**

```csharp
    [SkippableFact]
    public void AnInterruptedRepairIsRestoredOnTheNextRun()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        var journal = copy + RepairJournal.Suffix;
        try
        {
            File.Copy(TestPackage.Path, copy);
            var before = Bytes.Sha256(copy);
            var regions = PackageRegions.Load(copy);

            // Simulate a crash: journal written, CNT half-written, SI never reached.
            RepairJournal.Write(journal, copy, regions, new Progress(TextWriter.Null, false, false, 1));
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write))
            {
                fs.Position = regions.CntOffset;
                fs.Write(new byte[1_000_000]);
            }
            Assert.NotEqual(before, Bytes.Sha256(copy));

            Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", copy, "--in-place"]));

            Assert.Equal(before, Bytes.Sha256(copy));
            Assert.False(File.Exists(journal), "the journal must be removed after a successful restore");
        }
        finally { File.Delete(copy); File.Delete(journal); }
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter AnInterruptedRepair`
Expected: FAIL — the run repairs the corrupted file instead of restoring it.

- [ ] **Step 3: Implement `RecoverIfNeeded` and call it at the top of `Repair`, before every guard**

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter AnInterruptedRepair`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/RepairPlayGoCommand.cs fpkg-cli.Tests/InPlaceWriterTests.cs
git commit -m "feat: restore an interrupted in-place repair from its journal"
```

---

### Task 5: `--temp-dir`, `--verbose`, and the progress wiring

**Files:**
- Modify: `fpkg-cli/RepairPlayGo/RepairPlayGoCommand.cs`, `fpkg-cli/RepairPlayGo/PackageRegions.cs`, `fpkg-cli/RepairPlayGo/CntReseal.cs`, `fpkg-cli/Program.cs` (usage)
- Test: `fpkg-cli.Tests/RepairPlayGoCommandTests.cs` (add)

**Interfaces:**
- `PackageRegions.WriteTo(string outputPath, byte[] cnt, byte[] si, bool flushToDisk = true, Progress? progress = null)`
- `CntReseal.Seal(byte[] cnt, IReadOnlyList<CntEntry> physical, string contentId, string passcode, Progress? progress = null)`

`--temp-dir <path>` relocates the journal (`--in-place`) and the staging file (`--out`). Create it if absent. **When it resolves to a different device than an `--out` target, warn**: `File.Move` stops being a rename and becomes a non-atomic copy, so the staging file's whole purpose is weakened. Detect by comparing `new DriveInfo(...).Name`, or on Unix by `stat` device id via `File.GetAttributes`-adjacent means; if the platform makes this unreliable, warn unconditionally when `--temp-dir` is given with `--out` and say the check is best-effort. Do not refuse — the user asked for it.

**The stages, in order** (`Progress` is constructed with the right count for the mode):

| # | stage | percentage source |
|---|---|---|
| 1 | reading package | bytes of CNT+SI read |
| 2 | recovering PlayGo values | — (`Detail` dumps the recovered record under `--verbose`) |
| 3 | generating replacement entries | — (`Detail` lists the three sizes) |
| 4 | resealing the CNT | body digest bytes |
| 5 | writing the journal *(in-place only)* | journal bytes |
| 6 | writing the CNT *(in-place)* / staging pass 1 *(--out)* | bytes written |
| 7 | computing the mount CRC table | `BuildChunkCrc`'s own logger |
| 8 | writing the SI *(in-place)* / staging pass 2 *(--out)* | bytes written |

**Fix the long silent prelude:** print `Package:` and start stage 1 *before* `PlayGoInitialChunkProblem`, `PackageRegions.Load` and `CntEntryTable.Parse` — today all three run before any output, which is the gap the user reported.

- [ ] **Step 1: Write the failing test**

```csharp
    [SkippableFact]
    public void TheDryRunAnnouncesItselfBeforeTheSlowWork()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var sw = new StringWriter();
        var old = Console.Out;
        try { Console.SetOut(sw); RepairPlayGoCommand.Run(["repair-playgo", TestPackage.Path]); }
        finally { Console.SetOut(old); }

        string s = sw.ToString();
        int pkg = s.IndexOf("Package:", StringComparison.Ordinal);
        int problem = s.IndexOf("Problem:", StringComparison.Ordinal);
        Assert.True(pkg >= 0 && problem > pkg, "Package: must be printed before Problem:");
        Assert.Contains("reading package", s);
    }

    [SkippableFact]
    public void VerboseAddsTheRecoveredValuesAndQuietDoesNot()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        Assert.DoesNotContain("chunkLanguageMasks", CaptureRun(["repair-playgo", TestPackage.Path]));
        Assert.Contains("chunkLanguageMasks", CaptureRun(["repair-playgo", TestPackage.Path, "--verbose"]));
    }

    [Fact]
    public void TempDirIsCreatedWhenMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.NotEqual(0, RepairPlayGoCommand.Run(
                ["repair-playgo", "nonexistent.pkg", "--temp-dir", dir]));   // fails on the package, not the dir
            Assert.True(Directory.Exists(dir) || !File.Exists("nonexistent.pkg"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
```

Add a `CaptureRun(string[] args)` helper to that test class that redirects `Console.Out`, runs, restores, and returns the text.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter RepairPlayGoCommand`
Expected: FAIL — no `reading package` line, no `--verbose`, no `--temp-dir`.

- [ ] **Step 3: Thread `Progress` through `Repair`, `WriteTo`, `Seal` and `SiRepair.Rebuild`; add the two flags and the usage text**

Add to `Program.Usage()` beside the existing `repair-playgo` line:

```
                          --temp-dir <dir>  where the journal (--in-place) or the staging
                                            file (--out) is written; defaults to beside the
                                            target. A different device makes --out's final
                                            rename a non-atomic copy.
                          --verbose         recovered PlayGo values, per-entry digest work
                                            and the SI member list
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter RepairPlayGoCommand`
Expected: all pass.

- [ ] **Step 5: Run the command for real and read the output**

```bash
dotnet build fpkg-cli/fpkg.csproj -c Release -o fpkg-tools/bin
./fpkg repair-playgo Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg --verbose
```

Expected: `Package:` appears immediately, stages are numbered, no stage runs silently for more than a second or two without a percentage, and `--verbose` adds the recovered values.

- [ ] **Step 6: Commit**

```bash
git add fpkg-cli/RepairPlayGo fpkg-cli/Program.cs fpkg-cli.Tests/RepairPlayGoCommandTests.cs
git commit -m "feat: add --temp-dir and --verbose, and report progress per stage"
```

---

### Task 6: Route `--in-place` through the journalled writer

**Files:**
- Modify: `fpkg-cli/RepairPlayGo/RepairPlayGoCommand.cs`
- Test: `fpkg-cli.Tests/AcceptanceTests.cs` (add)

`--in-place` calls `InPlaceWriter.Write`; `--out` keeps `WriteRepaired`'s stage-and-rename. Both still run every guard first. `--in-place` no longer creates a `.repair-playgo.tmp` at all.

- [ ] **Step 1: Write the failing test — the two modes must agree byte for byte**

```csharp
    /// <summary>
    /// --in-place and --out take different write paths now. They must still produce identical
    /// bytes, and both must equal the oracle. If this ever fails, the two paths have diverged
    /// and one of them is wrong — do not adjust this test.
    /// </summary>
    [SkippableFact]
    public void InPlaceAndOutProduceIdenticalBytes()
    {
        RequireArtifacts();
        var viaOut = FreshOutPath();
        var copy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", TestPackage.Path, "--out", viaOut]));
            File.Copy(TestPackage.Path, copy);
            Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", copy, "--in-place"]));

            Assert.Equal(Bytes.Sha256(viaOut), Bytes.Sha256(copy));
            Assert.Equal(Bytes.Sha256(Oracle.Path), Bytes.Sha256(copy));
            Assert.False(File.Exists(copy + ".repair-playgo.tmp"));
            Assert.False(File.Exists(copy + RepairJournal.Suffix));
        }
        finally { File.Delete(viaOut); File.Delete(copy); }
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter InPlaceAndOut`
Expected: FAIL — `--in-place` still stages, so the `.tmp` assertion fails.

- [ ] **Step 3: Route `--in-place` to `InPlaceWriter.Write`**

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj`
Expected: everything passes, including the pre-existing acceptance gate.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/RepairPlayGoCommand.cs fpkg-cli.Tests/AcceptanceTests.cs
git commit -m "feat: --in-place now writes in place, journalled"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/repair-playgo-spec.md`, `CHANGELOG.md`, `dist/README.md`

- [ ] **Step 1: Document the write model in the spec**

A new subsection covering: the two invariants that make in-place possible (payload never changes; CNT length fixed by two guards); the five-step sequence; the journal's layout and why its identity hashes the FIH block rather than the CNT; the recovery behaviour; and the measured cost — **~64 MB written and no spare disk needed, against ~1.32 GB and 2× disk for `--out`**.

- [ ] **Step 2: Update `CHANGELOG.md` and `dist/README.md`**

Cover `--in-place` being genuinely in-place and recoverable, `--temp-dir` with its cross-device caveat for `--out`, `--verbose`, and the staged progress output.

- [ ] **Step 3: Commit**

```bash
git add docs/repair-playgo-spec.md CHANGELOG.md dist/README.md
git commit -m "docs: document journalled in-place writes, --temp-dir and --verbose"
```

---

## Notes for whoever executes this

- **Task 3's invariant assert is load-bearing.** If `repaired.Cnt.Length != regions.Cnt.Length`, in-place writing is unsound and the throw is the only correct response. Do not relax it to a resize.
- **The journal is written before the package is opened for writing, and flushed to disk before any mutation.** If a change makes that ordering ambiguous, the design is broken.
- The acceptance gate from v1 must stay green throughout. Run the full suite, not just the filtered tests, before each commit.
- `./fpkg` execs the prebuilt `fpkg-tools/bin/fpkg.dll`. To exercise your changes through the real CLI you must build with `-c Release -o fpkg-tools/bin` first, or you will test a stale binary.
