# Streaming `.ffpfsc` / `.exfat` Sources Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a PS5 PKG directly from a `.ffpfsc` or `.exfat` container, with no intermediate unpacked file tree, behind an optional dnlib patch.

**Architecture:** A new `FpkgVirtualSource` assembly holds a ported read-only exFAT parser layered on LibProsperoPkg's existing public `PfsReader`/`PFSCReader` stack, plus a process-wide registry keyed by opaque `ffpfsc:<handle>:<path>` tokens. A dnlib patcher rewrites seven sites in `LibProsperoPkg.dll` so those tokens resolve through the registry instead of the filesystem. Patch absent means container sources are unsupported; patch present means they work — exactly the Oodle model already in this repo.

**Tech Stack:** C# / .NET 10, xunit 2.9.2, dnlib 4.4.0, LibProsperoPkg 0.6.4.

**Spec:** `docs/superpowers/specs/2026-09-14-ffpfsc-streaming-source-design.md`

## Global Constraints

- Target framework `net10.0`; `Nullable` and `ImplicitUsings` enabled, matching every existing project in the repo.
- References to `LibProsperoPkg` use the repo's established pattern: a `HintPath` of `$(LibDir)LibProsperoPkg.dll` with `LibDir` defaulting to `../`, `<Private>false</Private>`, and `<AssemblySearchPaths>{HintPathFromItem};{TargetFrameworkDirectory};{RawFileName}</AssemblySearchPaths>`. Omitting the `AssemblySearchPaths` pin lets MSBuild silently resolve a stray `LibProsperoPkg.dll` from elsewhere in the tree — this has already caused a whole afternoon of invalid benchmarks in this repo.
- The virtual path scheme is exactly `ffpfsc:<handle>:<absolute path inside the volume>`, e.g. `ffpfsc:a1b2c3d4:/Media/resources.resource`. `<handle>` is 8 lowercase hex characters.
- Never bundle `LibProsperoPkg.dll` into any output directory; it is resolved from the release folder at runtime.
- Patched methods are located by **pattern** (declaring type + name prefix before `|` + return type + parameter count), never by full compiler-generated name. Ordinals such as `|39_33` shift between upstream releases.
- Every patch site carries a shape assertion that throws before writing anything if the IL does not match. A patcher that cannot recognise a site must fail, never silently skip.
- Commit messages end with the line `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- macOS/arm64 only. Do not add Windows path handling.

---

## File Structure

| path | responsibility |
|---|---|
| `FpkgVirtualSource/FpkgVirtualSource.csproj` | the shim assembly, referenced by nothing at build time; loaded by patched IL at runtime |
| `FpkgVirtualSource/ExfatGeometry.cs` | boot-sector geometry record and its parser |
| `FpkgVirtualSource/ExfatEntry.cs` | one parsed directory entry |
| `FpkgVirtualSource/ExfatReader.cs` | cluster chains, directory walk, positional reads |
| `FpkgVirtualSource/ExfatFileStream.cs` | seekable read-only `Stream` over one exFAT file |
| `FpkgVirtualSource/Container.cs` | opens a `.ffpfsc`/`.exfat`, owns the shared reader and its lock |
| `FpkgVirtualSource/ContainerRegistry.cs` | handle → `Container` |
| `FpkgVirtualSource/VirtualSource.cs` | the static surface the patched IL calls |
| `FpkgVirtualSource.Tests/` | xunit tests, including a synthetic exFAT volume builder |
| `tools/VirtualSourcePatcher/` | dnlib patcher for the seven sites |
| `patch.sh` | applies every available patch to one module |
| `fpkg-cli/Program.cs` | source detection, staging, registry handoff |

---

## Task 1: exFAT geometry parsing

**Files:**
- Create: `FpkgVirtualSource/FpkgVirtualSource.csproj`
- Create: `FpkgVirtualSource/ExfatGeometry.cs`
- Create: `FpkgVirtualSource.Tests/FpkgVirtualSource.Tests.csproj`
- Create: `FpkgVirtualSource.Tests/BootSector.cs`
- Test: `FpkgVirtualSource.Tests/ExfatGeometryTests.cs`

**Interfaces:**
- Consumes: `LibProsperoPkg.Util.IMemoryReader` — `void Read(long pos, byte[] buf, int offset, int count)`.
- Produces: `ExfatGeometry` record with `int BytesPerSector, SectorsPerCluster, long FatOffsetSectors, ClusterHeapOffsetSectors, int ClusterCount, RootDirCluster, uint VolumeSerial`, computed `long ClusterSize`; `static ExfatGeometry Parse(IMemoryReader source)`; `ExfatException : Exception`.

- [ ] **Step 1: Create the two projects**

`FpkgVirtualSource/FpkgVirtualSource.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>FpkgVirtualSource</AssemblyName>
    <RootNamespace>FpkgVirtualSource</RootNamespace>
    <NoWarn>$(NoWarn);NU1701</NoWarn>
  </PropertyGroup>
  <PropertyGroup>
    <LibDir Condition="'$(LibDir)' == ''">../</LibDir>
    <AssemblySearchPaths>{HintPathFromItem};{TargetFrameworkDirectory};{RawFileName}</AssemblySearchPaths>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="LibProsperoPkg">
      <HintPath>$(LibDir)LibProsperoPkg.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

`FpkgVirtualSource.Tests/FpkgVirtualSource.Tests.csproj`:

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
    <ProjectReference Include="../FpkgVirtualSource/FpkgVirtualSource.csproj" />
  </ItemGroup>
</Project>
```

Copy `PprPfsKrakenTool.Tests/ReleaseFolder.cs` and `TestResolver.cs` into `FpkgVirtualSource.Tests/`, deleting the `NativeLibrary.SetDllImportResolver` block from `TestResolver.Install` — there is no native shim here.

- [ ] **Step 2: Write the test fixture helper**

`FpkgVirtualSource.Tests/BootSector.cs`:

```csharp
using LibProsperoPkg.Util;

namespace FpkgVirtualSource.Tests;

/// <summary>An IMemoryReader over a byte array, for tests.</summary>
internal sealed class ByteSource(byte[] bytes) : IMemoryReader
{
    public byte[] Bytes { get; } = bytes;
    public void Read(long pos, byte[] buf, int offset, int count) =>
        Array.Copy(Bytes, pos, buf, offset, count);
    public void Dispose() { }
}

internal static class BootSector
{
    /// <summary>A 512-byte exFAT main boot sector with the given geometry.</summary>
    internal static byte[] Build(
        uint fatOffsetSectors = 128, uint fatLengthSectors = 64,
        uint heapOffsetSectors = 256, uint clusterCount = 1024,
        uint rootCluster = 2, uint serial = 0xDEADBEEF,
        byte bytesPerSectorShift = 9, byte sectorsPerClusterShift = 7)
    {
        var v = new byte[512];
        "EXFAT   "u8.CopyTo(v.AsSpan(3, 8));
        BitConverter.GetBytes(fatOffsetSectors).CopyTo(v, 0x50);
        BitConverter.GetBytes(fatLengthSectors).CopyTo(v, 0x54);
        BitConverter.GetBytes(heapOffsetSectors).CopyTo(v, 0x58);
        BitConverter.GetBytes(clusterCount).CopyTo(v, 0x5C);
        BitConverter.GetBytes(rootCluster).CopyTo(v, 0x60);
        BitConverter.GetBytes(serial).CopyTo(v, 0x64);
        v[0x6C] = bytesPerSectorShift;
        v[0x6D] = sectorsPerClusterShift;
        v[0x1FE] = 0x55; v[0x1FF] = 0xAA;
        return v;
    }
}
```

- [ ] **Step 3: Write the failing test**

`FpkgVirtualSource.Tests/ExfatGeometryTests.cs`:

```csharp
using LibProsperoPkg.Util;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class ExfatGeometryTests
{
    private static ExfatGeometry Parse(byte[] vbr) => ExfatGeometry.Parse(new ByteSource(vbr));

    [Fact]
    public void ParsesShiftsIntoSizes()
    {
        var g = Parse(BootSector.Build(bytesPerSectorShift: 9, sectorsPerClusterShift: 7));
        Assert.Equal(512, g.BytesPerSector);
        Assert.Equal(128, g.SectorsPerCluster);
        Assert.Equal(65536, g.ClusterSize);
    }

    [Fact]
    public void ParsesLayoutFields()
    {
        var g = Parse(BootSector.Build(fatOffsetSectors: 128, heapOffsetSectors: 256,
                                       clusterCount: 1024, rootCluster: 5, serial: 0xDEADBEEF));
        Assert.Equal(128, g.FatOffsetSectors);
        Assert.Equal(256, g.ClusterHeapOffsetSectors);
        Assert.Equal(1024, g.ClusterCount);
        Assert.Equal(5, g.RootDirCluster);
        Assert.Equal(0xDEADBEEFu, g.VolumeSerial);
    }

    [Fact]
    public void RejectsMissingSignature()
    {
        var v = BootSector.Build();
        v[3] = (byte)'X';
        Assert.Throws<ExfatException>(() => Parse(v));
    }

    [Fact]
    public void RejectsMissingBootSignature()
    {
        var v = BootSector.Build();
        v[0x1FE] = 0;
        Assert.Throws<ExfatException>(() => Parse(v));
    }

    [Theory]
    [InlineData((byte)8)]
    [InlineData((byte)13)]
    public void RejectsUnsupportedSectorShift(byte shift)
    {
        Assert.Throws<ExfatException>(() => Parse(BootSector.Build(bytesPerSectorShift: shift)));
    }
}
```

- [ ] **Step 4: Run the test and confirm it fails**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: FAIL — `ExfatGeometry` does not exist.

- [ ] **Step 5: Implement**

`FpkgVirtualSource/ExfatGeometry.cs`:

```csharp
using System.Buffers.Binary;
using LibProsperoPkg.Util;

namespace FpkgVirtualSource;

/// <summary>Thrown when a volume is malformed or uses exFAT features this reader does not cover.</summary>
public sealed class ExfatException(string message) : Exception(message);

/// <summary>Volume geometry from the exFAT main boot sector.</summary>
public sealed record ExfatGeometry(
    int BytesPerSector,
    int SectorsPerCluster,
    long FatOffsetSectors,
    long ClusterHeapOffsetSectors,
    int ClusterCount,
    int RootDirCluster,
    uint VolumeSerial)
{
    public long ClusterSize => (long)BytesPerSector * SectorsPerCluster;

    private static ReadOnlySpan<byte> Signature => "EXFAT   "u8;

    public static ExfatGeometry Parse(IMemoryReader source)
    {
        var vbr = new byte[512];
        try { source.Read(0, vbr, 0, vbr.Length); }
        catch (Exception ex) { throw new ExfatException("source too small for an exFAT boot sector: " + ex.Message); }

        if (!vbr.AsSpan(3, 8).SequenceEqual(Signature))
            throw new ExfatException("missing exFAT file system signature");
        if (BinaryPrimitives.ReadUInt16LittleEndian(vbr.AsSpan(0x1FE)) != 0xAA55)
            throw new ExfatException("missing boot signature 0xAA55");

        byte bytesPerSectorShift = vbr[0x6C];
        byte sectorsPerClusterShift = vbr[0x6D];
        if (bytesPerSectorShift is < 9 or > 12)
            throw new ExfatException($"unsupported bytes-per-sector shift {bytesPerSectorShift}");
        if (sectorsPerClusterShift > 25 - bytesPerSectorShift)
            throw new ExfatException($"unsupported sectors-per-cluster shift {sectorsPerClusterShift}");

        return new ExfatGeometry(
            BytesPerSector: 1 << bytesPerSectorShift,
            SectorsPerCluster: 1 << sectorsPerClusterShift,
            FatOffsetSectors: BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x50)),
            ClusterHeapOffsetSectors: BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x58)),
            ClusterCount: checked((int)BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x5C))),
            RootDirCluster: checked((int)BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x60))),
            VolumeSerial: BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x64)));
    }
}
```

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: PASS, 6 tests.

- [ ] **Step 7: Commit**

```bash
git add FpkgVirtualSource FpkgVirtualSource.Tests
git commit -m "$(printf 'exFAT geometry parsing\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 2: Cluster chain iteration

