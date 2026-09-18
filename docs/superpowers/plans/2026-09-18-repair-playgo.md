# `fpkg repair-playgo` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite a 0.6.8 package's PlayGo metadata in place, in seconds, to the exact bytes LibProsperoPkg 0.6.9 would have produced — without recompressing or copying the payload.

**Architecture:** The package is split into outer-PFS / CNT / SI with `ProsperoPackageArchive.Split`. Only the CNT region (~63 MB) and the SI zip (~535 KB) are touched; the outer PFS is copied through byte-for-byte. Three CNT entries are regenerated, the body is relaid out, and the whole CNT digest chain is resealed using **public** `ProsperoImageDigests` / `ProsperoPublisherRsa` primitives — no reflection into private members, and no `Header` struct is ever built (the original header bytes are patched field by field at known offsets). The SI zip is rebuilt from its own members with a new `playgo-chunk.dat` and a recomputed CRC table.

**Tech Stack:** C# / .NET 10, `LibProsperoPkg` 0.6.9 (referenced, not modified), xunit + `Xunit.SkippableFact`.

**Spec:** `docs/repair-playgo-spec.md` — read it before Task 1 and keep it open. It was corrected on 2026-09-18 against the 0.6.9 decompile; the "Reseal scope", "Implementation route", "Padding alternative" and "Acceptance" sections are the corrected ones and are authoritative over anything remembered from an earlier reading.

---

## Global Constraints

- **Two fixed points gate the feature. Both must pass. Nothing ships without both.**
  1. Reseal the test package with **no entry changed** → output byte-identical to input.
  2. Repair the test package → output byte-identical to the 0.6.9 rebuild (the oracle).
  `ValidateLayout`, `fpkg verify --full` and the disappearance of the `PlayGoInitialChunkProblem`
  warning are fast feedback during development. They are **not** the gate.
- **`~/Developer/LibProsperoPKG` is a stale reference tree. The linked DLL is the only authority.**
  That checkout is the Aug 2026 upstream and predates the release this CLI loads. Use it to
  understand *how* something is formed — the shape of a digest, the order of a loop. Never take a
  **constant, a signature, or a set of ids** from it. Those come from the 0.6.9 decompile
  (`L69.cs`) or from the DLL itself via `fpkg api`.

  This has already bitten once: upstream has `PlaygoIds = [0x1001, 0x2010, 0x2011]`, the shipped
  0.6.9 has `[0x1001, 0x2010, 0x2011, 0x3000]`. Settled by computing both candidate digests and
  comparing against the stored `GENERAL_DIGESTS.PlaygoDigest` in the original *and* the oracle —
  that is the shape of check to reach for, not "which file reads better".

  `SystemMediaIds` is identical in both trees. That was luck, not verification; it does not license
  trusting the next constant.

  Practical consequence: all three changed entries feed `PlaygoDigest`. There is no shortcut in
  which the scenario JSON is digest-neutral. The reseal runs in full whichever entries move.
- **No reflection into private members.** The only reflection in the feature is over
  `ProsperoPlayGo.BuildLanguageChunkLayout`, which is public but returns the internal nested record
  `LanguageChunkLayout`. Everything else is called directly.
- **Refuse, never degrade.** Five refusal conditions, each a hard error with an explanatory message:
  a passcode that does not match the package; `pfsimage.xml` present in the SI; entry growth
  exceeding the body slack; a `playgo-scenario.json` carrying presentation that regeneration would
  destroy; a package that is not the PS5 publisher profile (`EntryKeys.Length != 2944`).
- **`--dry-run` is the default.** Writing requires `--out <path>` or `--in-place`.
- All CNT header scalars are **big-endian**.
- `Passcode` in the test code below means `new string('0', 32)` — *this package's* passcode,
  not a constant of the format. Every API that decrypts or re-encrypts takes it as a required
  parameter with no default.
- Test package: `Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg`, 661,006,510 bytes, in the repo
  root. Every test that needs it uses `SkippableFact` so the suite still runs without it.
- Build the CLI with `dotnet build fpkg-cli/fpkg.csproj`; run tests with
  `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj`.
- Commit after every task. Conventional-commit subjects (`feat:`, `test:`, `fix:`, `docs:`).

### Reference values for the test package (all measured, all reusable as test constants)

All of these were read back off the package, not computed by hand — `0x3CA0000` is 63,569,920, and
an earlier draft of this table had 63,700,992 (which is `0x3CC0000`). Trust the table, and if you
recompute, recompute in a tool.

```
file size            661,006,510
outer PFS            offset 0x10000      size 596,705,280
CNT region           offset 596,770,816  size 63,569,920 (0x3CA0000)
SI (sce_suppl zip)   offset 660,340,736  size 665,774
body_offset 0x2000   body_size 0x3C9E000 → body end 0x3CA0000 = the CNT region size exactly
last entry (12288) ends at 0x3C9517D     → slack 44,675 bytes
sc_entry_count 6     main_ent_data_size 2944+2048+480+864+864 = 7200
content id           EP4060-PPSA25872_00-T2DNFMAINGAMEPS5
```

**`Cnt.Length == body_offset + body_size`.** The CNT region carries no padding past the body. That
coincidence is load-bearing for the CRC length rule in Task 10, so it is asserted there rather than
assumed.

SI members (there is no `pfsimage.xml`, which is what makes this package repairable):

```
common/etc/naps_meta_18.dat                                        617,536
common/etc/naps_meta_{300,301,302,308}.dat                              48 each
common/etc/playgo-chunk.dat                                          6,736  ← shrinks to 5,376
config/EP4060-PPSA25872_00-T2DNFMAINGAMEPS5/playgo-chunk.crc        40,304  = 10,076 CRC entries
```

CNT entries, **physical** order (this is `pkg.Entries` order and it is *not* id order):

| # | id | off | size | enc | name |
|---|---|---|---|---|---|
| 0 | 16 | 8192 | 2944 | | ENTRY_KEYS |
| 1 | 32 | 11136 | 2048 | | IMAGE_KEY |
| 2 | 128 | 13184 | 480 | | GENERAL_DIGESTS |
| 3 | 256 | 13664 | 864 | | METAS |
| 4 | 1 | 14528 | 864 | | DIGESTS |
| 5 | 512 | 15392 | 231 | | ENTRY_NAMES |
| 6 | 8192 | 15632 | 2543 | | param.json |
| 7 | 1024 | 18176 | 1024 | ✓ | |
| 8 | 1025 | 19200 | 512 | ✓ | |
| 9 | 1026 | 19712 | 160 | ✓ | |
| 10 | 8224 | 19872 | 532 | ✓ | uds/npbind.dat |
| 11 | 8225 | 20416 | 532 | ✓ | trophy2/npbind.dat |
| 12 | 8256 | 20960 | 83666 | | pic2.png |
| 13 | 8288 | 104640 | 8294548 | | pic2.dds |
| 14 | 1034 | 8399200 | 291360 | | IMAGEDIGS_DAT |
| 15 | 4097 | 8690560 | **6736** | | playgo-chunk.dat |
| 16 | 4102 | 8697296 | 15698446 | | pic1.png |
| 17 | 4608 | 24395744 | 508216 | | icon0.png |
| 18 | 4640 | 24903968 | 13303564 | | pic0.png |
| 19 | 4736 | 38207536 | 262292 | | icon0.dds |
| 20 | 4768 | 38469840 | 8294548 | | pic0.dds |
| 21 | 4800 | 46764400 | 8294548 | | pic1.dds |
| 22 | 5248 | 55058960 | 8432496 | | trophy2/trophy00.ucp |
| 23 | 5280 | 63491456 | 31504 | | uds/uds00.ucp |
| 24 | 8208 | 63522960 | 240 | | playgo-hash-table.dat |
| 25 | 8209 | 63523200 | **62** | | playgo-ficm.dat |
| 26 | 12288 | 63523264 | **1981** | | playgo-scenario.json |

Target sizes after repair: 4097 → 5376 (**−1360**), 8209 → 62 (unchanged), 12288 → 2293 (**+312**).

**Net body delta: −1048.** The body *shrinks*. 4097 sits early (offset 8,690,560) and 12288 is
last, so the relayout shifts everything after 4097 down by 1360 and then the final entry grows 312;
slack goes 44,675 → 45,723. The spec's "+312 fits 140×" is about the one entry that grows, not the
net. The slack guard therefore never fires on this package — it exists for a hypothetical one, and
Task 9 tests it against a synthesised condition.

Expected file-size arithmetic for gate 2: original 661,006,510 → oracle 661,005,150, delta
**1,360**, which is entirely the SI's *embedded* copy of `playgo-chunk.dat` shrinking. The CNT
region stays exactly 0x3CA0000 (`body_size` is unchanged because the body only shrank within its
existing 64 KiB rounding) and the outer PFS is untouched. **If the repaired file's size is right but
its bytes are not, the fault is in the CNT; if the size itself is wrong, the fault is in the SI.**

---

## File Structure

New, all under `fpkg-cli/RepairPlayGo/`, namespace `Fpkg.Cli.RepairPlayGo`:

| file | responsibility |
|---|---|
| `PackageRegions.cs` | split a .pkg into outer-PFS range / CNT bytes / SI bytes, and splice them back |
| `CntHeader.cs` | named big-endian field offsets into the CNT header, with typed get/set |
| `CntEntryTable.cs` | the entry list: parse the meta table + payloads, relay out, write back |
| `CntReseal.cs` | the eleven-step digest chain from the spec's "Reseal scope" |
| `PlayGoRecovery.cs` | recover chunk count / masks / extents / labels from the original `playgo-chunk.dat` |
| `PlayGoEntries.cs` | build the three replacement entries |
| `SiRepair.cs` | rebuild the SI zip with the new chunk.dat and a recomputed CRC table |
| `RepairPlayGoCommand.cs` | flag parsing, guards, dry-run report, orchestration |

Modified:

| file | change |
|---|---|
| `fpkg-cli/Program.cs:43` | add `"repair-playgo" => RepairPlayGoCommand.Run(args)` to the dispatch |
| `fpkg-cli/Program.cs:77` | add the usage block |
| `CHANGELOG.md` | new entry |

New test project `fpkg-cli.Tests/` (xunit, mirroring `PprPfsKrakenTool.Tests`):