**Files:**
- Create: `FpkgVirtualSource/ExfatReader.cs`
- Test: `FpkgVirtualSource.Tests/ExfatClusterTests.cs`

**Interfaces:**
- Consumes: `ExfatGeometry`, `ExfatException` from Task 1.
- Produces: `ExfatReader(IMemoryReader source)` with `ExfatGeometry Geometry { get; }`, `internal long ClusterByteOffset(int cluster)`, `internal IEnumerable<int> IterateClusters(int firstCluster, bool noFatChain, long length)`.

`length <= 0` means "follow the FAT to end of chain", which is how directories without a recorded length (notably the root) are walked.

- [ ] **Step 1: Write the failing test**

`FpkgVirtualSource.Tests/ExfatClusterTests.cs`:

```csharp
using Xunit;

namespace FpkgVirtualSource.Tests;

public class ExfatClusterTests
{
    // 512-byte sectors, 128 sectors/cluster => 64 KiB clusters. FAT at sector 128.
    private const int ClusterSize = 65536;
    private const long FatByteOffset = 128 * 512;

    private static ByteSource Volume(params (int cluster, uint next)[] fat)
    {
        var bytes = new byte[4 * 1024 * 1024];
        BootSector.Build().CopyTo(bytes, 0);
        foreach (var (cluster, next) in fat)
            BitConverter.GetBytes(next).CopyTo(bytes, FatByteOffset + cluster * 4);
        return new ByteSource(bytes);
    }

    [Fact]
    public void ContiguousAllocationNeedsNoFat()
    {
        var r = new ExfatReader(Volume());
        Assert.Equal(new[] { 10, 11, 12 },
            r.IterateClusters(10, noFatChain: true, length: 3 * ClusterSize).ToArray());
    }

    [Fact]
    public void ContiguousAllocationRoundsPartialClusterUp()
    {
        var r = new ExfatReader(Volume());
        Assert.Equal(new[] { 10, 11 },
            r.IterateClusters(10, noFatChain: true, length: ClusterSize + 1).ToArray());
    }

    [Fact]
    public void FollowsFatChainToEndMarker()
    {
        var r = new ExfatReader(Volume((10, 11), (11, 20), (20, 0xFFFFFFFF)));
        Assert.Equal(new[] { 10, 11, 20 },
            r.IterateClusters(10, noFatChain: false, length: 0).ToArray());
    }

    [Fact]
    public void StopsAtRecordedLengthEvenIfChainContinues()
    {
        var r = new ExfatReader(Volume((10, 11), (11, 20), (20, 0xFFFFFFFF)));
        Assert.Equal(new[] { 10, 11 },
            r.IterateClusters(10, noFatChain: false, length: 2 * ClusterSize).ToArray());
    }

    [Fact]
    public void EmptyAllocationYieldsNothing()
    {
        var r = new ExfatReader(Volume());
        Assert.Empty(r.IterateClusters(0, noFatChain: false, length: 0));
        Assert.Empty(r.IterateClusters(10, noFatChain: true, length: 0));
    }

    [Fact]
    public void DetectsChainLoop()
    {
        // 10 -> 11 -> 10 -> ... with no length bound.
        var r = new ExfatReader(Volume((10, 11), (11, 10)));
        Assert.Throws<ExfatException>(() =>
            r.IterateClusters(10, noFatChain: false, length: 0).ToArray());
    }

    [Fact]
    public void ClusterOffsetIsHeapRelativeFromClusterTwo()
    {
        var r = new ExfatReader(Volume());
        // heap at sector 256, 512-byte sectors, 64 KiB clusters; cluster 2 is the first.
        Assert.Equal(256L * 512, r.ClusterByteOffset(2));
        Assert.Equal(256L * 512 + ClusterSize, r.ClusterByteOffset(3));
    }
}
```

- [ ] **Step 2: Run the test and confirm it fails**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: FAIL — `ExfatReader` does not exist.

- [ ] **Step 3: Implement**

`FpkgVirtualSource/ExfatReader.cs`:

```csharp
using System.Buffers.Binary;
using LibProsperoPkg.Util;

namespace FpkgVirtualSource;

/// <summary>
/// Read-only exFAT parser over any <see cref="IMemoryReader"/>. Covers the subset game
/// images use: single FAT, standard directory entry sets, contiguous or FAT-chained
/// allocation. Ported from MkPFS's mkpfs/exfat.py.
/// </summary>
public sealed partial class ExfatReader
{
    private const uint FatEndOfChain = 0xFFFFFFFF;

    private readonly IMemoryReader _source;
    private readonly byte[] _fatEntry = new byte[4];

    public ExfatReader(IMemoryReader source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Geometry = ExfatGeometry.Parse(source);
    }

    public ExfatGeometry Geometry { get; }

    internal long ClusterByteOffset(int cluster) =>
        (Geometry.ClusterHeapOffsetSectors + (long)(cluster - 2) * Geometry.SectorsPerCluster)
        * Geometry.BytesPerSector;

    private uint FatNext(int cluster)
    {
        long offset = Geometry.FatOffsetSectors * Geometry.BytesPerSector + (long)cluster * 4;
        lock (_fatEntry)
        {
            _source.Read(offset, _fatEntry, 0, 4);
            return BinaryPrimitives.ReadUInt32LittleEndian(_fatEntry);
        }
    }

    /// <summary>
    /// Cluster numbers for one allocation. <paramref name="length"/> of zero or less means
    /// "follow the FAT to the end of the chain", which is how the root directory is walked.
    /// </summary>
    internal IEnumerable<int> IterateClusters(int firstCluster, bool noFatChain, long length)
    {
        if (firstCluster < 2) yield break;
        long clusterSize = Geometry.ClusterSize;

        if (noFatChain)
        {
            if (length <= 0) yield break;
            long count = (length + clusterSize - 1) / clusterSize;
            for (long i = 0; i < count; i++) yield return checked(firstCluster + (int)i);
            yield break;
        }

        long remaining = length > 0 ? (length + clusterSize - 1) / clusterSize : -1;
        int cluster = firstCluster;
        long seen = 0;
        while (cluster >= 2 && (uint)cluster < FatEndOfChain)
        {
            yield return cluster;
            seen++;
            if (remaining > 0 && seen >= remaining) yield break;
            if (seen > (long)Geometry.ClusterCount + 2)
                throw new ExfatException("cluster chain exceeds volume size (loop?)");
            cluster = unchecked((int)FatNext(cluster));
        }
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add FpkgVirtualSource FpkgVirtualSource.Tests
git commit -m "$(printf 'exFAT cluster chain iteration\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 3: Directory walk

**Files:**
- Create: `FpkgVirtualSource/ExfatEntry.cs`
- Create: `FpkgVirtualSource/ExfatReader.Directory.cs`
- Create: `FpkgVirtualSource.Tests/TestVolume.cs`
- Test: `FpkgVirtualSource.Tests/ExfatDirectoryTests.cs`

**Interfaces:**
- Consumes: `ExfatReader.IterateClusters`, `ExfatReader.ClusterByteOffset` from Task 2.
- Produces: `ExfatEntry` with `string Name, RelativePath; bool IsDirectory; int FirstCluster; long Length; bool NoFatChain; List<ExfatEntry> Children`; `IReadOnlyList<ExfatEntry> ExfatReader.RootEntries()`; `IEnumerable<ExfatEntry> ExfatReader.EnumerateFiles()` in `RelativePath` order, case-insensitive.

- [ ] **Step 1: Write the synthetic volume builder**

`FpkgVirtualSource.Tests/TestVolume.cs`:

```csharp
using System.Text;

namespace FpkgVirtualSource.Tests;

/// <summary>
/// Builds a minimal but structurally valid exFAT volume in memory: 512-byte sectors,
/// 64 KiB clusters, FAT at sector 128, cluster heap at sector 256, root at cluster 2.
/// Every file is allocated contiguously (NoFatChain), which is what MkPFS emits.
/// </summary>
internal sealed class TestVolume
{
    private const int SectorSize = 512;
    private const int ClusterSize = 65536;
    private const int FatSector = 128;
    private const int HeapSector = 256;
    private const int RootCluster = 2;
    private const int MaxClusters = 512;

    private readonly byte[] _bytes = new byte[HeapSector * SectorSize + MaxClusters * ClusterSize];
    private readonly List<(string Path, byte[] Data)> _files = [];

    internal TestVolume Add(string path, byte[] data) { _files.Add((path, data)); return this; }

    /// <summary>Lays the volume out and returns it. Directories are inferred from paths.</summary>
    internal ByteSource Build()
    {
        BootSector.Build(fatOffsetSectors: FatSector, heapOffsetSectors: HeapSector,
                         clusterCount: MaxClusters, rootCluster: RootCluster).CopyTo(_bytes, 0);

        // Group by parent directory. One directory level is enough for the reader's contract.
        var byDir = _files.GroupBy(f => f.Path.Contains('/') ? f.Path[..f.Path.LastIndexOf('/')] : "")
                          .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        int nextCluster = RootCluster + 1;   // cluster 2 is the root directory itself
        var dirEntries = new Dictionary<string, List<byte[]>> { [""] = [] };
        foreach (var group in byDir) dirEntries.TryAdd(group.Key, []);

        foreach (var group in byDir)
        {
            foreach (var (path, data) in group)
            {
                int first = data.Length == 0 ? 0 : nextCluster;
                if (data.Length > 0)
                {
                    data.CopyTo(_bytes, ClusterOffset(nextCluster));
                    nextCluster += (data.Length + ClusterSize - 1) / ClusterSize;
                }
                string name = path[(path.LastIndexOf('/') + 1)..];
                dirEntries[group.Key].AddRange(EntrySet(name, isDir: false, first, data.Length));
            }
        }

        // Subdirectories get a cluster each, then are referenced from the root.
        foreach (var group in byDir.Where(g => g.Key.Length > 0))
        {
            int dirCluster = nextCluster++;
            int off = ClusterOffset(dirCluster);
            foreach (var e in dirEntries[group.Key]) { e.CopyTo(_bytes, off); off += 32; }
            dirEntries[""].AddRange(EntrySet(group.Key, isDir: true, dirCluster, ClusterSize));
        }

        int rootOff = ClusterOffset(RootCluster);
        foreach (var e in dirEntries[""]) { e.CopyTo(_bytes, rootOff); rootOff += 32; }
        return new ByteSource(_bytes);
    }

    private static int ClusterOffset(int cluster) =>
        HeapSector * SectorSize + (cluster - 2) * ClusterSize;

    /// <summary>File entry + stream extension + as many file-name entries as the name needs.</summary>
    private static List<byte[]> EntrySet(string name, bool isDir, int firstCluster, long length)
    {
        var units = Encoding.Unicode.GetBytes(name);
        int nameEntries = (name.Length + 14) / 15;

        var file = new byte[32];
        file[0] = 0x85;
        file[1] = (byte)(1 + nameEntries);
        BitConverter.GetBytes((ushort)(isDir ? 0x10 : 0x20)).CopyTo(file, 0x04);

        var stream = new byte[32];
        stream[0] = 0xC0;
        stream[1] = 0x02;                       // NoFatChain
        stream[3] = (byte)name.Length;
        BitConverter.GetBytes(length).CopyTo(stream, 0x08);   // ValidDataLength
        BitConverter.GetBytes(firstCluster).CopyTo(stream, 0x14);
        BitConverter.GetBytes(length).CopyTo(stream, 0x18);   // DataLength

        var set = new List<byte[]> { file, stream };
        for (int i = 0; i < nameEntries; i++)
        {
            var n = new byte[32];
            n[0] = 0xC1;
            int start = i * 30, len = Math.Min(30, units.Length - start);
            if (len > 0) Array.Copy(units, start, n, 2, len);
            set.Add(n);
        }
        return set;
    }
}
```

- [ ] **Step 2: Write the failing test**

`FpkgVirtualSource.Tests/ExfatDirectoryTests.cs`:

```csharp
using System.Text;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class ExfatDirectoryTests
{
    private static ExfatReader Reader() => new(new TestVolume()
        .Add("eboot.bin", Encoding.ASCII.GetBytes("ELF-ish payload"))
        .Add("sce_sys/param.json", Encoding.ASCII.GetBytes("{}"))
        .Add("Media/resources.resource", new byte[ 70000 ])
        .Build());

    [Fact]
    public void FindsTopLevelFiles()
    {
        var names = Reader().RootEntries().Where(e => !e.IsDirectory).Select(e => e.Name);
        Assert.Contains("eboot.bin", names);
    }

    [Fact]
    public void FindsSubdirectoriesAndRecursesIntoThem()
    {
        var root = Reader().RootEntries();
        var sceSys = Assert.Single(root.Where(e => e.Name == "sce_sys"));
        Assert.True(sceSys.IsDirectory);
        Assert.Contains("param.json", sceSys.Children.Select(c => c.Name));
    }

    [Fact]
    public void BuildsPosixRelativePaths()
    {
        var paths = Reader().EnumerateFiles().Select(e => e.RelativePath).ToList();
        Assert.Contains("sce_sys/param.json", paths);
        Assert.Contains("Media/resources.resource", paths);
        Assert.Contains("eboot.bin", paths);
    }

    [Fact]
    public void RecordsLengthAndContiguityFromTheStreamExtension()
    {
        var f = Reader().EnumerateFiles().Single(e => e.RelativePath == "Media/resources.resource");
        Assert.Equal(70000, f.Length);
        Assert.True(f.NoFatChain);
        Assert.True(f.FirstCluster >= 2);
    }

    [Fact]
    public void EnumerationOrderIsDeterministicAndCaseInsensitive()
    {
        var a = Reader().EnumerateFiles().Select(e => e.RelativePath).ToList();
        var b = Reader().EnumerateFiles().Select(e => e.RelativePath).ToList();
        Assert.Equal(a, b);
        Assert.Equal(a.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), a);
    }

    [Fact]
    public void HandlesNamesLongerThanOneNameEntry()
    {
        var longName = new string('x', 40) + ".dat";
        var r = new ExfatReader(new TestVolume().Add(longName, [1, 2, 3]).Build());
        Assert.Equal(longName, r.EnumerateFiles().Single().Name);
    }
}
```

- [ ] **Step 3: Run the test and confirm it fails**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: FAIL — `RootEntries` does not exist.

- [ ] **Step 4: Implement the entry type**

`FpkgVirtualSource/ExfatEntry.cs`:

```csharp
namespace FpkgVirtualSource;

/// <summary>One file or directory parsed from an exFAT directory.</summary>
public sealed class ExfatEntry
{
    public required string Name { get; init; }
    /// <summary>POSIX-style path relative to the volume root, no leading slash.</summary>
    public required string RelativePath { get; init; }
    public required bool IsDirectory { get; init; }
    /// <summary>First cluster of the entry's data; 0 when empty.</summary>
    public required int FirstCluster { get; init; }
    public required long Length { get; init; }
    /// <summary>True when the allocation is contiguous and the FAT need not be consulted.</summary>
    public required bool NoFatChain { get; init; }
    public List<ExfatEntry> Children { get; } = [];
}
```

- [ ] **Step 5: Implement the walk**

`FpkgVirtualSource/ExfatReader.Directory.cs`:

```csharp
using System.Buffers.Binary;
using System.Text;

namespace FpkgVirtualSource;

public sealed partial class ExfatReader
{
    private const byte EntryEndOfDirectory = 0x00;
    private const byte EntryFile = 0x85;
    private const byte EntryStreamExtension = 0xC0;
    private const byte EntryFileName = 0xC1;
    private const byte AttrDirectory = 0x10;
    private const byte SecondaryFlagNoFatChain = 0x02;

    /// <summary>The directory tree rooted at the volume root.</summary>
    public IReadOnlyList<ExfatEntry> RootEntries() =>
        WalkDirectory(Geometry.RootDirCluster, noFatChain: false, length: 0, relativeDir: "");

    /// <summary>Every file (not directory) in the volume, ordered by path, case-insensitively.</summary>
    public IEnumerable<ExfatEntry> EnumerateFiles()
    {
        static IEnumerable<ExfatEntry> Walk(IReadOnlyList<ExfatEntry> nodes)
        {
            foreach (var n in nodes.OrderBy(n => n.RelativePath, StringComparer.OrdinalIgnoreCase))
                if (n.IsDirectory) foreach (var c in Walk(n.Children)) yield return c;
                else yield return n;
        }
        return Walk(RootEntries());
    }

    private List<byte[]> ReadDirectoryEntries(int firstCluster, bool noFatChain, long length)
    {
        var raw = new List<byte[]>();
        var cluster = new byte[Geometry.ClusterSize];
        foreach (int c in IterateClusters(firstCluster, noFatChain, length))
        {
            _source.Read(ClusterByteOffset(c), cluster, 0, cluster.Length);
            for (int off = 0; off + 32 <= cluster.Length; off += 32)
            {
                if (cluster[off] == EntryEndOfDirectory) return raw;
                raw.Add(cluster.AsSpan(off, 32).ToArray());
            }
        }
        return raw;
    }

    private List<ExfatEntry> WalkDirectory(int firstCluster, bool noFatChain, long length, string relativeDir)
    {
        var entries = new List<ExfatEntry>();
        var raw = ReadDirectoryEntries(firstCluster, noFatChain, length);

        for (int i = 0; i < raw.Count; )
        {
            byte[] entry = raw[i];
            if (entry[0] != EntryFile) { i++; continue; }

            int secondaryCount = entry[1];
            ushort attrs = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(0x04));
            var secondaries = raw.Skip(i + 1).Take(secondaryCount).ToList();
            i += 1 + secondaryCount;
            if (secondaries.Count < secondaryCount || secondaries.Count == 0) continue;

            byte[] stream = secondaries[0];
            if (stream[0] != EntryStreamExtension) continue;

            int nameLength = stream[3];
            long dataLength = BinaryPrimitives.ReadInt64LittleEndian(stream.AsSpan(0x18));
            int childCluster = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(0x14));
            bool childNoFat = (stream[1] & SecondaryFlagNoFatChain) != 0;

            var units = new List<byte>();
            foreach (byte[] s in secondaries.Skip(1))
                if (s[0] == EntryFileName) units.AddRange(s.AsSpan(2, 30).ToArray());
            string decoded = Encoding.Unicode.GetString(units.ToArray());
            string name = decoded.Length > nameLength ? decoded[..nameLength] : decoded;
            if (name.Length == 0) continue;

            bool isDir = (attrs & AttrDirectory) != 0;
            string relativePath = relativeDir.Length == 0 ? name : relativeDir + "/" + name;
            var node = new ExfatEntry
            {
                Name = name,
                RelativePath = relativePath,
                IsDirectory = isDir,
                FirstCluster = childCluster,
                Length = dataLength,
                NoFatChain = childNoFat,
            };
            if (isDir)
                node.Children.AddRange(WalkDirectory(childCluster, childNoFat, dataLength, relativePath));
            entries.Add(node);
        }
        return entries;
    }
}
```

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: PASS, 19 tests.

- [ ] **Step 7: Commit**

```bash
git add FpkgVirtualSource FpkgVirtualSource.Tests
git commit -m "$(printf 'exFAT directory walk\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 4: Seekable read-only stream over one exFAT file

**Files:**
- Create: `FpkgVirtualSource/ExfatFileStream.cs`
- Modify: `FpkgVirtualSource/ExfatReader.Directory.cs` (add `OpenFile`)
- Test: `FpkgVirtualSource.Tests/ExfatFileStreamTests.cs`

**Interfaces:**
- Consumes: `ExfatEntry`, `ExfatReader.IterateClusters`, `ExfatReader.ClusterByteOffset`.
- Produces: `Stream ExfatReader.OpenFile(ExfatEntry entry, object gate)`. `gate` is the lock every read on the shared source takes; the caller owns it. The returned stream is seekable, `CanWrite == false`, `Length == entry.Length`.

The cluster list is materialised once at open. A 16 GB file in 64 KiB clusters is 256k ints (1 MB) — acceptable, and it makes `Seek` O(1).

- [ ] **Step 1: Write the failing test**

`FpkgVirtualSource.Tests/ExfatFileStreamTests.cs`:

```csharp
using System.Security.Cryptography;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class ExfatFileStreamTests
{
    private static readonly byte[] Payload = Make(200_000);

    private static byte[] Make(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 + (i >> 11));
        return b;
    }

    private static Stream Open(out object gate)
    {
        var r = new ExfatReader(new TestVolume().Add("data/big.bin", Payload).Build());
        gate = new object();
        return r.OpenFile(r.EnumerateFiles().Single(), gate);
    }

    [Fact]
    public void ReportsLengthAndCapabilities()
    {
        using var s = Open(out _);
        Assert.Equal(Payload.Length, s.Length);
        Assert.True(s.CanRead);
        Assert.True(s.CanSeek);
        Assert.False(s.CanWrite);
    }

    [Fact]
    public void SequentialReadReturnsExactBytes()
    {
        using var s = Open(out _);
        var got = new MemoryStream();
        s.CopyTo(got, 4096);
        Assert.Equal(Payload, got.ToArray());
    }

    [Fact]
    public void ReadSpansClusterBoundaries()
    {
        using var s = Open(out _);
        s.Position = 65536 - 10;               // straddle the first cluster boundary
        var buf = new byte[20];
        s.ReadExactly(buf);
        Assert.Equal(Payload.AsSpan(65526, 20).ToArray(), buf);
    }

    [Fact]
    public void SeekFromEndAndCurrentWork()
    {
        using var s = Open(out _);
        s.Seek(-100, SeekOrigin.End);
        var tail = new byte[100];
        s.ReadExactly(tail);
        Assert.Equal(Payload.AsSpan(Payload.Length - 100, 100).ToArray(), tail);

        s.Position = 0;
        s.Seek(50, SeekOrigin.Current);
        Assert.Equal(50, s.Position);
    }

    [Fact]
    public void ReadPastEndReturnsZero()
    {
        using var s = Open(out _);
        s.Position = s.Length;
        Assert.Equal(0, s.Read(new byte[16], 0, 16));
    }

    [Fact]
    public void WritesAreRejected()
    {
        using var s = Open(out _);
        Assert.Throws<NotSupportedException>(() => s.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => s.SetLength(0));
    }

    [Fact]
    public void ConcurrentReadersOverOneSourceAgreeWithSequential()
    {
        var r = new ExfatReader(new TestVolume().Add("data/big.bin", Payload).Build());
        object gate = new();
        var entry = r.EnumerateFiles().Single();
        string expected = Convert.ToHexString(SHA256.HashData(Payload));

        Parallel.For(0, 8, _ =>
        {
            using var s = r.OpenFile(entry, gate);
            using var ms = new MemoryStream();
            s.CopyTo(ms, 8192);
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(ms.ToArray())));
        });
    }
}
```

- [ ] **Step 2: Run the test and confirm it fails**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: FAIL — `OpenFile` does not exist.

- [ ] **Step 3: Implement the stream**

`FpkgVirtualSource/ExfatFileStream.cs`:

```csharp
using LibProsperoPkg.Util;

namespace FpkgVirtualSource;

/// <summary>
/// A seekable, read-only view of one exFAT file. Every read of the underlying source is
/// taken under <c>gate</c>, which the owning container shares across all its streams:
/// PFSCReader keeps a single decode buffer and is not reentrant.
/// </summary>
internal sealed class ExfatFileStream : Stream
{
    private readonly IMemoryReader _source;
    private readonly object _gate;
    private readonly int[] _clusters;
    private readonly long _clusterSize;
    private readonly Func<int, long> _clusterOffset;
    private long _position;

    internal ExfatFileStream(IMemoryReader source, object gate, int[] clusters,
                             long clusterSize, long length, Func<int, long> clusterOffset)
    {
        _source = source;
        _gate = gate;
        _clusters = clusters;
        _clusterSize = clusterSize;
        _clusterOffset = clusterOffset;
        Length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = value < 0
            ? throw new ArgumentOutOfRangeException(nameof(value))
            : value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0 || _position >= Length) return 0;

        int total = 0;
        while (count > 0 && _position < Length)
        {
            long index = _position / _clusterSize;
            if (index >= _clusters.Length) break;
            long within = _position % _clusterSize;
            int chunk = (int)Math.Min(Math.Min(count, _clusterSize - within), Length - _position);

            lock (_gate)
                _source.Read(_clusterOffset(_clusters[(int)index]) + within, buffer, offset, chunk);

            offset += chunk; count -= chunk; total += chunk; _position += chunk;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("cannot seek before the start of the file");
        return _position = target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

- [ ] **Step 4: Add `OpenFile` to the reader**

Append to `FpkgVirtualSource/ExfatReader.Directory.cs`, inside the `ExfatReader` class:

```csharp
    /// <summary>
    /// Opens a seekable, read-only view of <paramref name="entry"/>. Reads of the shared
    /// source are serialised on <paramref name="gate"/>, which the container owns.
    /// </summary>
    public Stream OpenFile(ExfatEntry entry, object gate)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(gate);
        if (entry.IsDirectory) throw new ExfatException("not a file: " + entry.RelativePath);

        int[] clusters = IterateClusters(entry.FirstCluster, entry.NoFatChain, entry.Length).ToArray();
        long available = clusters.Length * Geometry.ClusterSize;
        if (available < entry.Length)
            throw new ExfatException(
                $"file '{entry.RelativePath}' is truncated: {entry.Length - available} bytes short");

        return new ExfatFileStream(_source, gate, clusters, Geometry.ClusterSize,
                                   entry.Length, ClusterByteOffset);
    }
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: PASS, 26 tests.

- [ ] **Step 6: Commit**

```bash
git add FpkgVirtualSource FpkgVirtualSource.Tests
git commit -m "$(printf 'seekable read-only stream over an exFAT file\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 5: Container open, registry and the `VirtualSource` surface

**Files:**
- Create: `FpkgVirtualSource/Container.cs`
- Create: `FpkgVirtualSource/ContainerRegistry.cs`
- Create: `FpkgVirtualSource/VirtualSource.cs`
- Test: `FpkgVirtualSource.Tests/VirtualSourceTests.cs`
- Test: `FpkgVirtualSource.Tests/ContainerIntegrationTests.cs`

**Interfaces:**
- Consumes: `ExfatReader`, `ExfatEntry` from Tasks 1–4; `LibProsperoPkg.PFS.PfsReader`, `PFSCReader`, `InodeFlags`, `LibProsperoPkg.Util.StreamReader`.
- Produces, and every later task depends on these exact signatures:

```csharp
public readonly record struct VirtualEntry(string RelativePath, bool IsDirectory, long Length);

public static class VirtualSource
{
    public static bool   IsVirtual(string? path);
    public static Stream Open(string path);
    public static Stream OpenOrFile(string path);          // virtual, else a real FileStream
    public static IEnumerable<VirtualEntry> Enumerate(string virtualRoot);
    public static string Root(string handle);              // "ffpfsc:<handle>:/"
    public static string PathFor(string handle, string relativePath);
}

public static class ContainerRegistry
{
    public static string Register(Container container);    // returns the 8-hex handle
    public static Container Get(string handle);
    public static void Release(string handle);
}

public sealed class Container : IDisposable
{
    public static Container Open(string path);             // .ffpfsc, .exfat, or raw PFS
    public ExfatReader Reader { get; }
    public object Gate { get; }
    public IReadOnlyList<ExfatEntry> Files { get; }
    public string? FindAppRoot();                          // dir holding sce_sys/param.json, or null
}
```

- [ ] **Step 1: Write the failing unit test**

`FpkgVirtualSource.Tests/VirtualSourceTests.cs`:

```csharp
using System.Text;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class VirtualSourceTests
{
    [Theory]
    [InlineData("ffpfsc:a1b2c3d4:/Media/x.bin", true)]
    [InlineData("/Users/me/game/eboot.bin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognisesVirtualPaths(string? path, bool expected) =>
        Assert.Equal(expected, VirtualSource.IsVirtual(path));

    [Fact]
    public void ComposesAndParsesPaths()
    {
        string p = VirtualSource.PathFor("a1b2c3d4", "Media/x.bin");
        Assert.Equal("ffpfsc:a1b2c3d4:/Media/x.bin", p);
        Assert.True(VirtualSource.IsVirtual(p));
        Assert.Equal("ffpfsc:a1b2c3d4:/", VirtualSource.Root("a1b2c3d4"));
    }

    [Fact]
    public void OpenOrFileFallsBackToTheRealFilesystem()
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "hello");
            using var s = VirtualSource.OpenOrFile(tmp);
            using var r = new StreamReader(s);
            Assert.Equal("hello", r.ReadToEnd());
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void UnknownHandleFailsLoudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VirtualSource.Open("ffpfsc:deadbeef:/x"));
        Assert.Contains("deadbeef", ex.Message);
    }
}
```

- [ ] **Step 2: Run the test and confirm it fails**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: FAIL — `VirtualSource` does not exist.

- [ ] **Step 3: Implement the container**

`FpkgVirtualSource/Container.cs`:

```csharp
using System.Buffers.Binary;
using LibProsperoPkg.PFS;
using LibProsperoPkg.Util;
using StreamReader = LibProsperoPkg.Util.StreamReader;

namespace FpkgVirtualSource;

/// <summary>
/// One open source container. Layers, outermost first: the file, an optional PFS wrapper,
/// an optional PFSC decompressor, then the exFAT volume. All reads of the innermost source
/// go through <see cref="Gate"/> because PFSCReader holds one decode buffer.
/// </summary>
public sealed class Container : IDisposable
{
    private const long PfsVersion2 = 2;
    private const long PfsMagic = 0x1332A0B;

    private readonly List<IDisposable> _own = [];
    private bool _disposed;

    private Container(ExfatReader reader, string path)
    {
        Reader = reader;
        ContainerPath = path;
        Files = reader.EnumerateFiles().ToList();
    }

    /// <summary>Named to avoid shadowing System.IO.Path inside this class.</summary>
    public string ContainerPath { get; }
    public ExfatReader Reader { get; }
    public object Gate { get; } = new();
    public IReadOnlyList<ExfatEntry> Files { get; }

    /// <summary>Opens a .ffpfsc, a raw PFS image, or a bare .exfat, detected by header.</summary>
    public static Container Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                1 << 20, FileOptions.RandomAccess);
        var own = new List<IDisposable> { fs };
        try
        {
            IMemoryReader source = new StreamReader(fs);
            if (LooksLikePfs(fs))
            {
                var pfs = new PfsReader(source);
                var inner = pfs.GetAllFiles().OrderByDescending(f => f.size).FirstOrDefault()
                    ?? throw new ExfatException($"{path}: PFS container holds no files");
                IMemoryReader view = inner.GetView();
                if ((inner.flags & InodeFlags.compressed) != 0)
                {
                    var pfsc = new PFSCReader(view);
                    own.Add(pfsc);
                    view = pfsc;
                }
                source = view;
            }
            var container = new Container(new ExfatReader(source), path);
            container._own.AddRange(own);
            return container;
        }
        catch
        {
            for (int i = own.Count - 1; i >= 0; i--) own[i].Dispose();
            throw;
        }
    }

    private static bool LooksLikePfs(FileStream fs)
    {
        Span<byte> head = stackalloc byte[16];
        long saved = fs.Position;
        try
        {
            fs.Position = 0;
            if (fs.Read(head) != head.Length) return false;
            return BinaryPrimitives.ReadInt64LittleEndian(head) == PfsVersion2
                && BinaryPrimitives.ReadInt64LittleEndian(head[8..]) == PfsMagic;
        }
        finally { fs.Position = saved; }
    }

    /// <summary>
    /// The directory holding sce_sys/param.json, as a POSIX relative path ("" for the volume
    /// root). Null when there is no app root, or when there is more than one.
    /// </summary>
    public string? FindAppRoot()
    {
        var roots = Files
            .Where(f => f.RelativePath.EndsWith("sce_sys/param.json", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath[..^"sce_sys/param.json".Length].TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roots.Count == 1 ? roots[0] : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = _own.Count - 1; i >= 0; i--) _own[i].Dispose();
    }
}
```

- [ ] **Step 4: Implement the registry and the static surface**

`FpkgVirtualSource/ContainerRegistry.cs`:

```csharp
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FpkgVirtualSource;

/// <summary>Process-wide handle to container map. Written by the CLI, read by patched IL.</summary>
public static class ContainerRegistry
{
    private static readonly ConcurrentDictionary<string, Container> Open = new(StringComparer.Ordinal);

    public static string Register(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            if (Open.TryAdd(handle, container)) return handle;
        }
        throw new InvalidOperationException("could not allocate a container handle");
    }

    public static Container Get(string handle) =>
        Open.TryGetValue(handle, out var c)
            ? c
            : throw new InvalidOperationException(
                $"no source container is registered for handle '{handle}'. " +
                "The virtual-source patch resolves paths through a container opened by the CLI.");

    public static void Release(string handle)
    {
        if (Open.TryRemove(handle, out var c)) c.Dispose();
    }
}
```

`FpkgVirtualSource/VirtualSource.cs`:

```csharp
namespace FpkgVirtualSource;

/// <summary>One entry of a virtual source tree.</summary>
public readonly record struct VirtualEntry(string RelativePath, bool IsDirectory, long Length);

/// <summary>
/// The surface the patched LibProsperoPkg IL calls. Everything here must stay static and
/// exception-safe: it runs deep inside someone else's call stack.
/// </summary>
public static class VirtualSource
{
    private const string Scheme = "ffpfsc:";

    public static bool IsVirtual(string? path) =>
        path is not null && path.StartsWith(Scheme, StringComparison.Ordinal);

    public static string Root(string handle) => $"{Scheme}{handle}:/";

    public static string PathFor(string handle, string relativePath) =>
        $"{Scheme}{handle}:/{relativePath.TrimStart('/')}";

    private static (Container Container, string Relative) Resolve(string path)
    {
        int sep = path.IndexOf(':', Scheme.Length);
        if (sep < 0) throw new InvalidOperationException("malformed virtual path: " + path);
        string handle = path[Scheme.Length..sep];
        return (ContainerRegistry.Get(handle), path[(sep + 1)..].TrimStart('/'));
    }

    public static Stream Open(string path)
    {
        var (container, relative) = Resolve(path);
        var entry = container.Files.FirstOrDefault(
                        f => string.Equals(f.RelativePath, relative, StringComparison.Ordinal))
                    ?? throw new FileNotFoundException("not present in the source container: " + relative, path);
        return container.Reader.OpenFile(entry, container.Gate);
    }