| file | responsibility |
|---|---|
| `fpkg.Tests.csproj` | project file |
| `TestPackage.cs` | locates the .pkg, skips when absent |
| `Bytes.cs` | shared assertion helpers: `AssertEqual`, `Sha256`, `Sha256Range` |
| `Oracle.cs` | builds (and caches) the 0.6.9 rebuild that gate 2 compares against |
| `CntHeaderTests.cs`, `CntEntryTableTests.cs`, `CntResealTests.cs`, `PlayGoRecoveryTests.cs`, `PlayGoEntriesTests.cs`, `SiRepairTests.cs`, `AcceptanceTests.cs` | one per unit, plus the two gates |

---

### Task 1: Test project and the test-package locator

**Files:**
- Create: `fpkg-cli.Tests/fpkg.Tests.csproj`
- Create: `fpkg-cli.Tests/TestPackage.cs`
- Test: `fpkg-cli.Tests/TestPackageTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal static class TestPackage` with
  `static string Path` (absolute path to the .pkg; throws if absent),
  `static bool Exists`,
  `static string RepoRoot` (the folder holding `LibProsperoPkg.dll`),
  `const string ContentId = "PPSA25872-...."` — **do not hard-code it blind**, read it in Step 3.

- [ ] **Step 1: Create the project file**

`fpkg-cli.Tests/fpkg.Tests.csproj` — note `LibDir` and the `fpkg` project reference; the CLI
project reads `../VERSION` and `../LibProsperoPkg.xml`, both of which resolve from the repo root:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);NU1701</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Xunit.SkippableFact" Version="1.4.13" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../fpkg-cli/fpkg.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing test**

`fpkg-cli.Tests/TestPackageTests.cs`:

```csharp
using Xunit;

public class TestPackageTests
{
    [SkippableFact]
    public void LocatesTheTestPackage()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        Assert.EndsWith(".pkg", TestPackage.Path);
        Assert.Equal(661_006_510, new FileInfo(TestPackage.Path).Length);
    }

    [Fact]
    public void FindsTheReleaseFolder() =>
        Assert.True(File.Exists(Path.Combine(TestPackage.RepoRoot, "LibProsperoPkg.dll")));
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj`
Expected: FAIL — `TestPackage` does not exist (CS0103).

- [ ] **Step 4: Implement**

`fpkg-cli.Tests/TestPackage.cs`:

```csharp
internal static class TestPackage
{
    internal const string FileName = "Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg";

    /// <summary>Walks up from the test binary looking for the release folder.</summary>
    internal static string RepoRoot { get; } = Find();

    internal static string Path => System.IO.Path.Combine(RepoRoot, FileName);

    internal static bool Exists => File.Exists(Path);

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "LibProsperoPkg.dll")))
                return dir.FullName;
        throw new InvalidOperationException("LibProsperoPkg.dll not found above " + AppContext.BaseDirectory);
    }
}
```

- [ ] **Step 5: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj`
Expected: 2 passed.

- [ ] **Step 6: Read the content id and record it**

Run: `./fpkg info Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg | head -20` and note the content id
the package reports. Add it to `TestPackage` as `internal const string ContentId = "…";` and assert
it in a third test that reads it back via `ProsperoPackageArchive.Inspect`. It is needed by the SI
member path `config/<contentId>/playgo-chunk.crc`.

- [ ] **Step 7: Add the shared assertion helpers**

Four later test files need these. `Bytes.AssertEqual` reports the first differing offset, which is
the difference between a diagnosable byte-compare failure and an opaque one.

`fpkg-cli.Tests/Bytes.cs`:

```csharp
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
```

- [ ] **Step 8: Commit**

```bash
git add fpkg-cli.Tests
git commit -m "test: add fpkg-cli test project, test-package locator and byte helpers"
```

---

### Task 2: Split and splice the package

**Files:**
- Create: `fpkg-cli/RepairPlayGo/PackageRegions.cs`
- Test: `fpkg-cli.Tests/PackageRegionsTests.cs`

**Interfaces:**
- Consumes: `TestPackage`.
- Produces:
  ```csharp
  namespace Fpkg.Cli.RepairPlayGo;

  internal sealed record PackageRegions(
      string SourcePath,
      long OuterPfsOffset,   // 0x10000
      long OuterPfsSize,
      long CntOffset,        // = OuterPfsOffset + OuterPfsSize
      byte[] Cnt,
      byte[] Si)             // empty array when the package has no SI segment
  {
      internal static PackageRegions Load(string packagePath);
      internal void WriteTo(string outputPath, byte[] cnt, byte[] si);
  }
  ```
  `WriteTo` streams `[0, CntOffset)` from the source verbatim, then `cnt`, then `si`. It never
  materialises the payload.

- [ ] **Step 1: Write the failing test**

```csharp
public class PackageRegionsTests
{
    [SkippableFact]
    public void SplitsTheKnownRegions()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        Assert.Equal(0x10000, r.OuterPfsOffset);
        Assert.Equal(596_705_280, r.OuterPfsSize);
        Assert.Equal(596_770_816, r.CntOffset);
        Assert.Equal(63_569_920, r.Cnt.Length);
        Assert.Equal(665_774, r.Si.Length);
        // No padding past the body — Task 10's CRC length rule depends on this.
        Assert.Equal((long)r.Cnt.Length,
            (long)CntHeader.U64(r.Cnt, CntHeader.BodyOffset) + (long)CntHeader.U64(r.Cnt, CntHeader.BodySize));
    }

    [SkippableFact]
    public void SplicingBackTheOriginalRegionsReproducesTheFile()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            r.WriteTo(tmp, r.Cnt, r.Si);
            Assert.Equal(Bytes.Sha256(TestPackage.Path), Bytes.Sha256(tmp));
        }
        finally { File.Delete(tmp); }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter PackageRegions`
Expected: FAIL — `PackageRegions` does not exist.

- [ ] **Step 3: Implement**

Use `ProsperoPackageArchive.Split(Stream input, Stream outerPfs, Stream cnt, Stream supplement)`
to discover the boundaries, but do **not** keep the outer PFS in memory — pass `Stream.Null` for it
and take its length from the FIH (`ProsperoPkgReader.Read(path).Fih.EmbeddedCntOffset` is
`OuterPfsOffset + OuterPfsSize`). Concretely:

```csharp
internal static PackageRegions Load(string packagePath)
{
    var pkg = ProsperoPkgReader.Read(packagePath);
    long cntOffset = pkg.Fih.EmbeddedCntOffset;

    using var input = File.OpenRead(packagePath);
    using var cnt = new MemoryStream();
    using var si  = new MemoryStream();
    ProsperoPackageArchive.Split(input, Stream.Null, cnt, si);

    return new PackageRegions(
        packagePath,
        OuterPfsOffset: 0x10000,
        OuterPfsSize:   cntOffset - 0x10000,
        CntOffset:      cntOffset,
        Cnt:            cnt.ToArray(),
        Si:             si.ToArray());
}
```

`WriteTo`:

```csharp
internal void WriteTo(string outputPath, byte[] cnt, byte[] si)
{
    using var src = File.OpenRead(SourcePath);
    using var dst = File.Create(outputPath);
    // Header, FIH and the whole outer PFS, byte for byte. Never decoded, never rewritten.
    var head = new byte[81920];
    long remaining = CntOffset;
    while (remaining > 0)
    {
        int n = src.Read(head, 0, (int)Math.Min(head.Length, remaining));
        if (n <= 0) throw new EndOfStreamException("package truncated before the CNT region");
        dst.Write(head, 0, n);
        remaining -= n;
    }
    dst.Write(cnt);
    dst.Write(si);
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter PackageRegions`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/PackageRegions.cs fpkg-cli.Tests/PackageRegionsTests.cs
git commit -m "feat: split a package into outer-PFS, CNT and SI regions"
```

---

### Task 3: CNT header field accessors

**Files:**
- Create: `fpkg-cli/RepairPlayGo/CntHeader.cs`
- Test: `fpkg-cli.Tests/CntHeaderTests.cs`

**Interfaces:**
- Consumes: `PackageRegions`.
- Produces: `internal static class CntHeader` — offsets and typed accessors over the CNT byte array.

```csharp
internal static class CntHeader
{
    internal const int EntryCount         = 16;   // u32
    internal const int ScEntryCount       = 20;   // u16
    internal const int EntryCount2        = 22;   // u16
    internal const int EntryTableOffset   = 24;   // u32
    internal const int MainEntDataSize    = 28;   // u32
    internal const int BodyOffset         = 32;   // u64
    internal const int BodySize           = 40;   // u64
    internal const int MandatorySize      = 48;   // u64
    internal const int ContentId          = 64;   // 48-byte ASCII slot
    internal const int ContentType        = 116;  // u32
    internal const int ScEntries1Hash     = 256;  // 32 B
    internal const int ScEntries2Hash     = 288;  // 32 B
    internal const int DigestTableHash    = 320;  // 32 B
    internal const int BodyDigest         = 352;  // 32 B
    internal const int MountDescriptor    = 1024; // 128 B  (the ForceFihRelativeImageOffset input)
    internal const int PfsImageOffset     = 1040; // u64    (= MountDescriptor + 0x10)
    internal const int CntRegionOffset    = 1200; // u64
    internal const int CntRegionSize      = 1208; // u64
    internal const int DescImageKeyOffset = 1296; // u32
    internal const int DescImageKeySize   = 1300; // u32
    internal const int DescMandatoryOffset= 1304; // u32
    internal const int DescMandatorySize  = 1308; // u32
    internal const int DescDigest         = 1312; // 64 B
    internal const int PackageDigest      = 4064; // 32 B
    internal const int HeaderWrap         = 4096;

    internal static ushort U16(ReadOnlySpan<byte> cnt, int off) => BinaryPrimitives.ReadUInt16BigEndian(cnt[off..]);
    internal static uint   U32(ReadOnlySpan<byte> cnt, int off) => BinaryPrimitives.ReadUInt32BigEndian(cnt[off..]);
    internal static ulong  U64(ReadOnlySpan<byte> cnt, int off) => BinaryPrimitives.ReadUInt64BigEndian(cnt[off..]);
    internal static void SetU32(Span<byte> cnt, int off, uint v)  => BinaryPrimitives.WriteUInt32BigEndian(cnt[off..], v);
    internal static void SetU64(Span<byte> cnt, int off, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(cnt[off..], v);
    internal static void SetBytes(Span<byte> cnt, int off, ReadOnlySpan<byte> v) => v.CopyTo(cnt[off..]);
}
```

- [ ] **Step 1: Write the failing test**

Every offset is cross-checked against a value the library reports independently, so a wrong offset
cannot slip through:

```csharp
public class CntHeaderTests
{
    [SkippableFact]
    public void OffsetsAgreeWithTheLibrarysOwnReader()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var hdr = ProsperoPkgReader.Read(TestPackage.Path).Header;

        Assert.Equal(hdr.EntryCount,       CntHeader.U32(cnt, CntHeader.EntryCount));
        Assert.Equal(hdr.ScEntryCount,     CntHeader.U16(cnt, CntHeader.ScEntryCount));
        Assert.Equal(hdr.EntryTableOffset, CntHeader.U32(cnt, CntHeader.EntryTableOffset));
        Assert.Equal(hdr.BodyOffset,       CntHeader.U64(cnt, CntHeader.BodyOffset));
        Assert.Equal(hdr.BodySize,         CntHeader.U64(cnt, CntHeader.BodySize));
        Assert.Equal(hdr.ContentType,      CntHeader.U32(cnt, CntHeader.ContentType));
        Assert.Equal(hdr.ContentId,
            Encoding.ASCII.GetString(cnt, CntHeader.ContentId, 36).TrimEnd('\0'));
    }

    [SkippableFact]
    public void KnownConstantsForTheTestPackage()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        Assert.Equal(27u,        CntHeader.U32(cnt, CntHeader.EntryCount));
        Assert.Equal((ushort)6,  CntHeader.U16(cnt, CntHeader.ScEntryCount));
        Assert.Equal(0x2000ul,   CntHeader.U64(cnt, CntHeader.BodyOffset));
        Assert.Equal(0x3C9E000ul,CntHeader.U64(cnt, CntHeader.BodySize));
        Assert.Equal(7200u,      CntHeader.U32(cnt, CntHeader.MainEntDataSize));
        // IMAGEDIGS_DAT (1034) sits at 8,399,200 and IMAGE_KEY (32) at 11,136.
        Assert.Equal(8_399_200u, CntHeader.U32(cnt, CntHeader.DescMandatoryOffset));
        Assert.Equal(291_360u,   CntHeader.U32(cnt, CntHeader.DescMandatorySize));
        Assert.Equal(11_136u,    CntHeader.U32(cnt, CntHeader.DescImageKeyOffset));
        Assert.Equal(2048u,      CntHeader.U32(cnt, CntHeader.DescImageKeySize));
        Assert.Equal(8_399_200ul,CntHeader.U64(cnt, CntHeader.MandatorySize));
    }

    [SkippableFact]
    public void ThePackageDigestAtOffset4064IsWhatTheLibraryComputes()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var preimage = cnt[..4064];
        BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(CntHeader.PfsImageOffset, 8), 65536ul);
        Assert.Equal(
            Convert.ToHexString(ProsperoImageDigests.ComputePackageDigest(preimage)),
            Convert.ToHexString(cnt.AsSpan(CntHeader.PackageDigest, 32)));
    }
}
```

The third test is the one that proves the `[1040..1048) = 65536` force is real. If it fails without
the force and passes with it, the spec's step 10 is confirmed on this package.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter CntHeader`
Expected: FAIL — `CntHeader` does not exist.

- [ ] **Step 3: Implement `CntHeader.cs` exactly as given in the Interfaces block above**

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter CntHeader`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/CntHeader.cs fpkg-cli.Tests/CntHeaderTests.cs
git commit -m "feat: named big-endian accessors for the CNT header fields"
```

---

### Task 4: The entry table — parse and write back

**Files:**
- Create: `fpkg-cli/RepairPlayGo/CntEntryTable.cs`
- Test: `fpkg-cli.Tests/CntEntryTableTests.cs`

**Interfaces:**
- Consumes: `CntHeader`, `PackageRegions`.
- Produces:
  ```csharp
  internal sealed class CntEntry
  {
      internal uint Id;
      internal uint NameTableOffset;
      internal uint Flags1;
      internal uint Flags2;
      internal uint DataOffset;
      internal uint DataSize;
      /// <summary>Plaintext. For an encrypted entry this is the decrypted form.</summary>
      internal byte[] Payload = [];
      /// <summary>The bytes as they sit in the CNT — Align(DataSize,16) long when encrypted.</summary>
      internal byte[] StoredPayload = [];
      internal bool Encrypted => (Flags1 & 0x80000000u) != 0;
      internal string? Name;                  // null when NameTableOffset == 0
      /// <summary>The 32-byte meta record, needed as the AES key/IV seed for encrypted entries.</summary>
      internal byte[] MetaBytes();
  }

  internal sealed class CntEntryTable
  {
      /// <summary>Entries in PHYSICAL (pkg.Entries) order — ascending DataOffset.</summary>
      internal IReadOnlyList<CntEntry> Physical { get; }
      /// <summary>The same entries sorted ascending by id — the order of the on-disk meta table.</summary>
      internal IReadOnlyList<CntEntry> ById { get; }
      /// <summary>
      /// The passcode is a REQUIRED parameter with no default. A wrong passcode does not
      /// fail loudly — it silently decrypts the five protected entries to garbage, and a
      /// later reseal would then re-encrypt that garbage and compute perfectly valid
      /// digests over it. Defaulting this would reintroduce, one layer lower, exactly the
      /// failure Task 11's CheckPasscode guard exists to prevent.
      /// contentId is NOT a parameter: it is read from the CNT header's own ASCII slot,
      /// which is the value the builder itself used to derive these keys.
      /// </summary>
      internal static CntEntryTable Parse(byte[] cnt, string passcode);
      internal CntEntry this[uint id] { get; }
  }
  ```

**Why physical order matters:** `main_ent_data_size` sums the first `sc_entry_count - 1` entries in
`pkg.Entries` order, and `LayOutEntries` places entries in that order. The on-disk meta table is
sorted by id. Both orders are needed and they differ in this package.

`Physical` is recovered by sorting the parsed meta records by `DataOffset`. That is exactly the
order `LayOutEntries` produced, because it assigned offsets by walking `pkg.Entries` and advancing
monotonically.

- [ ] **Step 1: Write the failing test**

```csharp
public class CntEntryTableTests
{
    [SkippableFact]
    public void ParsesTheKnownEntryTable()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);

        Assert.Equal(27, t.Physical.Count);
        Assert.Equal([16u, 32u, 128u, 256u, 1u, 512u, 8192u], t.Physical.Take(7).Select(e => e.Id));
        Assert.Equal(6736u, t[4097].DataSize);
        Assert.Equal(62u,   t[8209].DataSize);
        Assert.Equal(1981u, t[12288].DataSize);
        Assert.Equal("playgo-chunk.dat", t[4097].Name);
        Assert.True(t[1024].Encrypted);
        Assert.False(t[12288].Encrypted);
        // The five that feed main_ent_data_size, in physical order.
        Assert.Equal(7200u, (uint)t.Physical.Take(5).Sum(e => (long)e.DataSize));
    }

    [SkippableFact]
    public void PayloadsRoundTripAgainstTheLibrarysOwnReader()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var passcode = new string('0', 32);
        foreach (uint id in new uint[] { 4097, 8208, 8209, 12288, 8192 })
        {
            var expected = ProsperoPackageArchive.TryReadCntEntry(TestPackage.Path, passcode, id);
            Assert.Equal(Convert.ToHexString(expected!), Convert.ToHexString(t[id].Payload));
        }
    }

    [SkippableFact]
    public void MetaRecordsRoundTripThroughTheLibrarysCodec()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var t = CntEntryTable.Parse(cnt, Passcode);
        int table = (int)CntHeader.U32(cnt, CntHeader.EntryTableOffset);
        foreach (var (e, i) in t.ById.Select((e, i) => (e, i)))
        {
            // MetaEntry.Write leaves the trailing 8 bytes of each 32-byte record untouched,
            // so compare only the 24 bytes it does write.
            Assert.Equal(
                Convert.ToHexString(cnt.AsSpan(table + i * 32, 24)),
                Convert.ToHexString(e.MetaBytes().AsSpan(0, 24)));
        }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter CntEntryTable`
Expected: FAIL — `CntEntryTable` does not exist.

- [ ] **Step 3: Implement**

Parse the meta table at `entry_table_offset`, `entry_count` records of 32 bytes, all big-endian:
`id` u32 @0, `NameTableOffset` u32 @4, `Flags1` u32 @8, `Flags2` u32 @12, `DataOffset` u32 @16,
`DataSize` u32 @20, then 8 unused bytes.

Names come from the ENTRY_NAMES (512) payload: a NUL-separated blob indexed by `NameTableOffset`.
Read from `NameTableOffset` to the next NUL. `NameTableOffset == 0` means no name.

Payloads: for a plain entry, `cnt[DataOffset .. DataOffset+DataSize]`. For an encrypted entry, the
stored bytes are `Align(DataSize, 16)` long; keep the **plaintext** in `Payload` (decrypt with
`Entry.Decrypt(stored, contentId, passcode, meta, publisherProfile: true)`) so that re-encryption at
a new offset is possible. Store the raw stored bytes too, in `internal byte[] StoredPayload`, so a
no-op reseal can prove it round-trips.

`MetaBytes()` builds the 32-byte record with a `MetaEntry` and its `GetBytes()`, so the library owns
that layout:

```csharp
internal byte[] MetaBytes()
{
    var m = new MetaEntry
    {
        id = (EntryId)Id, NameTableOffset = NameTableOffset,
        Flags1 = Flags1, Flags2 = Flags2, DataOffset = DataOffset, DataSize = DataSize,
    };
    return m.GetBytes();
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter CntEntryTable`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/CntEntryTable.cs fpkg-cli.Tests/CntEntryTableTests.cs
git commit -m "feat: parse the CNT entry table in both physical and id order"
```

---

### Task 5: The reseal — and the first gate

This is the load-bearing task. It ends with fixed point 1.

**Files:**
- Create: `fpkg-cli/RepairPlayGo/CntReseal.cs`
- Test: `fpkg-cli.Tests/CntResealTests.cs`

**Interfaces:**
- Consumes: `CntHeader`, `CntEntryTable`, `PackageRegions`.
- Produces:
  ```csharp
  internal static class CntReseal
  {
      /// <summary>
      /// Rebuilds the CNT region from <paramref name="entries"/> in physical order and reseals every
      /// digest in the chain. <paramref name="cnt"/> supplies the original header, which is patched
      /// in place; it is not otherwise read.
      /// </summary>
      internal static byte[] Seal(byte[] cnt, IReadOnlyList<CntEntry> physical,
                                  string contentId, string passcode);
  }
  ```

**The algorithm, in `FinishContainer`'s own order.** Deviating from this order changes the output.

1. **Lay out.** Walk `physical`, assigning `DataOffset = num` and advancing
   `num = Align(num + DataSize, 16)`, starting from `body_offset`. METAS' `DataSize` is
   `entryCount * 32`. Then `body_size = Align(body_offset + total, 65536) - body_offset` (the
   publisher profile: `ENTRY_KEYS.DataSize == 2944`). Set `entry_count`, `entry_count_2`,
   `entry_table_offset` (= METAS' new `DataOffset`), and
   `main_ent_data_size = sum of the first sc_entry_count-1 payload lengths in physical order`.
   Set `mandatory_size`, `desc_mandatory_offset/size` from entry 1034 and
   `desc_image_key_offset/size` from entry 32.
2. **Meta table.** Write the 32-byte records, sorted ascending by id, at `entry_table_offset`.
   Preserve each record's trailing 8 bytes from the original where the entry already existed.
3. **GENERAL_DIGESTS (128).** Recompute and rewrite its payload *before* the body is written:
   - `HeaderDigest` = `ProsperoImageDigests.ComputeHeaderDigest(header[0..64), ProsperoImageDigests.ForceFihRelativeImageOffset(header[1024..1152)))`
   - `ContentDigest` = `ComputeContentDigest(descriptor56, gameDigest, new byte[32], includeGame)`;
     `descriptor56` is content id ASCII at 0, BE32 `drm_type` at 48, BE32 `content_type` at 52
   - `GameDigest` = `TargetDigest` = the header's `pfs_image_digest` (1088, 32 B), when `content_type != 34`
   - `SystemDigest`/`PlaygoDigest` = `ComputeConcatDigest` over `ComputeEntryDigest(payload)` of each
     contributing entry, ordered **ascending by id at runtime** (`ComputeConcatOverEntries` does the
     `.OrderBy`, so the constants' own order is irrelevant). The digest is omitted entirely when
     nothing contributes.

     ```csharp
     SystemMediaIds = [0x1006, 0x100D, 0x1200, 0x1220, 0x1240, 0x1280, 0x12A0, 0x12C0, 0x2040, 0x2060];
     PlaygoIds      = [0x1001, 0x2010, 0x2011, 0x3000];
     ```

     **"Contributing" is stricter than "present in the entry table."** `ComputeConcatOverEntries`
     filters on `e is GenericEntry { FileData: not null }`, so an entry listed in the meta table but
     without a materialised payload contributes nothing. This repair builds its entry list by
     parsing bytes, so **every** entry must carry its payload or these digests will silently differ.
     In this package 0x100D (4109) and 0x1240 (4672) are absent and the other eight system ids
     contribute; all four PlayGo ids are present.

     **On `PlaygoIds`, do not trust `~/Developer/LibProsperoPKG`.** That tree has
     `[0x1001, 0x2010, 0x2011]` — three ids, no `0x3000`. It is the Aug 2026 upstream and predates
     the shipped release. The 0.6.9 assembly this CLI loads has `new uint[4] { 4097u, 8208u, 8209u,
     12288u }`, and that is what the oracle was built with. Verified against the stored
     `GENERAL_DIGESTS.PlaygoDigest` in both packages: the four-id set reproduces it in each, the
     three-id set reproduces neither.

     ```
     original 0.6.8   stored C0D14B2BE200ECEF…   {4097,8208,8209,12288} matches
     oracle   0.6.9   stored 86D9C2FECD721325…   {4097,8208,8209,12288} matches
     ```

     The consequence matters for Task 8: changing `playgo-scenario.json` **does** move
     `PlaygoDigest`.
   - `ParamDigest` = `ComputeEntryDigest(entry 8192 payload)`
   Write them back with `GeneralDigestsEntry.Read` / `Set` / `Write` so the library owns that
   record's format.
4. **Body.** Write every entry payload at its new `DataOffset`. Encrypted entries are re-encrypted
   with `Entry.WriteEncrypted(stream, contentId, passcode, publisherProfile: true)` on a
   `GenericEntry` carrying the new `meta` — the key and IV derive from the meta record, so an entry
   that moved must be re-encrypted.
5. **DIGESTS (1).** For `i` from **1** to `ById.Count - 1` (index 0, DIGESTS itself, is skipped and
   keeps its original 32 bytes): `SHA3-256` over
   `cnt[DataOffset .. DataOffset + (Encrypted ? Align(DataSize,16) : DataSize))`, written into slot
   `i` of the DIGESTS payload *and* into the body at `digestsOffset + 32*i`.
6. `body_digest` = `Sha3_256(cnt[body_offset .. body_offset+body_size))` → offset 352.
7. `digest_table_hash` = `Sha3_256(DIGESTS payload)` → offset 320.
8. `sc_entries1_hash` = `Sha3_256` over the concatenated body bytes of ENTRY_KEYS(16),
   IMAGE_KEY(32) if present, GENERAL_DIGESTS(128), METAS(256), DIGESTS(1) — in that **semantic**
   order, each `DataOffset` for `DataSize` — → offset 256.
9. `sc_entries2_hash` = the same list without its last element (DIGESTS), except METAS contributes
   only `sc_entry_count * 32` bytes → offset 288.
10. `desc_digest` = `Sha3_256(imageKeyBytes) || Sha3_256(imagedigsBytes)` (64 B) → offset 1312, only
    when both `desc_*_size` are non-zero.
11. Package digest: copy `cnt[0..4064)`, force BE64 `[1040..1048) = 65536` in the copy,
    `ComputePackageDigest` → write 32 B at 4064.
12. Wrap: copy `cnt[0..4096)`, force the **same** BE64 at `[1040..1048)`,
    `ProsperoPublisherRsa.BuildCntHeaderWrap` → write at 4096.

- [ ] **Step 1: Write the failing test — GATE 1**

```csharp
public class CntResealTests
{
    /// <summary>
    /// FIXED POINT 1. Reseal with nothing changed. If the output is byte-identical to the input
    /// then every digest, offset and header field in this reimplementation matches what
    /// LibProsperoPkg itself produced. This is the total check the design rests on; if it ever
    /// regresses, nothing downstream can be trusted.
    /// </summary>
    [SkippableFact]
    public void ResealingAnUnchangedPackageIsAFixedPoint()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var t = CntEntryTable.Parse(r.Cnt, Passcode);
        var contentId = ProsperoPkgReader.Read(TestPackage.Path).Header.ContentId;

        var resealed = CntReseal.Seal(r.Cnt, t.Physical, contentId, new string('0', 32));

        // Staged, cheapest and most-localising first. A bare whole-buffer compare reports an
        // offset; these report the STAGE, which is what you actually need at 3am.
        Region("header",          r.Cnt, resealed, 0, 0x2000);
        Region("meta table",      r.Cnt, resealed,
               (int)CntHeader.U32(r.Cnt, CntHeader.EntryTableOffset), 27 * 32);
        Entry ("DIGESTS",         r.Cnt, resealed, t, 1);
        Entry ("GENERAL_DIGESTS", r.Cnt, resealed, t, 128);
        Entry ("METAS",           r.Cnt, resealed, t, 256);
        Region("body",            r.Cnt, resealed,
               (int)CntHeader.U64(r.Cnt, CntHeader.BodyOffset),
               (int)CntHeader.U64(r.Cnt, CntHeader.BodySize));
        Bytes.AssertEqual(r.Cnt, resealed);
    }

    /// <summary>
    /// FIXED POINT 3. The one above proves the digest chain but exercises NO entry movement —
    /// every offset it computes is the one already on disk. The repair shifts everything after
    /// entry 4097 down by 1360 and then the last entry up by 312, and nothing else between the
    /// two gates tests relayout on its own. So: grow a late entry, reseal, shrink it back,
    /// reseal again, and require the round trip to land on the original CNT. Needs no oracle,
    /// and fails HERE rather than as a mystery byte mismatch in Task 9.
    /// </summary>
    [SkippableFact]
    public void RelayoutRoundTripsThroughAChangedEntryLength()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var contentId = ProsperoPkgReader.Read(TestPackage.Path).Header.ContentId;
        var passcode = new string('0', 32);

        // playgo-scenario.json (12288) is last in physical order; pad it so everything after it
        // in the body — nothing — moves, then pick a mid-list entry so plenty does.
        var grown = CntEntryTable.Parse(r.Cnt, Passcode);
        var target = grown[4097];                       // physical index 15 of 27
        var original = target.Payload;
        target.Payload = [.. original, .. new byte[1000]];
        target.DataSize = (uint)target.Payload.Length;

        var bigger = CntReseal.Seal(r.Cnt, grown.Physical, contentId, passcode);
        Assert.NotEqual(Convert.ToHexString(r.Cnt), Convert.ToHexString(bigger));

        var shrunk = CntEntryTable.Parse(bigger, Passcode);
        shrunk[4097].Payload = original;
        shrunk[4097].DataSize = (uint)original.Length;
        var back = CntReseal.Seal(bigger, shrunk.Physical, contentId, passcode);

        Bytes.AssertEqual(r.Cnt, back);
    }

    private static void Region(string stage, byte[] want, byte[] got, int off, int len)
    {
        for (int i = 0; i < len; i++)
            if (want[off + i] != got[off + i])
                Assert.Fail($"{stage}: first difference at 0x{off + i:X} (0x{i:X} into the region) — " +
                            $"expected 0x{want[off + i]:X2}, got 0x{got[off + i]:X2}");
    }

    private static void Entry(string stage, byte[] want, byte[] got, CntEntryTable t, uint id) =>
        Region(stage, want, got, (int)t[id].DataOffset, (int)t[id].DataSize);
}
```

**Reading a staged failure.** Each stage points at a different task:

| stage | what is wrong |
|---|---|
| header, offset < 0x100 | a layout scalar — `body_size`, `entry_table_offset`, `main_ent_data_size` |
| header, 0x100–0x180 | `sc_entries1/2_hash` or `digest_table_hash` — step 8/9 ordering |
| header, 0x520 | `desc_digest` — step 10 |
| header, 0xFE0 / 0x1000 | the package digest or the wrap — the `[1040..1048)` force |
| meta table | Task 4's record layout, or the trailing-8-bytes preservation |
| GENERAL_DIGESTS | step 3 — most likely the `PlaygoIds`/`SystemMediaIds` sets or a missing payload |
| DIGESTS | step 5 — most likely the loop starting at 0 instead of 1, or the encrypted-entry rounding |
| body (and nothing above) | an entry payload — re-encryption of a moved encrypted entry |

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter CntReseal`
Expected: FAIL — `CntReseal` does not exist.