    public static Stream OpenOrFile(string path) =>
        IsVirtual(path)
            ? Open(path)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);

    /// <summary>Files and directories directly beneath <paramref name="virtualRoot"/>, recursively.</summary>
    public static IEnumerable<VirtualEntry> Enumerate(string virtualRoot)
    {
        var (container, relative) = Resolve(virtualRoot);
        string prefix = relative.Length == 0 ? "" : relative.TrimEnd('/') + "/";
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var f in container.Files)
        {
            if (prefix.Length > 0 && !f.RelativePath.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rel = f.RelativePath[prefix.Length..];
            if (rel.Length == 0) continue;

            for (int slash = rel.IndexOf('/'); slash >= 0; slash = rel.IndexOf('/', slash + 1))
            {
                string dir = rel[..slash];
                if (seenDirs.Add(dir)) yield return new VirtualEntry(dir, IsDirectory: true, Length: 0);
            }
            yield return new VirtualEntry(rel, IsDirectory: false, f.Length);
        }
    }
}
```

- [ ] **Step 5: Run the unit tests and confirm they pass**

Run: `dotnet test FpkgVirtualSource.Tests -v q`
Expected: PASS, 30 tests.

- [ ] **Step 6: Write the integration test against a real container**

`FpkgVirtualSource.Tests/ContainerIntegrationTests.cs`:

```csharp
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
        Skip.IfNot(File.Exists(Fixture) &amp;&amp; Directory.Exists(SourceTree),
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
            using var s = container.Reader.OpenFile(entry, container.Gate);
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
```

- [ ] **Step 7: Build the fixture and run the whole suite**

```bash
python3 -m venv /tmp/mkvenv && /tmp/mkvenv/bin/pip -q install cryptography
cd ~/Developer/MkPFS && /tmp/mkvenv/bin/python -m mkpfs pack folder /tmp/out2 /tmp/out2.ffpfsc
cd /Users/rrocha/Developer/fpkg-gui && dotnet test FpkgVirtualSource.Tests -v q
```

Expected: PASS, 32 tests, none skipped.

- [ ] **Step 8: Commit**

```bash
git add FpkgVirtualSource FpkgVirtualSource.Tests
git commit -m "$(printf 'container open, registry and VirtualSource surface\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 6: Patcher skeleton with pattern-based lookup and shape assertions

**Files:**
- Create: `tools/VirtualSourcePatcher/VirtualSourcePatcher.csproj`
- Create: `tools/VirtualSourcePatcher/Sites.cs`
- Create: `tools/VirtualSourcePatcher/Program.cs`

**Interfaces:**
- Produces: `Sites.Find(ModuleDefMD, string declaringType, string namePrefix, string returnType, int paramCount) -> MethodDef`, `Sites.Expect(bool, string)`, and a CLI `virtual-source-patcher <in.dll> <out.dll>`.

There are no unit tests for this task; the patcher's test is that it produces a module which loads and passes Task 13's acceptance test. Its safety comes from assertions that refuse to write.

- [ ] **Step 1: Create the project**

`tools/VirtualSourcePatcher/VirtualSourcePatcher.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>virtual-source-patcher</AssemblyName>
    <RootNamespace>VirtualSourcePatcher</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="dnlib" Version="4.4.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the lookup helpers**

`tools/VirtualSourcePatcher/Sites.cs`:

```csharp
using dnlib.DotNet;

namespace VirtualSourcePatcher;

internal static class Sites
{
    internal static void Expect(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException(
                $"shape check failed: {what}. Upstream IL has changed; re-derive the patch " +
                "against the new release before shipping it.");
    }

    internal static TypeDef Type(ModuleDefMD module, string fullName) =>
        module.GetTypes().FirstOrDefault(t => t.FullName == fullName)
        ?? throw new InvalidOperationException($"type not found: {fullName}");

    /// <summary>
    /// Finds a method by shape rather than by name. Compiler-generated local functions are
    /// named like &lt;BuildInnerTree&gt;g__IsSelf|39_34 and the ordinal after the pipe moves
    /// between releases, so only the part before it is matched.
    /// </summary>
    internal static MethodDef Method(TypeDef type, string namePrefix, string returnType, int paramCount)
    {
        var found = type.Methods.Where(m =>
            m.HasBody &&
            m.MethodSig.RetType.FullName == returnType &&
            m.MethodSig.Params.Count == paramCount &&
            (m.Name.String == namePrefix ||
             (m.Name.String.Contains("g__" + namePrefix + "|", StringComparison.Ordinal)))).ToList();

        Expect(found.Count == 1,
            $"{type.Name}.{namePrefix} -> {returnType}/{paramCount} matched {found.Count} methods, expected 1");
        return found[0];
    }
}
```

- [ ] **Step 3: Write the entry point with the site inventory**

`tools/VirtualSourcePatcher/Program.cs`:

```csharp
using System.Security.Cryptography;
using dnlib.DotNet;
using VirtualSourcePatcher;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: virtual-source-patcher <LibProsperoPkg.dll> <output.dll>");
    return 2;
}

string source = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
if (!File.Exists(source)) { Console.Error.WriteLine($"error: not found: {source}"); return 1; }

string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
var module = ModuleDefMD.Load(source);

// Resolving these up front means an upstream shape change fails before anything is rewritten.
var builder = Sites.Type(module, "LibProsperoPkg.PKG.ProsperoPkgBuilder");
var innerFile = Sites.Type(module, "LibProsperoPkg.PFS.ProsperoPs5InnerFile");
var fsFile = Sites.Type(module, "LibProsperoPkg.PFS.FSFile");

var openRead = Sites.Method(innerFile, "OpenRead", "System.IO.Stream", 0);
var populate = Sites.Method(builder, "Populate", "System.Void", 8);
var isLooseElf = Sites.Method(builder, "IsLooseElf", "System.Boolean", 1);
var isSelf = Sites.Method(builder, "IsSelf", "System.Boolean", 1);
var sceVersion = Sites.Method(builder, "GetApplicationSceVersion", "System.Byte[]", 1);

var fsFileCtor = fsFile.FindConstructors().SingleOrDefault(c =>
    c.MethodSig.Params.Count == 2 &&
    c.MethodSig.Params[0].FullName == "System.String" &&
    c.MethodSig.Params[1].FullName == "System.Int64")
    ?? throw new InvalidOperationException("FSFile(string, long) not found");

Console.WriteLine($"source {sha[..12]}…");
foreach (var (name, m) in new (string, MethodDef)[]
         {
             ("OpenRead", openRead), ("Populate", populate), ("IsLooseElf", isLooseElf),
             ("IsSelf", isSelf), ("GetApplicationSceVersion", sceVersion), ("FSFile..ctor", fsFileCtor),
         })
    Console.WriteLine($"  located {name,-24} {m.FullName}");

// Rewrites land in Tasks 7-10. Writing the module unchanged here proves load and save round-trip.
module.Write(output);
Console.WriteLine($"wrote {output}");
return 0;
```

- [ ] **Step 4: Run it and confirm every site resolves**

```bash
dotnet run --project tools/VirtualSourcePatcher -c Release -- \
  LibProsperoPkg.dll /tmp/vsp-roundtrip.dll
```

Expected: six `located` lines, then `wrote`. If any line reports a shape failure, stop: the plan's site inventory is stale and must be re-derived before continuing.

- [ ] **Step 5: Confirm the round-tripped module still works**

```bash
mkdir -p /tmp/vsp && cp -R fpkg-cli /tmp/vsp/ && cp fpkg /tmp/vsp/
cp /tmp/vsp-roundtrip.dll /tmp/vsp/LibProsperoPkg.dll && cp LibProsperoPkg.xml /tmp/vsp/
/tmp/vsp/fpkg version
```

Expected: prints the library version. A dnlib save that breaks the module fails here rather than three tasks later.

- [ ] **Step 6: Commit**

```bash
git add tools/VirtualSourcePatcher
git commit -m "$(printf 'virtual-source patcher skeleton with shape assertions\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 7: Patch sites 1 and 2 — `OpenRead` and the `FSFile` constructor

**Files:**
- Create: `tools/VirtualSourcePatcher/Rewrites.cs`
- Modify: `tools/VirtualSourcePatcher/Program.cs`

**Interfaces:**
- Consumes: `Sites.Expect`, the resolved `MethodDef`s from Task 6, and `VirtualSource.IsVirtual`/`Open` from Task 5.
- Produces: `Rewrites.PrependVirtualOpen(MethodDef openRead, MethodDef sourcePathGetter, Sites.Shim shim)` and `Rewrites.VirtualiseFsFileCtor(MethodDef ctor, TypeDef fsFile, Sites.Shim shim)`.

Site 1 prepends, in IL terms:

```csharp
if (VirtualSource.IsVirtual(this.SourcePath)) return VirtualSource.Open(this.SourcePath);
```

Site 2 makes the constructor skip `Path.GetFullPath` for virtual paths. Its existing `Write` delegate opens `SourcePath` with `new FileStream(...)`; that is replaced with `VirtualSource.OpenOrFile(SourcePath)`, which behaves identically for real paths.

- [ ] **Step 1: Add the assembly reference plumbing**

Append to `tools/VirtualSourcePatcher/Sites.cs`:

```csharp
    /// <summary>Imports the FpkgVirtualSource entry points into the target module.</summary>
    internal sealed class Shim
    {
        internal Shim(ModuleDefMD target, string shimPath)
        {
            var shim = ModuleDefMD.Load(shimPath);
            var vs = Type(shim, "FpkgVirtualSource.VirtualSource");
            var importer = new Importer(target);
            IsVirtual  = importer.Import(Method(vs, "IsVirtual", "System.Boolean", 1));
            Open       = importer.Import(Method(vs, "Open", "System.IO.Stream", 1));
            OpenOrFile = importer.Import(Method(vs, "OpenOrFile", "System.IO.Stream", 1));
            Enumerate  = importer.Import(Method(vs, "Enumerate",
                "System.Collections.Generic.IEnumerable`1<FpkgVirtualSource.VirtualEntry>", 1));
        }

        internal IMethod IsVirtual { get; }
        internal IMethod Open { get; }
        internal IMethod OpenOrFile { get; }
        internal IMethod Enumerate { get; }
    }
```

- [ ] **Step 2: Write the two rewrites**

`tools/VirtualSourcePatcher/Rewrites.cs`:

```csharp
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace VirtualSourcePatcher;

internal static class Rewrites
{
    /// <summary>
    /// Site 1. Prepends to ProsperoPs5InnerFile.OpenRead():
    ///   if (VirtualSource.IsVirtual(SourcePath)) return VirtualSource.Open(SourcePath);
    /// </summary>
    internal static void PrependVirtualOpen(MethodDef openRead, MethodDef sourcePathGetter, Sites.Shim shim)
    {
        var body = openRead.Body;
        Sites.Expect(body.Instructions.Count > 0, "OpenRead has no body");
        var original = body.Instructions[0];

        var prologue = new[]
        {
            OpCodes.Ldarg_0.ToInstruction(),
            OpCodes.Call.ToInstruction(sourcePathGetter),
            OpCodes.Call.ToInstruction(shim.IsVirtual),
            OpCodes.Brfalse.ToInstruction(original),
            OpCodes.Ldarg_0.ToInstruction(),
            OpCodes.Call.ToInstruction(sourcePathGetter),
            OpCodes.Call.ToInstruction(shim.Open),
            OpCodes.Ret.ToInstruction(),
        };
        for (int i = 0; i < prologue.Length; i++) body.Instructions.Insert(i, prologue[i]);
        body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Site 2. In FSFile(string, long): leave virtual paths untouched by Path.GetFullPath, and
    /// open through the shim so the Write delegate streams from the container.
    /// </summary>
    internal static void VirtualiseFsFileCtor(MethodDef ctor, TypeDef fsFile, Sites.Shim shim)
    {
        var instructions = ctor.Body.Instructions;

        // Path.GetFullPath(origFileName) -> virtual ? origFileName : Path.GetFullPath(origFileName)
        int getFullPath = instructions.ToList().FindIndex(i =>
            i.OpCode == OpCodes.Call && i.Operand is IMethod m &&
            m.FullName.Contains("System.IO.Path::GetFullPath", StringComparison.Ordinal));
        Sites.Expect(getFullPath >= 0, "FSFile(string, long) does not call Path.GetFullPath");

        var keepAsIs = instructions[getFullPath + 1];
        var guard = new[]
        {
            OpCodes.Dup.ToInstruction(),
            OpCodes.Call.ToInstruction(shim.IsVirtual),
            OpCodes.Brtrue.ToInstruction(keepAsIs),
        };
        for (int i = 0; i < guard.Length; i++) instructions.Insert(getFullPath + i, guard[i]);

        // The Write delegate constructs a FileStream over SourcePath. Redirect it to the shim.
        // FileStream's six arguments are pushed BEFORE the newobj, so the five after the path
        // are nopped in place — the same in-place technique this repo already used on
        // ParseOptimal, which keeps the stack consistent without moving any instruction.
        int redirected = 0;
        foreach (var method in fsFile.Methods.Concat(
                     fsFile.NestedTypes.SelectMany(n => n.Methods)).Where(m => m.HasBody))
        {
            var body = method.Body.Instructions;
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i].OpCode != OpCodes.Newobj || body[i].Operand is not IMethod ctorRef) continue;
                if (ctorRef.DeclaringType.FullName != "System.IO.FileStream") continue;
                Sites.Expect(ctorRef.MethodSig.Params.Count == 6,
                    $"expected the 6-argument FileStream constructor, got {ctorRef.MethodSig.Params.Count}");
                Sites.Expect(i >= 5, "not enough instructions before newobj FileStream");
                for (int back = 1; back <= 5; back++)
                {
                    var arg = body[i - back];
                    Sites.Expect(arg.OpCode.StackBehaviourPop == StackBehaviour.Pop0,
                        $"argument {back} before newobj FileStream is {arg.OpCode}, which is not a " +
                        "simple push; re-derive this rewrite against the current IL");
                    arg.OpCode = OpCodes.Nop; arg.Operand = null;
                }
                body[i] = OpCodes.Call.ToInstruction(shim.OpenOrFile);
                redirected++;
            }
            method.Body.UpdateInstructionOffsets();
        }
        Sites.Expect(redirected >= 1, "no FileStream construction found in FSFile to redirect");
        ctor.Body.UpdateInstructionOffsets();
    }
}
```

**Before writing this, dump the constructor and confirm the push order.** Add a `--dump <method>` mode to `Program.cs` — load the module, find the method, print `m.Body.Instructions` with indices, exactly like the `Sites.Method` lookup plus a `foreach`. Then run:

```bash
dotnet run --project tools/VirtualSourcePatcher -c Release -- --dump LibProsperoPkg.dll FSFile..ctor
```

The five instructions before `newobj FileStream` must each be a simple push (`ldc.i4`, `ldc.i4.s`, a field load, and so on). The assertion above enforces that at patch time; the dump tells you in advance whether it will hold.

- [ ] **Step 3: Wire the rewrites into `Program.cs`**

Replace the `module.Write(output)` line with:

```csharp
string shimPath = Path.Combine(Path.GetDirectoryName(output)!, "FpkgVirtualSource.dll");
Sites.Expect(File.Exists(shimPath), $"FpkgVirtualSource.dll must sit next to {output}");
var shim = new Sites.Shim(module, shimPath);

var sourcePathGetter = innerFile.Methods.Single(m => m.Name == "get_SourcePath");
Rewrites.PrependVirtualOpen(openRead, sourcePathGetter, shim);
Rewrites.VirtualiseFsFileCtor(fsFileCtor, fsFile, shim);

module.Write(output);
Console.WriteLine($"wrote {output} (sites 1, 2)");
```

- [ ] **Step 4: Verify the patched module still builds a normal package**

```bash
dotnet build FpkgVirtualSource -c Release -p:LibDir=../ --nologo
mkdir -p /tmp/vsp && cp FpkgVirtualSource/bin/Release/net10.0/FpkgVirtualSource.dll /tmp/vsp/
dotnet run --project tools/VirtualSourcePatcher -c Release -- LibProsperoPkg.dll /tmp/vsp/LibProsperoPkg.dll
cp -R fpkg-cli /tmp/vsp/ && cp fpkg LibProsperoPkg.xml /tmp/vsp/
/tmp/vsp/fpkg build --source /tmp/out2 --out /tmp/vsp/out --temp-dir /tmp/vsp/tmp \
  --kraken-backend BuiltIn --kraken-level 6