- [ ] **Step 3: Implement `CntReseal.Seal` following steps 1–12 above**

Work on a copy: `var outCnt = (byte[])cnt.Clone();` then zero `[body_offset, body_size)` before
writing the body, so that any byte the layout does not cover is deterministic. If gate 1 then fails
on padding bytes, the original padding was not zero — in that case preserve it instead of zeroing,
and record which it was in a comment. Do not guess; let the test decide.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter CntReseal`
Expected: PASS.

Expected: 2 passed — the no-op fixed point and the relayout round trip.

The staged table above names the culprit for the first. For the second, a failure is specifically a
*layout* fault: offsets, `body_size` rounding, the meta table's sort, `main_ent_data_size`, or the
re-encryption of an encrypted entry that moved. **Do not proceed to Task 6 until both pass** — every
later task assumes them, and Task 9 is the first thing that would otherwise exercise movement, by
which point the oracle mismatch tells you much less.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/CntReseal.cs fpkg-cli.Tests/CntResealTests.cs
git commit -m "feat: reseal the CNT digest chain from public primitives

Verified by two fixed points: resealing an unchanged package reproduces it
byte for byte, and growing then shrinking an entry round-trips through the
relayout path back to the original."
```

---

### Task 6: Recover the PlayGo values from the original `playgo-chunk.dat`