shasum -a 256 /tmp/vsp/out/*.pkg
```

Expected: the build succeeds and the SHA-256 equals an unpatched build of the same source with the same flags. Sites 1 and 2 must be inert for folder sources.

- [ ] **Step 5: Commit**

```bash
git add tools/VirtualSourcePatcher
git commit -m "$(printf 'patch sites 1 and 2: OpenRead and the FSFile constructor\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 8: Patch sites 5, 6 and 7 — the executable probes

**Files:**
- Modify: `tools/VirtualSourcePatcher/Rewrites.cs`
- Modify: `tools/VirtualSourcePatcher/Program.cs`

**Interfaces:**
- Consumes: `Sites.Shim`, the three probe `MethodDef`s from Task 6.
- Produces: `Rewrites.VirtualiseProbe(MethodDef probe, Sites.Shim shim)`.

`ConvertFile` probes **every** file in the tree for ELF magic, including multi-gigabyte payload files, and the no-path branch of each probe materialises the whole file. All three probes read only a handful of leading bytes when given a path, so routing them through the shim keeps that cheap for virtual files.

- [ ] **Step 1: Dump the three probes and confirm their shape**

```bash
for m in IsLooseElf IsSelf GetApplicationSceVersion; do
  dotnet run --project tools/VirtualSourcePatcher -c Release -- --dump LibProsperoPkg.dll $m
done
```

Confirm each contains exactly one `call` to the `OpenExecutableProbe` local function, guarded by a `string.IsNullOrEmpty(file.SourcePath)` test. Record the instruction indices; the rewrite below targets the `call`, not a fixed offset.

- [ ] **Step 2: Write the rewrite**

Append to `tools/VirtualSourcePatcher/Rewrites.cs`:

```csharp
    /// <summary>
    /// Sites 5-7. The probes already branch on "is there a SourcePath", and when there is one
    /// they call OpenExecutableProbe to read a few leading bytes. Swapping that single call for
    /// VirtualSource.OpenOrFile makes virtual paths work and leaves real paths byte-identical:
    /// both return a Stream positioned at zero, and the probes only ever read forward from it.
    /// </summary>
    internal static void VirtualiseProbe(MethodDef probe, Sites.Shim shim)
    {
        int swapped = 0;
        foreach (var ins in probe.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || ins.Operand is not IMethod m) continue;
            if (!m.Name.String.Contains("OpenExecutableProbe", StringComparison.Ordinal)) continue;
            ins.Operand = shim.OpenOrFile;
            swapped++;
        }
        Sites.Expect(swapped == 1,
            $"{probe.Name}: expected exactly 1 OpenExecutableProbe call, found {swapped}");

        // The locals held the concrete FileStream; widen them so a shim Stream fits.
        foreach (var local in probe.Body.Variables)
            if (local.Type.FullName == "System.IO.FileStream")
                local.Type = probe.Module.CorLibTypes.GetTypeRef("System.IO", "Stream").ToTypeSig();

        probe.Body.UpdateInstructionOffsets();
    }
```

- [ ] **Step 3: Wire the three calls into `Program.cs`**

Insert before `module.Write(output)`:

```csharp
foreach (var probe in new[] { isLooseElf, isSelf, sceVersion })
    Rewrites.VirtualiseProbe(probe, shim);
```

and change the final message to `(sites 1, 2, 5, 6, 7)`.

- [ ] **Step 4: Verify a folder build is still byte-identical**

```bash
dotnet run --project tools/VirtualSourcePatcher -c Release -- LibProsperoPkg.dll /tmp/vsp/LibProsperoPkg.dll
rm -rf /tmp/vsp/out /tmp/vsp/tmp
/tmp/vsp/fpkg build --source /tmp/out2 --out /tmp/vsp/out --temp-dir /tmp/vsp/tmp \
  --kraken-backend BuiltIn --kraken-level 6
shasum -a 256 /tmp/vsp/out/*.pkg
```

Expected: identical SHA-256 to the unpatched build. `eboot.bin` is a real SELF, so this exercises all three probes on the happy path.

- [ ] **Step 5: Commit**

```bash
git add tools/VirtualSourcePatcher
git commit -m "$(printf 'patch sites 5-7: route executable probes through the shim\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 9: Patch site 3 — `Populate`

**Files:**
- Modify: `tools/VirtualSourcePatcher/Rewrites.cs`
- Create: `FpkgVirtualSource/TreeOverlay.cs`
- Modify: `tools/VirtualSourcePatcher/Program.cs`

**Interfaces:**
- Consumes: `VirtualSource.Enumerate`, `FSDir`/`FSFile` from LibProsperoPkg.
- Produces: `FpkgVirtualSource.TreeOverlay.Populate(object node, string path)` returning `bool` — true when it handled the directory, false when the caller should fall through to its own filesystem walk.

Rather than rewriting `Populate`'s body — a long local function with six optional parameters and nested closures — the patch prepends a single early-out. All the tree-building logic lives in our own assembly, in C#, where it can be read and tested.

- [ ] **Step 1: Implement the overlay in the shim**

`FpkgVirtualSource/TreeOverlay.cs`:

```csharp
using System.Reflection;

namespace FpkgVirtualSource;

/// <summary>
/// Builds the FSDir/FSFile tree for a virtual source. Called from patched Populate.
///
/// Reflection is used deliberately: this assembly must not hold a compile-time reference to
/// LibProsperoPkg's FSDir/FSFile, or the shim would pin one library version and break on the
/// next release. The reflection cost is paid once per directory, not per byte.
/// </summary>
public static class TreeOverlay
{
    /// <summary>The marker file the CLI drops in the staging root.</summary>
    public const string MarkerFileName = ".fpkg-virtual-source";

    /// <summary>
    /// Fills <paramref name="node"/> from the container when <paramref name="path"/> is a staging
    /// root carrying the marker. Returns false when it is an ordinary directory, in which case the
    /// caller does its normal filesystem walk.
    /// </summary>
    public static bool Populate(object node, string path)
    {
        string marker = Path.Combine(path, MarkerFileName);
        if (!File.Exists(marker)) return false;

        string handle = File.ReadAllText(marker).Trim();
        var container = ContainerRegistry.Get(handle);
        string appRoot = container.FindAppRoot()
            ?? throw new InvalidOperationException(
                "the source container has no single app root (a directory holding sce_sys/param.json)");

        var asm = node.GetType().Assembly;
        Type dirType = asm.GetType("LibProsperoPkg.PFS.FSDir", throwOnError: true)!;
        Type fileType = asm.GetType("LibProsperoPkg.PFS.FSFile", throwOnError: true)!;
        var fileCtor = fileType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(string), typeof(long)])
            ?? throw new InvalidOperationException("FSFile(string, long) not found");

        var dirs = new Dictionary<string, object>(StringComparer.Ordinal) { [""] = node };
        string prefix = appRoot.Length == 0 ? "" : appRoot + "/";

        foreach (var entry in VirtualSource.Enumerate(VirtualSource.Root(handle))
                                           .OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            if (prefix.Length > 0 && !entry.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            string rel = entry.RelativePath[prefix.Length..];
            if (rel.Length == 0) continue;

            // sce_sys is staged on disk; the caller's own walk already added it.
            if (rel == "sce_sys" || rel.StartsWith("sce_sys/", StringComparison.Ordinal)) continue;

            int slash = rel.LastIndexOf('/');
            string parentPath = slash < 0 ? "" : rel[..slash];
            string name = slash < 0 ? rel : rel[(slash + 1)..];
            object parent = dirs[parentPath];

            if (entry.IsDirectory)
            {
                object child = Activator.CreateInstance(dirType)!;
                Set(child, "name", name);
                Set(child, "Parent", parent);
                Add(parent, "Dirs", child);
                dirs[rel] = child;
            }
            else
            {
                object file = fileCtor.Invoke([VirtualSource.PathFor(handle, entry.RelativePath), entry.Length]);
                Set(file, "name", name);
                Set(file, "Parent", parent);
                Add(parent, "Files", file);
            }
        }
        return true;
    }

    private static void Set(object target, string member, object? value)
    {
        var type = target.GetType();
        var field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null) { field.SetValue(target, value); return; }
        type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(target, value);
    }

    private static void Add(object target, string listMember, object item)
    {
        object list = target.GetType()
            .GetField(listMember, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(target)!;
        list.GetType().GetMethod("Add")!.Invoke(list, [item]);
    }
}
```

- [ ] **Step 2: Write the shim test**

Append to `FpkgVirtualSource.Tests/VirtualSourceTests.cs`:

```csharp
    [Fact]
    public void TreeOverlayIgnoresOrdinaryDirectories()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try { Assert.False(TreeOverlay.Populate(new object(), dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }
```

Run: `dotnet test FpkgVirtualSource.Tests -v q` — expected PASS.

- [ ] **Step 3: Write the patcher rewrite**

Append to `tools/VirtualSourcePatcher/Rewrites.cs`:

```csharp
    /// <summary>
    /// Site 3. Prepends to Populate(node, path, ...):
    ///   if (TreeOverlay.Populate(node, path)) return;
    /// The staged sce_sys is still walked normally by the caller for the real directory.
    /// </summary>
    internal static void PrependTreeOverlay(MethodDef populate, IMethod overlay)
    {
        var body = populate.Body;
        Sites.Expect(body.Instructions.Count > 0, "Populate has no body");
        Sites.Expect(populate.MethodSig.Params.Count == 8,
            $"Populate takes {populate.MethodSig.Params.Count} parameters, expected 8");
        Sites.Expect(populate.MethodSig.Params[0].FullName == "LibProsperoPkg.PFS.FSDir",
            "Populate's first parameter is not FSDir");
        Sites.Expect(populate.MethodSig.Params[1].FullName == "System.String",
            "Populate's second parameter is not String");

        var original = body.Instructions[0];
        var prologue = new[]
        {
            OpCodes.Ldarg_0.ToInstruction(),
            OpCodes.Ldarg_1.ToInstruction(),
            OpCodes.Call.ToInstruction(overlay),
            OpCodes.Brfalse.ToInstruction(original),
            OpCodes.Ret.ToInstruction(),
        };
        for (int i = 0; i < prologue.Length; i++) body.Instructions.Insert(i, prologue[i]);
        body.UpdateInstructionOffsets();
    }
```

Add `TreeOverlay.Populate` to `Sites.Shim`:

```csharp
            var overlay = Type(shim, "FpkgVirtualSource.TreeOverlay");
            TreeOverlay = importer.Import(Method(overlay, "Populate", "System.Boolean", 2));
```

with a matching `internal IMethod TreeOverlay { get; }` property.

- [ ] **Step 4: Wire it in and verify a folder build is unchanged**

Add `Rewrites.PrependTreeOverlay(populate, shim.TreeOverlay);` to `Program.cs`, then:

```bash
dotnet build FpkgVirtualSource -c Release -p:LibDir=../ --nologo
cp FpkgVirtualSource/bin/Release/net10.0/FpkgVirtualSource.dll /tmp/vsp/
dotnet run --project tools/VirtualSourcePatcher -c Release -- LibProsperoPkg.dll /tmp/vsp/LibProsperoPkg.dll
rm -rf /tmp/vsp/out /tmp/vsp/tmp
/tmp/vsp/fpkg build --source /tmp/out2 --out /tmp/vsp/out --temp-dir /tmp/vsp/tmp \
  --kraken-backend BuiltIn --kraken-level 6
shasum -a 256 /tmp/vsp/out/*.pkg
```

Expected: still byte-identical to the unpatched build. `/tmp/out2` has no marker file, so the overlay must return false and change nothing.

- [ ] **Step 5: Commit**

```bash
git add tools/VirtualSourcePatcher FpkgVirtualSource FpkgVirtualSource.Tests
git commit -m "$(printf 'patch site 3: overlay the container tree in Populate\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 10: Patch site 4 — `PlaintextBlockReader`

**Files:**
- Modify: `tools/VirtualSourcePatcher/Rewrites.cs`
- Modify: `tools/VirtualSourcePatcher/Program.cs`

**Interfaces:**
- Consumes: `Sites.Shim.OpenOrFile`.
- Produces: `Rewrites.WidenPlaintextBlockReader(TypeDef reader, Sites.Shim shim)`.

This is the riskiest site. `ProsperoNapsMeta.PlaintextBlockReader` caches `private FileStream? currentStream` and opens it with `new FileStream(sourcePath, ...)`. The field type must widen to `Stream` and the construction must route through the shim. NAPS integrity digests are computed from exactly these reads, so an error here corrupts the package rather than crashing — which is why Task 13's byte-identical check is the real gate.

- [ ] **Step 1: Dump the type and confirm its shape**

```bash
dotnet run --project tools/VirtualSourcePatcher -c Release -- --dump-type LibProsperoPkg.dll PlaintextBlockReader
```

Add `--dump-type` to `Program.cs` (print every field with its type, then every method's IL). Confirm: three fields (`currentPath : String`, `currentStream : FileStream`, `buffer : Byte[]`), and that every use of `currentStream` is a `Stream` member — `Read`, `Position`/`Seek`, `Dispose`. **If any use calls a `FileStream`-only member, stop and report; the rewrite below is not sufficient.**

- [ ] **Step 2: Write the rewrite**

Append to `tools/VirtualSourcePatcher/Rewrites.cs`:

```csharp
    /// <summary>
    /// Site 4. Widens PlaintextBlockReader.currentStream from FileStream to Stream and routes its
    /// construction through the shim. Every existing use is a Stream member, so widening the field
    /// leaves all of them valid; calls declared on FileStream are retargeted to Stream.
    /// </summary>
    internal static void WidenPlaintextBlockReader(TypeDef reader, Sites.Shim shim)
    {
        var field = reader.Fields.SingleOrDefault(f => f.Name == "currentStream")
            ?? throw new InvalidOperationException("PlaintextBlockReader.currentStream not found");
        Sites.Expect(field.FieldType.FullName == "System.IO.FileStream",
            $"currentStream is {field.FieldType.FullName}, expected System.IO.FileStream");

        var module = reader.Module;
        var streamSig = module.CorLibTypes.GetTypeRef("System.IO", "Stream").ToTypeSig();
        var importer = new Importer(module);
        field.FieldType = streamSig;

        int replacedCtor = 0, retargeted = 0;
        foreach (var method in reader.Methods.Where(m => m.HasBody))
        {
            var instructions = method.Body.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                var ins = instructions[i];

                if (ins.OpCode == OpCodes.Newobj && ins.Operand is IMethod ctor &&
                    ctor.DeclaringType.FullName == "System.IO.FileStream")
                {
                    // The five trailing arguments are constants pushed immediately before the
                    // newobj; replacing them with nop is stack-neutral because OpenOrFile takes
                    // only the path. Verified against the dump in Step 1.
                    Sites.Expect(ctor.MethodSig.Params.Count == 6,
                        $"expected the 6-argument FileStream constructor, got {ctor.MethodSig.Params.Count}");
                    for (int back = 1; back <= 5; back++)
                    {
                        var arg = instructions[i - back];
                        Sites.Expect(arg.OpCode.StackBehaviourPush == StackBehaviour.Push1 ||
                                     arg.OpCode.StackBehaviourPush == StackBehaviour.Pushi,
                            $"argument {back} before newobj FileStream is {arg.OpCode}, not a simple push");
                        arg.OpCode = OpCodes.Nop; arg.Operand = null;
                    }
                    instructions[i] = OpCodes.Call.ToInstruction(shim.OpenOrFile);
                    replacedCtor++;
                    continue;
                }

                if (ins.Operand is IMethod call &&
                    call.DeclaringType?.FullName == "System.IO.FileStream")
                {
                    var onStream = importer.Import(
                        module.CorLibTypes.GetTypeRef("System.IO", "Stream")
                              .ResolveTypeDefThrow()
                              .FindMethod(call.Name, call.MethodSig));
                    ins.Operand = onStream;
                    retargeted++;
                }
            }
            method.Body.UpdateInstructionOffsets();
        }

        Sites.Expect(replacedCtor == 1,
            $"expected exactly 1 FileStream construction in PlaintextBlockReader, found {replacedCtor}");
        Console.WriteLine($"  site 4: widened currentStream, retargeted {retargeted} FileStream call(s)");
    }
```

- [ ] **Step 3: Wire it in**

In `Program.cs`, before `module.Write(output)`:

```csharp
var napsMeta = Sites.Type(module, "LibProsperoPkg.PKG.ProsperoNapsMeta");
var plaintextReader = napsMeta.NestedTypes.SingleOrDefault(t => t.Name == "PlaintextBlockReader")
    ?? throw new InvalidOperationException("ProsperoNapsMeta.PlaintextBlockReader not found");
Rewrites.WidenPlaintextBlockReader(plaintextReader, shim);
```

and change the message to `(sites 1-7)`.

- [ ] **Step 4: Verify a folder build is STILL byte-identical**

```bash
dotnet run --project tools/VirtualSourcePatcher -c Release -- LibProsperoPkg.dll /tmp/vsp/LibProsperoPkg.dll
rm -rf /tmp/vsp/out /tmp/vsp/tmp
/tmp/vsp/fpkg build --source /tmp/out2 --out /tmp/vsp/out --temp-dir /tmp/vsp/tmp \
  --kraken-backend BuiltIn --kraken-level 6
shasum -a 256 /tmp/vsp/out/*.pkg
```

Expected: identical SHA-256 to the unpatched build. This is the single most important check in the plan — NAPS integrity digests come from this code path, and a wrong digest produces a package that builds cleanly and fails on console.

- [ ] **Step 5: Commit**

```bash
git add tools/VirtualSourcePatcher
git commit -m "$(printf 'patch site 4: widen PlaintextBlockReader to Stream\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 11: Unify the patch pipeline

**Files:**
- Create: `patch.sh`
- Modify: `patch-oodle.sh`
- Modify: `fpkg-cli/LibraryResolver.cs`
- Modify: `fpkg-cli/Program.cs` (the `version` command)

**Interfaces:**
- Produces: `LibProsperoPkg.patched.dll` plus `LibProsperoPkg.patched.stamp`, a JSON document `{"source":"<sha256>","patches":["oodle","ffpfsc"]}`.

Two independently patched copies of the library cannot both load, so the existing `LibProsperoPkg.oodle.dll` and a separate ffpfsc output would silently fight. One module, applied in sequence.

- [ ] **Step 1: Write `patch.sh`**

```bash
#!/bin/sh
# Applies every available patch to one copy of LibProsperoPkg and records which went in.
#
#   ./patch.sh
#
# Oodle needs native/libfpkgoodle.dylib (build it with OODLE_SDK=<sdk> ./native/build.sh).
# Virtual source needs FpkgVirtualSource.dll, which is built here.
set -e
here=$(cd "$(dirname "$0")" && pwd)
lib="$here/LibProsperoPkg.dll"
out="$here/LibProsperoPkg.patched.dll"
stamp="$here/LibProsperoPkg.patched.stamp"
[ -f "$lib" ] || { echo "error: $lib not found" >&2; exit 1; }

work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
cp "$lib" "$work/stage.dll"
applied=""

if [ -f "$here/native/libfpkgoodle.dylib" ]; then
    dotnet run --project "$here/tools/OodlePatcher" -c Release --nologo -- \
        "$work/stage.dll" "$work/next.dll" >/dev/null
    mv "$work/next.dll" "$work/stage.dll"
    applied="$applied oodle"
else
    echo "note: skipping oodle (native/libfpkgoodle.dylib absent)" >&2
fi

dotnet build "$here/FpkgVirtualSource" -c Release -p:LibDir="$here/" --nologo >/dev/null
cp "$here/FpkgVirtualSource/bin/Release/net10.0/FpkgVirtualSource.dll" "$here/"
cp "$here/FpkgVirtualSource.dll" "$work/"
dotnet run --project "$here/tools/VirtualSourcePatcher" -c Release --nologo -- \
    "$work/stage.dll" "$work/next.dll" >/dev/null
mv "$work/next.dll" "$work/stage.dll"
applied="$applied ffpfsc"

mv "$work/stage.dll" "$out"
printf '{"source":"%s","patches":[%s]}\n' \
    "$(shasum -a 256 "$lib" | cut -d' ' -f1)" \
    "$(echo $applied | sed 's/ /","/g; s/^/"/; s/$/"/')" > "$stamp"
echo "patched LibProsperoPkg.patched.dll:$applied"
```

`chmod +x patch.sh`. Replace the body of `patch-oodle.sh` with `exec "$(dirname "$0")/patch.sh" "$@"` and a one-line comment saying it is kept for compatibility.

- [ ] **Step 2: Teach the resolver about the new name**

In `fpkg-cli/LibraryResolver.cs`, wherever `LibProsperoPkg.oodle.dll` is preferred, prefer `LibProsperoPkg.patched.dll` first, then `LibProsperoPkg.oodle.dll`, then `LibProsperoPkg.dll`. Validate `LibProsperoPkg.patched.stamp`'s `source` against the SHA-256 of `LibProsperoPkg.dll` exactly as the existing Oodle stamp check does, and ignore a stale patched DLL with a warning rather than loading it.

- [ ] **Step 3: Report the patches in `fpkg version`**

In `DescribeLibrary`, replace the single `patched Oodle: available/not available` line with one that reads the stamp:

```csharp
string stamp = Path.Combine(Path.GetDirectoryName(path)!, "LibProsperoPkg.patched.stamp");
string patches = File.Exists(stamp)
    ? string.Join(", ", JsonDocument.Parse(File.ReadAllText(stamp))
        .RootElement.GetProperty("patches").EnumerateArray().Select(e => e.GetString()))
    : "none";
Console.WriteLine($"  patches:      {patches}");
```

- [ ] **Step 4: Verify**

```bash
./patch.sh
./publish.sh >/dev/null
./fpkg version
```

Expected: `patches:      oodle, ffpfsc` (or just `ffpfsc` without the dylib), and `loaded from` naming `LibProsperoPkg.patched.dll`.

- [ ] **Step 5: Commit**

```bash
git add patch.sh patch-oodle.sh fpkg-cli
git commit -m "$(printf 'unify the patch pipeline into one patched module\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 12: CLI wiring — container detection and staging

**Files:**
- Modify: `fpkg-cli/Program.cs`
- Modify: `fpkg-cli/fpkg.csproj`

**Interfaces:**
- Consumes: `Container.Open`, `Container.FindAppRoot`, `ContainerRegistry.Register/Release`, `TreeOverlay.MarkerFileName`, `VirtualSource` from Task 5.
- Produces: `--source` accepting a container file; a `ContainerSource : IDisposable` private class in `Program.cs` that owns the staging root and the registry handle.

- [ ] **Step 1: Reference the shim from the CLI**

Add to `fpkg-cli/fpkg.csproj`, alongside the existing `LibProsperoPkg` reference:

```xml
    <Reference Include="FpkgVirtualSource">
      <HintPath>$(LibDir)FpkgVirtualSource.dll</HintPath>
      <Private>false</Private>
    </Reference>
```

Guard every use behind a feature constant set the same way the existing ones are, so a release folder without the shim still builds and runs:

```xml
    <DefineConstants Condition="Exists('$(LibDir)FpkgVirtualSource.dll')">$(DefineConstants);HAS_VIRTUAL_SOURCE</DefineConstants>
```

- [ ] **Step 2: Implement the staging owner**

Add to `fpkg-cli/Program.cs`, next to `SceSysQuarantine`:

```csharp
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

        internal static ContainerSource Open(string path, string temporaryDirectory)
        {
            var container = FpkgVirtualSource.Container.Open(path);
            try
            {
                string appRoot = container.FindAppRoot() ?? throw new RenamerLikeError(
                    $"{Path.GetFileName(path)} has no single app root (a folder containing " +
                    "sce_sys/param.json). Candidates: " + string.Join(", ", container.Files
                        .Where(f => f.RelativePath.EndsWith("sce_sys/param.json", StringComparison.OrdinalIgnoreCase))
                        .Select(f => f.RelativePath)));

                string handle = FpkgVirtualSource.ContainerRegistry.Register(container);
                string root = Path.Combine(temporaryDirectory, "fpkg-vsrc-" + handle);
                Directory.CreateDirectory(root);

                string prefix = appRoot.Length == 0 ? "sce_sys/" : appRoot + "/sce_sys/";
                long staged = 0;
                foreach (var entry in container.Files.Where(f =>
                             f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    string target = Path.Combine(root, "sce_sys",
                        entry.RelativePath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var src = container.Reader.OpenFile(entry, container.Gate);
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
                container.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            FpkgVirtualSource.ContainerRegistry.Release(handle);
            try { Directory.Delete(SourceFolder, recursive: true); } catch (IOException) { }
        }
    }
#endif
```

- [ ] **Step 3: Branch the source validation**

Replace `Program.cs:307-309` (the `Directory.Exists` checks) with:

```csharp
        ContainerSource? containerSource = null;
        if (File.Exists(source) && !Directory.Exists(source))
        {
#if HAS_VIRTUAL_SOURCE
            containerSource = ContainerSource.Open(source, tempDir);
            source = containerSource.SourceFolder;
#else
            return Fail($"--source is a file, which needs the virtual-source patch: {source}. " +
                        "Run ./patch.sh to enable .ffpfsc and .exfat sources.");
#endif
        }
        else
        {
            if (!Directory.Exists(source)) return Fail($"source folder does not exist: {source}");
            if (!Directory.Exists(Path.Combine(source, "sce_sys")))
                return Fail($"source folder has no sce_sys/ subfolder: {source}");
        }
```

Add `using var _containerSource = containerSource;` immediately afterwards so the staging root is removed on every exit path, and move the existing `tempDir` resolution above this block since `ContainerSource.Open` needs it.

- [ ] **Step 4: Document the flag**

Change the help text for `--source` from `source folder; must contain sce_sys/` to:

```
          --source <path>         source folder containing sce_sys/, or a .ffpfsc / .exfat
                                  container (containers need ./patch.sh)         (required)
```

- [ ] **Step 5: Verify both source kinds work**

```bash
./patch.sh && ./publish.sh >/dev/null
./fpkg build --source /tmp/out2 --out /tmp/a --temp-dir /tmp/ta --kraken-backend BuiltIn --kraken-level 6
./fpkg build --source /tmp/out2.ffpfsc --out /tmp/b --temp-dir /tmp/tb --kraken-backend BuiltIn --kraken-level 6
shasum -a 256 /tmp/a/*.pkg /tmp/b/*.pkg
```

Expected: both succeed. The hashes are compared properly in Task 13; here it is enough that the container build completes and the staging root under `/tmp/tb` is gone afterwards.

- [ ] **Step 6: Commit**

```bash
git add fpkg-cli
git commit -m "$(printf 'build from a .ffpfsc or .exfat container\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Task 13: Acceptance test and documentation

**Files:**
- Create: `FpkgVirtualSource.Tests/AcceptanceTests.cs`
- Modify: `NOTES.md`
- Modify: `~/.claude/projects/-Users-rrocha-Developer-fpkg-gui/memory/fpkg-ffpfsc-source-provider-plan.md`

**Interfaces:**
- Consumes: everything.

The whole feature reduces to one criterion: a container build and a folder build of the same content produce the same package, byte for byte.

- [ ] **Step 1: Write the acceptance test**

`FpkgVirtualSource.Tests/AcceptanceTests.cs`:

```csharp
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class AcceptanceTests
{
    private const string Fixture = "/tmp/out2.ffpfsc";
    private const string SourceTree = "/tmp/out2";

    private static string Build(string source, string tag)
    {
        string release = ReleaseFolder.Find();
        string outDir = Path.Combine(Path.GetTempPath(), "fpkg-accept-" + tag);
        string tmpDir = Path.Combine(Path.GetTempPath(), "fpkg-accept-tmp-" + tag);
        foreach (var d in new[] { outDir, tmpDir })
        {
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
            Directory.CreateDirectory(d);
        }

        var psi = new ProcessStartInfo(Path.Combine(release, "fpkg"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = release,
        };
        foreach (var arg in new[]
                 {
                     "build", "--source", source, "--out", outDir, "--temp-dir", tmpDir,
                     "--kraken-backend", "BuiltIn", "--kraken-level", "6", "--no-verify",
                 })
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"build failed for {source}:\n{stdout}\n{stderr}");

        string pkg = Directory.GetFiles(outDir, "*.pkg").Single();
        using var fs = File.OpenRead(pkg);
        string hash = Convert.ToHexString(SHA256.HashData(fs));
        Directory.Delete(outDir, recursive: true);
        Directory.Delete(tmpDir, recursive: true);
        return hash;
    }

    [SkippableFact]
    public void ContainerAndFolderBuildsAreByteIdentical()
    {
        Skip.IfNot(File.Exists(Fixture) && Directory.Exists(SourceTree),
                   $"needs {Fixture} and {SourceTree}");
        Assert.Equal(Build(SourceTree, "folder"), Build(Fixture, "container"));
    }
}
```

- [ ] **Step 2: Run it**

```bash
dotnet test FpkgVirtualSource.Tests -v q --filter ContainerAndFolderBuildsAreByteIdentical
```

Expected: PASS. **If the hashes differ, do not proceed.** Diff the two packages with `./fpkg info` on each and with `./fpkg extract`, and treat site 4 as the first suspect: it is the only site that can change output without failing.

- [ ] **Step 3: Measure temp usage and wall clock**

```bash
/usr/bin/time -p ./fpkg build --source /tmp/out2       --out /tmp/a --temp-dir /tmp/ta --kraken-backend BuiltIn --kraken-level 6
/usr/bin/time -p ./fpkg build --source /tmp/out2.ffpfsc --out /tmp/b --temp-dir /tmp/tb --kraken-backend BuiltIn --kraken-level 6
```

Record both. Expected: container build within roughly the same wall clock, with the only extra temp being the staged `sce_sys`.

- [ ] **Step 4: Document**

Add a `## Building from a .ffpfsc or .exfat container` section to `NOTES.md` covering: what the patch enables, `./patch.sh`, the `--source` change, that `sce_sys` is staged into the temp dir and everything else streams, the measured throughput (692 MiB/s shared, 3,364 MiB/s with independent readers, against 118 MiB/s for the hungriest consumer), and the acceptance criterion. Record the measured numbers from Step 3.

Update the parked memory note `fpkg-ffpfsc-source-provider-plan.md`: the plan is no longer parked, and its "nobody has fed a .ffpfsc into the builder without materialising the tree" conclusion is superseded.

- [ ] **Step 5: Commit**

```bash
git add FpkgVirtualSource.Tests NOTES.md
git commit -m "$(printf 'acceptance test: container and folder builds are byte-identical\n\nCo-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>')"
```

---

## Notes for whoever executes this

- **Tasks 1–5 are useful on their own.** They give a tested, standalone exFAT/container reader with no patching involved. If the patch work stalls, that library still has value.
- **After every patcher task, re-run the folder build and compare its hash to an unpatched build.** Sites 1–7 must all be inert for folder sources. A hash that changes after a patcher task means that task broke something, and you will never have a smaller haystack than right then.
- **Re-derive, do not force.** If a shape assertion fires against a newer LibProsperoPkg, the fix is to dump the IL and update the pattern — never to loosen the assertion.
- **`sudo` is never required.** Nothing here needs a kext, a mount, or elevated privileges.