**Files:**
- Create: `fpkg-cli/RepairPlayGo/PlayGoRecovery.cs`
- Test: `fpkg-cli.Tests/PlayGoRecoveryTests.cs`

**Interfaces:**
- Consumes: `CntEntryTable`.
- Produces:
  ```csharp
  internal sealed record PlayGoRecovery(
      int ChunkCount, int ScenarioCount, int DefaultScenarioId, int DefaultLanguageId,
      int ExtentCount, ulong LanguageMask, string ContentId,
      ulong TotalSize, ulong DataSize, ulong TailSize,
      IReadOnlyList<string> ScenarioLabels)
  {
      internal static PlayGoRecovery From(ReadOnlySpan<byte> chunkDat);
  }
  ```

Field offsets, from the spec's recovered-values table (all little-endian — this is the PlayGo
descriptor, not the CNT header):

| value | where |
|---|---|
| chunk count | u16 @ 10 |
| scenario count | u16 @ 14 |
| default scenario id | u16 @ 20 |
| extent count | u32 @ 32 |
| default language id | u8 @ 36 |
| language mask | u64 @ 56 |
| content id | 36 ASCII @ 64 (48-byte slot) |
| extent section offset | u32 @ 216 |
| scenario label section | offset u32 @ 240, length u32 @ 244, NUL-separated |

Extents are 16-byte records: u64 offset, u64 length, **both masked with `0xFFFFFFFFFFFF`**.
`TotalSize` is the sum of all extent lengths, `TailSize` is the last extent's length, and
`DataSize = TotalSize - TailSize`.

- [ ] **Step 1: Write the failing test**

```csharp
public class PlayGoRecoveryTests
{
    [SkippableFact]
    public void RecoversTheMeasuredValues()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var r = PlayGoRecovery.From(t[4097].Payload);

        Assert.Equal(100, r.ChunkCount);
        Assert.Equal(1,   r.ScenarioCount);
        Assert.Equal(0,   r.DefaultScenarioId);
        Assert.Equal(1,   r.DefaultLanguageId);
        Assert.Equal(101, r.ExtentCount);
        Assert.Equal(0xFFFFFFFFFFFFFFFFul, r.LanguageMask);
        Assert.Equal(0x23920000ul, r.TotalSize);
        Assert.Equal(0x23830000ul, r.DataSize);
        Assert.Equal(0xF0000ul,    r.TailSize);
        Assert.Equal(["Scenario #0"], r.ScenarioLabels);
        Assert.Equal(TestPackage.ContentId, r.ContentId);
    }

    [SkippableFact]
    public void AgreesWithTheLibrarysOwnCountsReader()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var r = PlayGoRecovery.From(t[4097].Payload);
        // ProsperoPlayGo.ReadChunkCounts returns the internal nested record Counts; read its two
        // properties reflectively rather than duplicating the parse.
        var counts = typeof(ProsperoPlayGo).Assembly
            .GetType("LibProsperoPkg.PlayGo.ProsperoPlayGo")!
            .GetMethod("ReadChunkCounts")!
            .Invoke(null, [ (object)t[4097].Payload ])!;   // adapt for the ReadOnlySpan parameter
        Assert.Equal(r.ChunkCount,    (int)counts.GetType().GetProperty("ChunkCount")!.GetValue(counts)!);
        Assert.Equal(r.ScenarioCount, (int)counts.GetType().GetProperty("ScenarioCount")!.GetValue(counts)!);
    }
}
```

Note on the second test: `ReadChunkCounts` takes a `ReadOnlySpan<byte>`, which cannot be passed
through `MethodInfo.Invoke`. If that blocks, delete this test and keep the first — the measured
values are the stronger check anyway, and Task 7 compares the *generated* bytes against the oracle,
which subsumes it. Do not spend more than a few minutes here.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter PlayGoRecovery`
Expected: FAIL — `PlayGoRecovery` does not exist.

- [ ] **Step 3: Implement**

```csharp
internal static PlayGoRecovery From(ReadOnlySpan<byte> d)
{
    if (d.Length < 256) throw new InvalidDataException("playgo-chunk.dat is too small.");

    int chunkCount    = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]);
    int scenarioCount = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
    int defScenario   = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]);
    int extentCount   = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[32..]);
    int defLanguage   = d[36];
    ulong mask        = BinaryPrimitives.ReadUInt64LittleEndian(d[56..]);
    string contentId  = Encoding.ASCII.GetString(d.Slice(64, 36)).TrimEnd('\0');
    int extents       = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[216..]);

    const ulong Mask48 = 0xFFFF_FFFF_FFFFul;
    ulong total = 0, tail = 0;
    for (int i = 0; i < extentCount; i++)
    {
        int at = extents + i * 16;
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(d[(at + 8)..]) & Mask48;
        total += length;
        tail = length;                         // the last one wins
    }

    int labelsAt  = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[240..]);
    int labelsLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[244..]);
    var labels = Encoding.ASCII.GetString(d.Slice(labelsAt, labelsLen))
        .Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Take(scenarioCount).ToArray();

    return new PlayGoRecovery(chunkCount, scenarioCount, defScenario, defLanguage, extentCount,
                              mask, contentId, total, total - tail, tail, labels);
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter PlayGoRecovery`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/PlayGoRecovery.cs fpkg-cli.Tests/PlayGoRecoveryTests.cs
git commit -m "feat: recover PlayGo layout values from an existing playgo-chunk.dat"
```

---

### Task 7: Build the oracle

The acceptance oracle is derived from the package itself. Build it once and cache it; a rebuild of a
630 MB package is not something to repeat per test.

**Files:**
- Create: `fpkg-cli.Tests/Oracle.cs`
- Test: `fpkg-cli.Tests/OracleTests.cs`

**Interfaces:**
- Consumes: `TestPackage`.
- Produces: `internal static class Oracle` with `static string Path` (the 0.6.9 rebuild .pkg) and
  `static bool Available`.

**An oracle already exists.** A 0.6.9 rebuild is sitting at
`/private/tmp/claude-501/-Users-rrocha-Developer-fpkg-cli/220ac4e5-cdcf-4593-a58d-4c51c316f7d7/scratchpad/t2out/EP4060-PPSA25872_00-T2DNFMAINGAMEPS5-A0102-V0102.pkg`
(661,005,150 bytes), with its merged source tree beside it in `t2src/`. That is **session scratch**:
treat it as a cache to copy from, never as a dependency. If it is there, copy it into `.oracle/pkg/`
and skip to Step 2 — but still run the verification in Step 1, because a cached oracle you have not
checked is worth less than no oracle. If it is gone, the regeneration below takes about ten seconds
of your attention and is the real answer.

- [ ] **Step 1: Produce the oracle, or adopt the cached one, and verify it either way**

```bash
cd /Users/rrocha/Developer/fpkg-cli
PKG=Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg
O=.oracle           # gitignored; see step 4
mkdir -p $O
./fpkg extract "$PKG" $O/out
./fpkg extract "$PKG" $O/out --rebuild-source
cp -R $O/out/inner $O/src && cp -R $O/out/source/sce_sys/. $O/src/sce_sys/
./fpkg build --source $O/src --out $O/pkg
ls -l $O/pkg
```

Record the produced file's size and SHA-256 in the commit message. Verify before going further:

```bash
./fpkg info $O/pkg/*.pkg | grep -E '4097|8209|12288'
```

Expected: `playgo-chunk.dat` 5376, `playgo-ficm.dat` 62, `playgo-scenario.json` 2293, and a file
size of exactly 661,005,150. **If any of those do not match, stop and report** — the oracle is not
the package the spec measured, and nothing after this point means anything.

Already confirmed empirically and worth not re-deriving: `param.json` is byte-identical between the
original and the oracle, so the rebuild's `param.json` normalisation is idempotent and will not
sabotage gate 2.

- [ ] **Step 2: Write the failing test**

```csharp
public class OracleTests
{
    [SkippableFact]
    public void TheOracleHasTheTargetEntrySizes()
    {
        Skip.IfNot(Oracle.Available, "oracle not built — see docs/superpowers/plans/2026-09-18-repair-playgo.md Task 7");
        var t = CntEntryTable.Parse(PackageRegions.Load(Oracle.Path).Cnt, Passcode);
        Assert.Equal(5376u, t[4097].DataSize);
        Assert.Equal(62u,   t[8209].DataSize);
        Assert.Equal(2293u, t[12288].DataSize);
    }

    [SkippableFact]
    public void TheOraclesOuterPfsIsIdenticalToTheOriginals()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var a = PackageRegions.Load(TestPackage.Path);
        var b = PackageRegions.Load(Oracle.Path);
        Assert.Equal(a.OuterPfsSize, b.OuterPfsSize);
        Assert.Equal(Bytes.Sha256Range(a.SourcePath, a.OuterPfsOffset, a.OuterPfsSize),
                     Bytes.Sha256Range(b.SourcePath, b.OuterPfsOffset, b.OuterPfsSize));
    }
}
```

The second test is the spec's central claim — *the payload never changes* — asserted rather than
assumed. Expected digest prefix: `3b7702ffc695b125…`.

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter Oracle`
Expected: FAIL — `Oracle` does not exist.

- [ ] **Step 4: Implement `Oracle` and gitignore the artifacts**

```csharp
internal static class Oracle
{
    /// <summary>
    /// The 0.6.9 rebuild that the repair must reproduce byte for byte. Built by hand once —
    /// see Task 7 of the implementation plan — because a 630 MB rebuild is not a per-test cost.
    /// </summary>
    internal static string Path { get; } =
        Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "*.pkg").FirstOrDefault() ?? "" : "";

    internal static bool Available => Path.Length > 0 && File.Exists(Path);

    private static string Dir => System.IO.Path.Combine(TestPackage.RepoRoot, ".oracle", "pkg");
}
```

Append to `.gitignore`:

```
# Acceptance oracle for `fpkg repair-playgo`: a 0.6.9 rebuild of the test package,
# regenerated from the package itself (see docs/superpowers/plans/2026-09-18-repair-playgo.md).
/.oracle/
```

- [ ] **Step 5: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter Oracle`
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add fpkg-cli.Tests/Oracle.cs fpkg-cli.Tests/OracleTests.cs .gitignore
git commit -m "test: build and gitignore the 0.6.9 acceptance oracle"
```

---

### Task 8: Generate the three replacement entries

**Files:**
- Create: `fpkg-cli/RepairPlayGo/PlayGoEntries.cs`
- Test: `fpkg-cli.Tests/PlayGoEntriesTests.cs`

**Interfaces:**
- Consumes: `PlayGoRecovery`.
- Produces:
  ```csharp
  internal sealed record PlayGoEntries(byte[] ChunkDat, byte[] Ficm, byte[] ScenarioJson)
  {
      internal static PlayGoEntries Build(PlayGoRecovery r, byte[] originalFicm);
  }
  ```

**`playgo-chunk.dat`** — via `BuildLanguageChunkLayout` + `BuildMultiChunkDat`. The first returns
the internal record `LanguageChunkLayout`, so it goes through reflection; the second is fully
public and is called directly.

```csharp
private static (IReadOnlyList<ulong> Main, IReadOnlyList<ulong> Masks)
    Layout(ulong dataSize, int chunkCount, ulong languageMask)
{
    // BuildLanguageChunkLayout is public but returns the internal nested record
    // ProsperoPlayGo.LanguageChunkLayout, which C# cannot name. This is the only
    // reflection in the feature, and it is over a public method.
    var m = typeof(ProsperoPlayGoLayoutInfo).Assembly
        .GetType("LibProsperoPkg.PlayGo.ProsperoPlayGo")!
        .GetMethod("BuildLanguageChunkLayout", [typeof(ulong), typeof(int), typeof(ulong)])
        ?? throw new InvalidOperationException(
            "ProsperoPlayGo.BuildLanguageChunkLayout(ulong,int,ulong) not found. The shipped " +
            "LibProsperoPkg has changed; re-derive repair-playgo against it before shipping.");
    var layout = m.Invoke(null, [dataSize, chunkCount, languageMask])!;
    var t = layout.GetType();
    return ((IReadOnlyList<ulong>)t.GetProperty("MainChunkSizes")!.GetValue(layout)!,
            (IReadOnlyList<ulong>)t.GetProperty("ChunkLanguageMasks")!.GetValue(layout)!);
}
```

then

```csharp
var (main, masks) = Layout(r.DataSize, r.ChunkCount, r.LanguageMask);
var chunkDat = ProsperoPlayGo.BuildMultiChunkDat(
    r.ContentId, main, r.TailSize,
    publisherNwonly: true, includePublisherLabels: true,
    languageMask: r.LanguageMask,
    scenarioCount: r.ScenarioCount,
    initialChunkCount: r.ChunkCount,
    defaultScenarioId: r.DefaultScenarioId,
    scenarioLabels: r.ScenarioLabels,
    chunkLanguageMasks: masks,
    defaultLanguageId: r.DefaultLanguageId);
```

**`playgo-ficm.dat`** — copy the original and zero every chunk id: 16-byte header, then 2 bytes per
file with the chunk id in the first. Same length in, same length out.

**`playgo-scenario.json`** — `ProsperoPlayGo.BuildScenarioJson(r.ScenarioCount, r.LanguageMask,
r.DefaultScenarioId)`. 0.6.9 does not append to the old bytes; it regenerates (see the corrected
spec section). Regeneration discards localized presentation, which is why Task 11 adds a guard.

- [ ] **Step 1: Write the failing test**

```csharp
public class PlayGoEntriesTests
{
    [SkippableFact]
    public void GeneratedEntriesAreByteIdenticalToTheOracles()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var orig   = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var oracle = CntEntryTable.Parse(PackageRegions.Load(Oracle.Path).Cnt, Passcode);

        var built = PlayGoEntries.Build(PlayGoRecovery.From(orig[4097].Payload), orig[8209].Payload);

        Assert.Equal(Convert.ToHexString(oracle[4097].Payload),  Convert.ToHexString(built.ChunkDat));
        Assert.Equal(Convert.ToHexString(oracle[8209].Payload),  Convert.ToHexString(built.Ficm));
        Assert.Equal(Convert.ToHexString(oracle[12288].Payload), Convert.ToHexString(built.ScenarioJson));
    }

    [SkippableFact]
    public void TheGeneratedSetIsAValidPlayGoLayout()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var r = PlayGoRecovery.From(t[4097].Payload);
        var built = PlayGoEntries.Build(r, t[8209].Payload);

        var info = ProsperoPlayGo.ValidateLayout(
            built.ChunkDat, built.Ficm, t[8208].Payload, built.ScenarioJson, r.ContentId, null);

        Assert.Equal(100, info.ChunkCount);
        Assert.Equal(1,   info.ScenarioCount);
        Assert.Equal(33,  info.ExtentCount);          // 101 → 33
        Assert.Equal(0x23920000ul, info.CoveredBytes);
        Assert.Equal(23,  info.FileCount);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter PlayGoEntries`
Expected: FAIL — `PlayGoEntries` does not exist.

- [ ] **Step 3: Implement `PlayGoEntries.cs` as described above**

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter PlayGoEntries`
Expected: 2 passed. Sizes should come out 5376 / 62 / 2293.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/PlayGoEntries.cs fpkg-cli.Tests/PlayGoEntriesTests.cs
git commit -m "feat: regenerate playgo-chunk.dat, playgo-ficm.dat and playgo-scenario.json"
```

---

### Task 9: Repair the CNT region end to end

**Files:**
- Create: `fpkg-cli/RepairPlayGo/CntRepair.cs`
- Test: `fpkg-cli.Tests/CntRepairTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
  ```csharp
  internal sealed record CntRepairResult(
      byte[] Cnt, byte[] NewChunkDat, long SlackBefore, long NetDelta);

  internal static class CntRepair
  {
      internal static CntRepairResult Repair(byte[] cnt, string contentId, string passcode);
  }
  ```

`Repair` parses the table, recovers the values, builds the three entries, substitutes their
payloads, checks the slack, and calls `CntReseal.Seal`. Slack is
`body_offset + body_size - (last physical entry's DataOffset + its UNALIGNED DataSize)`.
The last entry's 16-byte alignment tail counts as slack precisely because nothing follows it:
entry 12288 ends at 63,525,245 (0x3C9517D) and the body ends at 63,569,920, giving **44,675**.
Using the aligned end gives 44,672 and under-reports by 3 bytes. An earlier draft of this line
said "aligned", which contradicted both the spec's measured figure and this plan's own
assertion; the measured value is authoritative, and `NetDelta` is
the signed sum of the three size deltas — **negative for this package**. Only a `NetDelta` greater
than `SlackBefore` throws; a negative one is normal and simply leaves more slack. The throw carries
both numbers and points at the `body_size` bump described in the spec as the unimplemented fallback.

- [ ] **Step 1: Write the failing test — GATE 2a**

```csharp
public class CntRepairTests
{
    /// <summary>
    /// FIXED POINT 2, CNT half. The repaired CNT must equal the oracle's CNT byte for byte.
    /// </summary>
    [SkippableFact]
    public void TheRepairedCntEqualsTheOracles()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var orig = PackageRegions.Load(TestPackage.Path);
        var want = PackageRegions.Load(Oracle.Path).Cnt;

        var got = CntRepair.Repair(orig.Cnt, TestPackage.ContentId, new string('0', 32));

        Assert.Equal(44_675, got.SlackBefore);
        Assert.Equal(-1048,  got.NetDelta);     // 4097 −1360, 8209 0, 12288 +312
        Bytes.AssertEqual(want, got.Cnt);
    }

    /// <summary>
    /// The net delta is negative on every package on disk, so the guard has to be provoked.
    /// </summary>
    [SkippableFact]
    public void RefusesWhenGrowthExceedsSlack()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        // Synthesise the condition: shrink body_size so the slack is 16 bytes.
        var cnt = PackageRegions.Load(TestPackage.Path).Cnt;
        var t = CntEntryTable.Parse(cnt, Passcode);
        var last = t.Physical[^1];
        CntHeader.SetU64(cnt, CntHeader.BodySize,
            (ulong)(last.DataOffset + last.DataSize + 16) - CntHeader.U64(cnt, CntHeader.BodyOffset));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CntRepair.Repair(cnt, TestPackage.ContentId, new string('0', 32)));
        Assert.Contains("slack", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter CntRepair`
Expected: FAIL — `CntRepair` does not exist.

- [ ] **Step 3: Implement `CntRepair.cs`**

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter CntRepair`
Expected: 2 passed.

If the byte-compare fails inside a PlayGo entry's range, Task 8 is wrong. If it fails in the header
or the meta table, Task 5's relayout round trip should have caught it first — go back and work out
why it did not, rather than patching the layout against the oracle here. Debugging layout against a
661 MB oracle is exactly the situation fixed point 3 exists to prevent.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/CntRepair.cs fpkg-cli.Tests/CntRepairTests.cs
git commit -m "feat: repair the CNT region and refuse when growth exceeds the body slack"
```

---

### Task 10: Repair the SI segment

**Files:**
- Create: `fpkg-cli/RepairPlayGo/SiRepair.cs`
- Test: `fpkg-cli.Tests/SiRepairTests.cs`

**Interfaces:**
- Consumes: `PackageRegions`, `CntRepairResult`.
- Produces:
  ```csharp
  internal static class SiRepair
  {
      /// <summary>Reads the SI zip's members so a repaired one can be rebuilt from them.</summary>
      internal static IReadOnlyDictionary<string, byte[]> ReadMembers(byte[] si);

      /// <summary>
      /// Rebuilds the SI zip with the new playgo-chunk.dat and a CRC table recomputed over the
      /// repaired mount image (FIH + outer PFS + CNT).
      /// </summary>
      internal static byte[] Rebuild(byte[] si, string contentId, byte[] newChunkDat,
                                     Stream repairedMountImage, long mountImageLength);
  }
  ```

The members of this package, per the spec: `common/etc/naps_meta_18.dat`,
`common/etc/naps_meta_{300,301,302,308}.dat` (one blob, four paths),
`common/etc/playgo-chunk.dat`, `config/<contentId>/playgo-chunk.crc`. `pfsimage.xml` is **absent**,
and its presence is a refusal.

Rebuild with `ProsperoSiArchive.BuildMembers(contentId, pfsImageXml: null, playGoChunkDat,
napsMeta18, napsMeta300, playGoChunkCrc)` and `ProsperoSiArchive.WriteZip(members)` — the library
owns the zip format and the member order.

**The CRC table and its length.** `ProsperoPlayGo.BuildChunkCrc(stream, length)` emits
`ceil(length / 65536)` little-endian CRC32C words, four bytes each, with no header — the last block
is a *partial* read of whatever remains, so the exact length changes the last entry's value, not
only the entry count. Getting it wrong is the easiest way to fail the SI fixed point.

The length is **`CntOffset + Cnt.Length` = 660,340,736**, giving exactly 10,076 entries = the stored
40,304 bytes. Derived, not guessed: the stored table's last entry is `AF4E2A3E`, and searching every
possible end offset in the final block for the one whose CRC32C matches yields 660,340,736 uniquely.
Equivalently `CntOffset + body_offset + body_size`, because this package's CNT region has no padding
past the body — the two expressions coincide. `CntOffset + body_size` (8,192 short) does **not**
match, so the alignment is genuinely to the region end and not to the body size alone.

Because `NetDelta` is negative and `body_size` is unchanged, the repaired table has the **same**
entry count as the original. Assert that: a length change indicts the length rule, not the content.

CRC32C over ~660 MB is fast, so the spec's observation that entries differ only from index 9106 is
**not** an optimisation to implement — it is a free test. Assert entries 0–9105 are unchanged from
the original; that catches a wrong length or a wrong mount range immediately and far more precisely
than a whole-table compare.

- [ ] **Step 1: Write the failing test — starting with the SI's own fixed point**

```csharp
public class SiRepairTests
{
    /// <summary>
    /// FIXED POINT for the SI: rebuilding the zip from its own members, with the ORIGINAL
    /// chunk.dat and the ORIGINAL mount image, must reproduce it byte for byte. This proves
    /// BuildMembers/WriteZip reconstruct the same container before any content changes.
    /// </summary>
    [SkippableFact]
    public void RebuildingTheSiFromItsOwnMembersIsAFixedPoint()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var members = SiRepair.ReadMembers(r.Si);
        Assert.False(members.ContainsKey("common/etc/pfsimage.xml"));

        using var mount = File.OpenRead(TestPackage.Path);
        var got = SiRepair.Rebuild(r.Si, TestPackage.ContentId,
                                   members["common/etc/playgo-chunk.dat"],
                                   mount, r.CntOffset + r.Cnt.Length);
        Bytes.AssertEqual(r.Si, got);
    }

    [SkippableFact]
    public void TheCrcTableLengthRuleIsTheRegionEnd()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var r = PackageRegions.Load(TestPackage.Path);
        var stored = SiRepair.ReadMembers(r.Si)[$"config/{TestPackage.ContentId}/playgo-chunk.crc"];
        Assert.Equal(40_304, stored.Length);                       // 10,076 entries
        Assert.Equal(660_340_736, r.CntOffset + r.Cnt.Length);     // ceil(/65536) == 10,076

        using var mount = File.OpenRead(TestPackage.Path);
        var built = ProsperoPlayGo.BuildChunkCrc(mount, r.CntOffset + r.Cnt.Length);
        Bytes.AssertEqual(stored, built);

        // The 8,192-shorter candidate must NOT match, or the rule is underdetermined.
        mount.Position = 0;
        var shorter = ProsperoPlayGo.BuildChunkCrc(
            mount, r.CntOffset + (long)CntHeader.U64(r.Cnt, CntHeader.BodySize));
        Assert.NotEqual(Convert.ToHexString(stored), Convert.ToHexString(shorter));
    }

    /// <summary>FIXED POINT 2, SI half.</summary>
    [SkippableFact]
    public void TheRepairedSiEqualsTheOracles()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var orig = PackageRegions.Load(TestPackage.Path);
        var want = PackageRegions.Load(Oracle.Path).Si;

        var repaired = CntRepair.Repair(orig.Cnt, TestPackage.ContentId, new string('0', 32));

        // The CRC covers the repaired mount image, so it has to be spliced first.
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            orig.WriteTo(tmp, repaired.Cnt, []);
            using var mount = File.OpenRead(tmp);
            var got = SiRepair.Rebuild(orig.Si, TestPackage.ContentId, repaired.NewChunkDat,
                                       mount, orig.CntOffset + repaired.Cnt.Length);

            // Localise before comparing the whole zip: entry count first, then the prefix the
            // spec says cannot move, then everything.
            var wantCrc = SiRepair.ReadMembers(want)[$"config/{TestPackage.ContentId}/playgo-chunk.crc"];
            var gotCrc  = SiRepair.ReadMembers(got)[$"config/{TestPackage.ContentId}/playgo-chunk.crc"];
            var origCrc = SiRepair.ReadMembers(orig.Si)[$"config/{TestPackage.ContentId}/playgo-chunk.crc"];
            Assert.Equal(origCrc.Length, gotCrc.Length);                 // body_size unchanged
            Assert.Equal(Convert.ToHexString(origCrc.AsSpan(0, 9106 * 4)),
                         Convert.ToHexString(gotCrc.AsSpan(0, 9106 * 4))); // only the CNT tail moves
            Bytes.AssertEqual(wantCrc, gotCrc);
            Bytes.AssertEqual(want, got);
        }
        finally { File.Delete(tmp); }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter SiRepair`
Expected: FAIL — `SiRepair` does not exist.

- [ ] **Step 3: Implement `SiRepair.cs`**

`ReadMembers` uses `System.IO.Compression.ZipArchive` over a `MemoryStream`. The four
`naps_meta_3xx` members are identical, so pass any one of them as `napsMeta300`; assert they really
are identical and throw if not.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter SiRepair`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/RepairPlayGo/SiRepair.cs fpkg-cli.Tests/SiRepairTests.cs
git commit -m "feat: rebuild the SI segment with the repaired playgo-chunk.dat and CRC table"
```

---

### Task 11: The CLI command, the guards and the dry-run report

**Files:**
- Create: `fpkg-cli/RepairPlayGo/RepairPlayGoCommand.cs`
- Modify: `fpkg-cli/Program.cs:43` (dispatch), `fpkg-cli/Program.cs:77` (usage)
- Test: `fpkg-cli.Tests/RepairPlayGoCommandTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
  ```csharp
  internal static class RepairPlayGoCommand
  {
      internal static int Run(string[] args);
      /// <summary>Guard 4, exposed so its negative path is testable without a doctored package.</summary>
      internal static void EnsureScenarioJsonIsGeneric(byte[] scenarioJson, PlayGoRecovery r);
  }
  ```

```
fpkg repair-playgo <pkg> [--passcode <32>] [--out <path>] [--in-place] [--dry-run]
```

`--dry-run` is the default. It reports the recovered values, the three new entry sizes, the slack,
the growth, and the list of digests that would change — and writes nothing. `--out` and
`--in-place` are mutually exclusive, and one of them is required to write. `--in-place` writes to a
temporary file beside the target and renames over it only after the write succeeds.

**Five guards, all before anything is written.**

**Guard 1 — the passcode.** The most important and the least obvious, because getting it wrong is
*silent*. The reseal re-encrypts the five encrypted entries (1024, 1025, 1026, 8224, 8225) with a key
derived from the meta record plus the passcode. A wrong passcode produces garbage in those entries,
and then every digest check still passes — the digests are taken over the encrypted bytes that were
just written. It would fail only on a console.

Use `Pkg.CheckPasscode`, which needs only two fields and so does not require a parsed `Header`:

```csharp
var pkg = new Pkg { EntryKeys = KeysEntry.Read(entryKeysMeta, new MemoryStream(cnt)) };
pkg.Header.content_id = Encoding.ASCII.GetString(cnt, CntHeader.ContentId, 36).TrimEnd('\0');
if (!pkg.CheckPasscode(passcode)) return Fail("the passcode does not match this package ...");
```

Verified on the test package: 32 zeros yields `True`, 32 ones `False`, a random 32 `False`.

**Do not use a decrypt/re-encrypt round trip for this.** It looks equivalent and is not. For an entry
whose `DataSize` is already 16-aligned, decrypt-then-re-encrypt is a pure involution: a wrong key
yields wrong plaintext, and re-encrypting that wrong plaintext under the same wrong key returns the
original ciphertext exactly. Measured on this package -- entries 1024, 1025 and 1026 (sizes 1024,
512, 160) round-trip "successfully" under a deliberately wrong passcode; only 8224 and 8225 (size
532, stride 544) detect it, because their 12 padding bytes are dropped on decrypt and rebuilt as
zeros on re-encrypt. A guard built this way passes on any package whose encrypted entries all happen
to be 16-aligned.

| # | condition | message |
|---|---|---|
| 1 | `!Pkg.CheckPasscode(passcode)` | refuse: the passcode does not match this package; re-encrypting with it would silently corrupt the five protected entries while every digest still verified |
| 2 | `common/etc/pfsimage.xml` present in the SI | refuse: it encodes the full entry table and every digest, and reproducing it is unverified |
| 3 | growth > slack | refuse: name both numbers, and point at the `body_size` bump as the unimplemented fallback |
| 4 | `playgo-scenario.json` declares a member the regeneration would not reproduce (below) | refuse: **"this package carries custom scenario presentation; regenerating playgo-scenario.json would replace it with generic 'Scenario #N' labels"** -- not "differs from expected" |
| 5 | `ENTRY_KEYS.DataSize != 2944` | refuse: not the PS5 publisher profile this repair was derived against (equivalently `EntryKeys.Keys[0].key.Length != 384`) |

**On guard 4. The obvious formulation is wrong and would make the feature inert.**

Whole-file byte-equality against `BuildScenarioJson` refuses **every 0.6.8 package** -- including the
only one we can repair. Measured:

```
stored (0.6.8)  1,981 B      generated == stored ? False   <- byte-equality would refuse it
oracle (0.6.9)  2,293 B      generated == oracle ? True
per-member:  scenarioCount, scenarioDefaultId, scenarioDefaultLanguage, scenarios  -- all IDENTICAL
added by 0.6.9: chunkDefaultLanguage, chunkSupportedLanguages
```

The whole 312-byte delta is the two members 0.6.9 **adds**; the scenario presentation is
byte-identical. The plan's own "1981 -> 2293" line contradicted its own guard.

The correct rule is **additive-only**: every member the stored file declares must be reproduced with
identical raw JSON text, and the regeneration may only ADD members. That still refuses a package
carrying real localised scenario names -- its `scenarios` member would differ -- which is the
*correct* outcome, since 0.6.9's "carry the source presentation through" behaviour exists precisely
to preserve those and this repair cannot. A package with fewer than 31 languages is likewise refused,
because its `scenarios` text would disagree; that is the untested branch the spec's Open section
names, and refusing is right.

The message must say the package carries custom scenario presentation that regeneration would
replace with generic "Scenario #N" labels -- not imply the file is malformed. A dropped or
disagreeing member gets its own distinct message.

The test package's own JSON is all `"Scenario #0"`, so the guard's positive path is the only one it
exercises. **Write the negative-path test too**, synthesising a custom-label JSON -- otherwise the
guard ships untested and the first package with localised names is its first test.

Also: if `PlayGoInitialChunkProblem` reports nothing for the input, say so and exit 0 without
writing — there is nothing to repair.

- [ ] **Step 1: Write the failing test**

```csharp
public class RepairPlayGoCommandTests
{
    [SkippableFact]
    public void DryRunIsTheDefaultAndWritesNothing()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var before = File.GetLastWriteTimeUtc(TestPackage.Path);
        Assert.Equal(0, RepairPlayGoCommand.Run(["repair-playgo", TestPackage.Path]));
        Assert.Equal(before, File.GetLastWriteTimeUtc(TestPackage.Path));
    }

    [Fact]
    public void RejectsOutAndInPlaceTogether() =>
        Assert.NotEqual(0, RepairPlayGoCommand.Run(
            ["repair-playgo", "nonexistent.pkg", "--out", "x.pkg", "--in-place"]));

    [Fact]
    public void RejectsAMissingFile() =>
        Assert.NotEqual(0, RepairPlayGoCommand.Run(["repair-playgo", "nonexistent.pkg"]));

    [SkippableFact]
    public void RefusesAWrongPasscodeAndWritesNothing()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        Assert.NotEqual(0, RepairPlayGoCommand.Run(
            ["repair-playgo", TestPackage.Path, "--passcode", new string('1', 32), "--out", outPath]));
        Assert.False(File.Exists(outPath));
    }

    /// <summary>
    /// Guard 4's negative path. The test package's own scenario JSON is generic, so without this
    /// the guard ships untested and the first package with localised names is its first test.
    /// </summary>
    [SkippableFact]
    public void RefusesAScenarioJsonCarryingCustomPresentation()
    {
        Skip.IfNot(TestPackage.Exists, "test package not present");
        var t = CntEntryTable.Parse(PackageRegions.Load(TestPackage.Path).Cnt, Passcode);
        var r = PlayGoRecovery.From(t[4097].Payload);

        var generic = Encoding.UTF8.GetString(t[12288].Payload);
        var custom  = Encoding.UTF8.GetBytes(generic.Replace("Scenario #0", "Terminator 2D: No Fate"));

        RepairPlayGoCommand.EnsureScenarioJsonIsGeneric(t[12288].Payload, r);   // does not throw
        var ex = Assert.Throws<InvalidOperationException>(
            () => RepairPlayGoCommand.EnsureScenarioJsonIsGeneric(custom, r));
        Assert.Contains("custom scenario presentation", ex.Message);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter RepairPlayGoCommand`
Expected: FAIL — `RepairPlayGoCommand` does not exist.

- [ ] **Step 3: Implement the command**

Follow `PatchCommand.cs` for structure and `Program.Verify` for flag parsing (`ParseFlags`) and
error reporting (`Fail`). Reuse `Program`'s existing conventions rather than inventing new ones; if
`ParseFlags` and `Fail` are `private`, make them `internal` rather than duplicating them.

- [ ] **Step 4: Wire it into the dispatch and the usage text**

`fpkg-cli/Program.cs`, in the `args[0] switch` at line 43:

```csharp
"repair-playgo" => RepairPlayGoCommand.Run(args),
```

and in `Usage()`, beside the other command lines:

```
  fpkg repair-playgo <file.pkg> [--passcode <32>] [--out <path>|--in-place] [--dry-run]
                          Rewrites a 0.6.8-built package's PlayGo metadata to the 0.6.9 shape
                          without touching the payload. Reports and writes nothing unless
                          --out or --in-place is given.
```

- [ ] **Step 5: Run the tests and the command**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter RepairPlayGoCommand`
Expected: 3 passed.

Run: `dotnet build fpkg-cli/fpkg.csproj && ./fpkg repair-playgo Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg`
Expected: a dry-run report showing `chunks=100 scenarios=1 … extents 101 → 33`, entry sizes
6736 → 5376, 62 → 62, 1981 → 2293, slack 44,675, net delta −1048, and no file written.

- [ ] **Step 6: Commit**

```bash
git add fpkg-cli/RepairPlayGo/RepairPlayGoCommand.cs fpkg-cli/Program.cs fpkg-cli.Tests/RepairPlayGoCommandTests.cs
git commit -m "feat: add the fpkg repair-playgo command with a dry-run default"
```

---

### Task 12: Acceptance — the whole-package byte compare

**Files:**
- Create: `fpkg-cli.Tests/AcceptanceTests.cs`

- [ ] **Step 1: Write the failing test — GATE 2**

```csharp
public class AcceptanceTests
{
    /// <summary>
    /// FIXED POINT 2. Repair the 0.6.8 package; the result must be byte-identical to the 0.6.9
    /// rebuild of the same source. This is the gate. Nothing ships without it.
    /// </summary>
    [SkippableFact]
    public void TheRepairedPackageEqualsTheOracle()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            Assert.Equal(0, RepairPlayGoCommand.Run(
                ["repair-playgo", TestPackage.Path, "--out", outPath]));
            Assert.Equal(new FileInfo(Oracle.Path).Length, new FileInfo(outPath).Length);
            Assert.Equal(Bytes.Sha256(Oracle.Path), Bytes.Sha256(outPath));
        }
        finally { File.Delete(outPath); }
    }

    [SkippableFact]
    public void TheRepairedPackagePassesFullVerification()
    {
        Skip.IfNot(Oracle.Available && TestPackage.Exists, "oracle or test package not built");
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pkg");
        try
        {
            RepairPlayGoCommand.Run(["repair-playgo", TestPackage.Path, "--out", outPath]);
            var result = ProsperoPackageArchive.VerifyPackageFull(outPath, new string('0', 32));
            Assert.Empty(result.Issues);
        }
        finally { File.Delete(outPath); }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj --filter Acceptance`
Expected: 2 passed. If the SHA differs, the region-level tests in Tasks 9 and 10 have already
localised it — re-run those first.

- [ ] **Step 3: Confirm the warning is gone**

```bash
./fpkg repair-playgo Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg --out /tmp/repaired.pkg
./fpkg verify /tmp/repaired.pkg --full
```

Expected: no `WARNING: PlayGo:` line, no issues.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test fpkg-cli.Tests/fpkg.Tests.csproj`
Expected: everything passes, including both fixed points.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli.Tests/AcceptanceTests.cs
git commit -m "test: gate repair-playgo on byte-identity with the 0.6.9 rebuild"
```

---

### Task 13: Documentation

**Files:**
- Modify: `CHANGELOG.md`, `docs/repair-playgo-spec.md`

- [ ] **Step 1: Add the changelog entry**

Under the current version heading, matching the surrounding style:

```markdown
- `fpkg repair-playgo <pkg>` rewrites a package's PlayGo metadata in place to the shape
  LibProsperoPkg 0.6.9 produces, in seconds, without recompressing or copying the payload.
  Packages built with 0.6.8 declared only chunk zero initial while spreading files across every
  chunk, which a console reads as "almost nothing is downloaded". The command regenerates
  `playgo-chunk.dat`, `playgo-ficm.dat` and `playgo-scenario.json`, relays out the CNT body into
  its existing slack, reseals the digest chain and rebuilds the SI segment. Output is
  byte-identical to a 0.6.9 rebuild of the same source. `--dry-run` is the default; `--out` or
  `--in-place` is required to write. It refuses rather than degrading when the SI carries
  `pfsimage.xml`, when the growth exceeds the body slack, when the scenario JSON carries
  presentation regeneration would destroy, or when the package is not the PS5 publisher profile.
```

- [ ] **Step 2: Resolve the spec's "Open" section**

Three questions are listed there. Answer what the work settled and leave the rest open with a note
on what evidence would close it:

- *Does any package in the wild carry `pfsimage.xml`?* — still open; the guard is implemented.
- *Are `publisherNwonly` / `includePublisherLabels` really `true` for an Application volume?* —
  confirmed for this package by gate 2; still unconfirmed for non-Application volumes, which the
  `ENTRY_KEYS.DataSize != 2944` guard now excludes anyway.
- *A package with fewer than 31 languages exercises a different `BuildLanguageChunkLayout` branch.*
  — still untested. Note that it is reachable only through a package whose `playgo-chunk.dat`
  carries a narrower mask, and that no such package is on disk.

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md docs/repair-playgo-spec.md
git commit -m "docs: document fpkg repair-playgo and settle the spec's open questions"
```

---

## Notes for whoever executes this

- **Task 5 is the whole design.** If its fixed point will not go green, stop and say so rather than
  working around it — every later task assumes the reseal is exact, and a reseal that is merely
  close produces a package that verifies and then misbehaves on a console.
- **The 0.6.9 decompile is the authority**, not the upstream source at `~/Developer/LibProsperoPKG`,
  which is the Aug 2026 tree and predates the release this CLI ships against. Use the upstream
  source for orientation; confirm anything load-bearing against the shipped assembly.
- **`repair-playgo` runs against whichever assembly the CLI loads, which is normally the patched
  one.** `LibraryResolver` prefers `LibProsperoPkg.patched.dll` when its stamp is current, so the
  command runs on the patched copy in ordinary use, and the test project does too. The scratch
  probes used to derive the constants in this plan referenced the *unpatched* `LibProsperoPkg.dll`.
  That is harmless today — the patch sites are all source-tree and Oodle paths, none of which touch
  `ProsperoImageDigests`, `PkgWriter`, `ProsperoPlayGo` or the CNT — but it is an assumption, not a
  guarantee. If a future patch site ever lands near any of those, re-derive the constants against
  the patched assembly before trusting them.
- `fpkg api <filter>` prints the public surface of the loaded `LibProsperoPkg`, which is faster than
  guessing at signatures.
- Do not add a padding/no-relayout mode. The spec explains why; it was deliberately dropped.
