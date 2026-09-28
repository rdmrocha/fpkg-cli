# In-Place Single-File Build Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut peak build disk from ~2× the package size to ~1× by writing the inner `pfs_image.dat` straight into the final package at +0x10000 and transforming it in place, instead of spooling it to `--temp-dir` and copying it through the outer-PFS writer.

**Architecture:** A new `FpkgVirtualSource.InPlaceImage` shim exposes a `fpkgwin:<path>@<offset>` "window path" whose streams read and write a byte range of the final package as if it were a standalone file. Eight new dnlib sites in `tools/VirtualSourcePatcher` make LibProsperoPkg resolve window paths through that shim: stage 1 writes the inner image into the package's PFS data region, the deferred layout permutation is replayed in place with a copy-on-write staging map, and stage 3's outer-PFS pass reads block *j* from the very offset it writes it back to. Reads always run ahead of writes, so the in-place transform is safe; if the permutation's live displaced bytes exceed a budget, the shim silently falls back to a spool file and warns.

**Tech Stack:** C# / .NET 10, xUnit, dnlib (IL rewriting), LibProsperoPkg 0.6.9 (closed-source release binary).

**Spec:** this document — the "Spec" section below is the agreed design; it was derived by decompiling `LibProsperoPkg.dll` with ilspycmd and tracing the nwonly publisher build path. Line references are to that decompilation, reproducible with `dotnet ~/.dotnet/tools/.store/ilspycmd/*/ilspycmd/*/tools/net10.0/any/ilspycmd.dll -o <dir> -p LibProsperoPkg.dll`.

## Global Constraints

- **Byte parity is the acceptance bar.** A package built in place MUST be byte-identical (same length, same SHA-256) to one built with `--spool`. Any divergence is a bug in this change, not an expected difference.
- **Default on.** In-place is the default; `--spool` opts out. Both paths stay permanently supported — `--spool` is the parity oracle and the fallback.
- **Never fail a build over disk strategy.** Any condition the in-place path cannot handle falls back to the spool path with a `warning:` line naming the reason. A build that used to succeed must still succeed.
- **Inert without the CLI.** Every patch site must behave exactly as stock LibProsperoPkg when `InPlaceImage.Enabled` is false, so an unpatched or unconfigured host is unaffected. This mirrors sites 13–15.
- **Shape-checked patching.** Every new site asserts the upstream IL shape through `Sites.Expect` and fails the patch loudly if 0.6.9's IL has moved. Never patch by raw instruction index without an accompanying shape assertion.
- **Window path grammar:** `fpkgwin:` + absolute file path + `@` + decimal byte offset. Example: `fpkgwin:/Users/x/out/IV0000-TEST00000_00-0000000000000000.pkg@65536`.
- **Package geometry (fixed, do not parameterise):** the outer PFS begins at package offset `0x10000` (65536); `pfs_image.dat` is outer file 0 and always starts at outer block 0; outer block size is 65536.

## Review Focus

These are the input classes the spec implies but that no task's happy path exercises. Each has a test pinned to the task that owns the code.

1. **A layout permutation whose displaced bytes exceed the budget** — must fall back to the spool and still produce the byte-identical package, not a truncated or scrambled one. (Task 2, Task 8)
2. **A source tree with an identity layout (no moves at all)** — `MaterializePhysicalLayout` is never called, so the in-place path must already be correct with no relocation step. (Task 2, Task 8)
3. **A build that fails or is cancelled mid-stage** — the half-written package must be deleted, and no window path may ever be passed to `File.Delete` in a way that deletes the real output or throws out of a cleanup `finally`. (Task 3, Task 6)
4. **`--out` and `--temp-dir` on different volumes** — the in-place path stops using `--temp-dir` for the big file entirely; the CLI must not preflight-check free space on the wrong volume or claim a spool it will not write. (Task 7)
5. **An unpatched `LibProsperoPkg.dll` (user never ran `fpkg patch`)** — the CLI must not enable in-place, because none of the sites exist; it must not silently produce a corrupt package. (Task 7)

---

## Spec

### What costs the second copy today

`ProsperoPkgBuilder.BuildPublisherImage` (decompiled `LibProsperoPkg.PKG/ProsperoPkgBuilder.cs:816-1060`) runs five stages:

| stage | writes | size |
|---|---|---|
| 1 | inner `pfs_image.dat` **spool** in `--temp-dir` (`BuildFromFsTreeToSpool`, :860) | S |
| 2 | `naps_pkg_layout.dat` temp | ~100 KB |
| 3 | outer PFS, already written **directly into the final package at 0x10000** (:918) | S + a few blocks |
| 4 | `cnt.tmp` — metadata only, streamed since 0.6.4 | tens of MB |

Upstream already removed the outer staging copy: `PrepositionedFihOutputPath = finalPath` (`LibProsperoPkg/ProsperoPackageBuilder.cs:586`). What remains is the spool coexisting with the outer image that is its encrypted copy — peak ≈ 2 × package.

### Why in-place is safe

`ProsperoOuterPfsBuilder.BuildForPackageToFile` (`LibProsperoPkg.PFS/ProsperoOuterPfsBuilder.cs:553-1000`) writes data block *j* of file 0 at `outputOffset + j*65536` and reads its source strictly sequentially, one 64 KiB block at a time — both in the single-threaded loop (:709) and in the batched parallel path (:741), where the reader is always ahead of the writer. So if the plaintext already sits at exactly those offsets, "read block, hash, XTS-encrypt, write back" is a legal in-place transform. Structural blocks (superblock, inode table, dirents, FLT, indirect trees) are all allocated *after* the data blocks, so nothing the builder writes later lands on plaintext it has not yet consumed.

Nothing re-reads the spool after stage 3 in the CLI's configuration: `ProsperoNapsMeta` would (`ProsperoNapsMeta.cs:1073`) but short-circuits on the `PhysicalBlockDigests` / `PhysicalBlockCheckCodes` that stage 3 collects (`:984-987`). That holds only while `NapsIntegrityProvider == null`, which the CLI never sets — Task 3 asserts it.

### The one hazard: the deferred layout permutation

Stage 1 writes compressed blocks as it goes, then `SelectPhysicalLayout` (`ProsperoPs5InnerImageAssembler.cs:2785`) picks a coalescing/alignment plan that moves extents. In `BuildToSpool` (`:525`, `deferLayout: true`) the move is never materialised: `BuildImage` builds a `PhysicalLayoutStream` (`LibProsperoPkg.Util/PhysicalLayoutStream.cs`) that maps final offsets to spool offsets, and the outer pass reads through it.

That is exactly what in-place cannot do, because destination and source now live in the same bytes. `Coalesce` fills gaps left by dedup and `Relocate` compacts, so destination ≤ source is *usual* but **not guaranteed** — `Coalesce` appends to a running end that can exceed a movable extent's source offset. So the relocation must be correct for an arbitrary permutation.

Solution (Task 2): force `deferLayout: false` when in-place is on, and replace `MaterializePhysicalLayout` with a copy-on-write in-place pass. We control both ends of the copy — the `Func<Stream>` the library hands us wraps *our* `WindowStream`, so we see every physical source read. Before overwriting destination range *D*, the original bytes of *D* are staged in memory; any later source read that lands in a staged range is served from the staging map instead of the file, and a staged range is dropped once fully consumed. Live staged bytes are bounded by `MaxRelocationBytes`; exceeding it falls back. No knowledge of the library's internal extent structs is required, which is what makes this implementable from a separate assembly.

### Patch sites

| # | Method | Change |
|---|---|---|
| 16 | `ProsperoPkgBuilder.BuildPublisherImage` | `text2` (the spool path) becomes `InPlaceImage.SpoolPathFor(text2, text3, flag2)` |
| 17 | `ProsperoPs5InnerImageAssembler.BuildToSpool` | bypass `Path.GetFullPath` for window paths |
| 18 | `ProsperoPs5InnerImageAssembler.BuildCore` (`:893`) | `BuildFileIO.Open` → `InPlaceImage.Open` |
| 19 | `ProsperoPs5InnerImageAssembler.BuildImage` (`:2672`, `:2688`) | both `BuildFileIO.Open` → `InPlaceImage.Open`; retype the `FileStream` local at `:2672` to `Stream` |
| 20 | `ProsperoPs5InnerImageAssembler.BuildToSpool` (`:525`) | `deferLayout: true` → `InPlaceImage.DeferLayout()` |
| 21 | `ProsperoPs5InnerImageAssembler.MaterializePhysicalLayout` | head guard: `if (InPlaceImage.MaterializeInPlace(path, open, length)) return;` |
| 22 | `ProsperoOuterFileSource.OpenRead` | `BuildFileIO.Open` → `InPlaceImage.Open` |
| 23 | `ProsperoOuterPfsBuilder.BuildForPackageToFile` | output open: `FileMode.Create` → `OpenOrCreate`, `FileShare.None` → `FileShare.ReadWrite`; `new FileInfo(path).Length` → `InPlaceImage.LengthOf(path)` |

---

## File Structure

**Create**
- `FpkgVirtualSource/WindowStream.cs` — offset-translating `Stream` over a byte range of a real file. Sole responsibility: address translation and length bookkeeping.
- `FpkgVirtualSource/InPlaceImage.cs` — policy and façade: the `fpkgwin:` grammar, `Enabled`, the open/length/delete entry points the patch sites call, fallback state and logging.
- `FpkgVirtualSource/LayoutRelocator.cs` — the copy-on-write in-place materialiser and its staging map.
- `FpkgVirtualSource.Tests/WindowStreamTests.cs`
- `FpkgVirtualSource.Tests/InPlaceImageTests.cs`
- `FpkgVirtualSource.Tests/LayoutRelocatorTests.cs`
- `fpkg-cli.Tests/InPlaceParityTests.cs` — the end-to-end parity and peak-disk harness.

**Modify**
- `tools/VirtualSourcePatcher/Sites.cs` — import the new shim entry points into the `Shim` class.
- `tools/VirtualSourcePatcher/Rewrites.cs` — sites 16–23.
- `tools/VirtualSourcePatcher/VirtualSourcePatcherProgram.cs` — resolve the new methods and call the new rewrites.
- `fpkg-cli/Program.cs` — `--spool` flag, shim wiring next to the existing `CntEntryPolicy` wiring (`:808-818`), help text (`:182`), build banner (`:1235`).
- `README.md`, `CHANGELOG.md`, `NOTES.md` — document the change and retire the "peak temp" table's assumptions.

---

### Task 1: `WindowStream` — a file range that behaves like a file

**Files:**
- Create: `FpkgVirtualSource/WindowStream.cs`
- Test: `FpkgVirtualSource.Tests/WindowStreamTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal sealed class WindowStream : Stream` with `WindowStream(FileStream file, long baseOffset, bool truncateOnOpen)`, plus `long BaseOffset { get; }` and `long HighWaterMark { get; }` (the largest logical length ever set or written, which Task 3 publishes as the image length).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.IO;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class WindowStreamTests
{
    private static string TempFile(byte[] contents)
    {
        string path = Path.Combine(Path.GetTempPath(), "win-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, contents);
        return path;
    }

    [Fact]
    public void ReadsAndWritesAreOffsetByTheBase()
    {
        string path = TempFile([.. Enumerable.Repeat((byte)0xAA, 64), .. new byte[] { 1, 2, 3, 4 }]);
        using (var w = new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, false))
        {
            Assert.Equal(0, w.Position);
            Assert.Equal(4, w.Length);
            var buf = new byte[4];
            w.ReadExactly(buf);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf);

            w.Position = 0;
            w.Write([9, 9], 0, 2);
        }
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(0xAA, all[63]);            // the reserved head is untouched
        Assert.Equal(new byte[] { 9, 9, 3, 4 }, all[64..]);
    }

    [Fact]
    public void SetLengthIsRelativeToTheBaseAndNeverEatsTheHead()
    {
        string path = TempFile(new byte[128]);
        using (var w = new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, false))
        {
            w.SetLength(16);
            Assert.Equal(16, w.Length);
        }
        Assert.Equal(80, new FileInfo(path).Length);
    }

    [Fact]
    public void TruncateOnOpenClearsOnlyTheWindow()
    {
        string path = TempFile([.. Enumerable.Repeat((byte)0xAA, 64), .. new byte[] { 7, 7, 7, 7 }]);
        using (new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, true)) { }
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(64, all.Length);
        Assert.Equal(0xAA, all[63]);
    }

    [Fact]
    public void HighWaterMarkRemembersTheLargestLengthSeen()
    {
        string path = TempFile(new byte[64]);
        using var w = new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, true);
        w.Write(new byte[100], 0, 100);
        w.SetLength(40);
        Assert.Equal(40, w.Length);
        Assert.Equal(100, w.HighWaterMark);
    }

    [Fact]
    public void ReadsClampAtTheEndOfTheWindow()
    {
        string path = TempFile([.. new byte[64], .. new byte[] { 1, 2 }]);
        using var w = new WindowStream(File.Open(path, FileMode.Open, FileAccess.Read), 64, false);
        var buf = new byte[8];
        Assert.Equal(2, w.Read(buf, 0, 8));
        Assert.Equal(0, w.Read(buf, 0, 8));
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test FpkgVirtualSource.Tests --filter WindowStreamTests`
Expected: FAIL — `WindowStream` does not exist.

- [ ] **Step 3: Implement `WindowStream`**

```csharp
using System;
using System.IO;

namespace FpkgVirtualSource;

/// <summary>
/// A seekable read/write view of <paramref name="file"/> starting at <c>baseOffset</c>, presented
/// as if it were a file of its own. This is what lets LibProsperoPkg write the inner
/// <c>pfs_image.dat</c> straight into the final package's outer-PFS data region instead of into a
/// spool file: every offset the library computes from zero lands at <c>baseOffset + offset</c>.
///
/// <para>The bytes before <c>baseOffset</c> are the package's FIH header range and are never
/// touched — not by a write, not by SetLength, not by the Create-mode truncation, which clears
/// the window only.</para>
/// </summary>
internal sealed class WindowStream : Stream
{
    private readonly FileStream file;
    private long position;
    private bool disposed;

    internal WindowStream(FileStream file, long baseOffset, bool truncateOnOpen)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        this.file = file;
        BaseOffset = baseOffset;
        if (truncateOnOpen && file.Length > baseOffset)
            file.SetLength(baseOffset);
        HighWaterMark = Math.Max(0, file.Length - baseOffset);
    }

    internal long BaseOffset { get; }

    /// <summary>The largest logical length this window has ever held — the inner image's size.</summary>
    internal long HighWaterMark { get; private set; }

    public override bool CanRead => !disposed && file.CanRead;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => !disposed && file.CanWrite;
    public override long Length => Math.Max(0, file.Length - BaseOffset);

    public override long Position
    {
        get => position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long remaining = Length - position;
        if (remaining <= 0) return 0;
        int want = (int)Math.Min(buffer.Length, remaining);
        file.Position = BaseOffset + position;
        int read = file.Read(buffer[..want]);
        position += read;
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        file.Position = BaseOffset + position;
        file.Write(buffer);
        position += buffer.Length;
        HighWaterMark = Math.Max(HighWaterMark, position);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return position;
    }

    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        file.SetLength(BaseOffset + value);
        HighWaterMark = Math.Max(HighWaterMark, value);
    }

    public override void Flush() => file.Flush();

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            if (disposing) file.Dispose();
        }
        base.Dispose(disposing);
    }
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test FpkgVirtualSource.Tests --filter WindowStreamTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add FpkgVirtualSource/WindowStream.cs FpkgVirtualSource.Tests/WindowStreamTests.cs
git commit -m "feat: WindowStream, a file-range view for in-place package builds

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `LayoutRelocator` — replay the layout permutation in place

**Files:**
- Create: `FpkgVirtualSource/LayoutRelocator.cs`
- Test: `FpkgVirtualSource.Tests/LayoutRelocatorTests.cs`

**Interfaces:**
- Consumes: `WindowStream` from Task 1.
- Produces: `internal static class LayoutRelocator` with
  `internal static bool TryMaterialize(WindowStream target, Func<Stream> openSource, long length, long budgetBytes, Action<string>? log, out string? fallbackReason)`
  and `internal sealed class StagingSource : Stream` (the copy-on-write wrapper the relocator installs under the library's layout stream).

The library hands us `openSource` — a factory for its own `PhysicalLayoutStream`, which reads through a `WindowStream` we supplied. Task 3 arranges for that inner window to be the `StagingSource` instance created here, via `InPlaceImage.PendingStagingSource`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.IO;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class LayoutRelocatorTests
{
    /// <summary>A stand-in for the library's PhysicalLayoutStream: reads `extents` (destination
    /// order) out of the underlying source, exactly as the real one does.</summary>
    private sealed class FakeLayoutStream(Stream source, (long Src, long Len)[] extents) : Stream
    {
        private long pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => extents.Sum(e => e.Len);
        public override long Position { get => pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long walked = 0;
            foreach (var e in extents)
            {
                if (pos < walked + e.Len)
                {
                    long into = pos - walked;
                    int take = (int)Math.Min(count, e.Len - into);
                    source.Position = e.Src + into;
                    source.ReadExactly(buffer.AsSpan(offset, take));
                    pos += take;
                    return take;
                }
                walked += e.Len;
            }
            return 0;
        }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private static (string path, WindowStream window) Window(byte[] body)
    {
        string path = Path.Combine(Path.GetTempPath(), "relo-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, [.. new byte[64], .. body]);
        return (path, new WindowStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite), 64, false));
    }

    // A swap is the case a naive forward copy gets wrong: writing block 0 destroys the
    // source of block 1 before it has been read.
    [Fact]
    public void SwappedExtentsSurviveTheInPlacePass()
    {
        byte[] body = [.. Enumerable.Repeat((byte)1, 16), .. Enumerable.Repeat((byte)2, 16)];
        var (path, window) = Window(body);
        var staging = new LayoutRelocator.StagingSource(window);
        bool ok = LayoutRelocator.TryMaterialize(
            window, () => new FakeLayoutStream(staging, [(16, 16), (0, 16)]), 32, 1 << 20, null, out var why);
        window.Dispose();

        Assert.True(ok, why);
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(Enumerable.Repeat((byte)2, 16), all[64..80]);
        Assert.Equal(Enumerable.Repeat((byte)1, 16), all[80..96]);
    }

    [Fact]
    public void ForwardCompactionSurvivesAndTruncates()
    {
        byte[] body = [.. Enumerable.Repeat((byte)1, 16), .. new byte[16], .. Enumerable.Repeat((byte)3, 16)];
        var (path, window) = Window(body);
        var staging = new LayoutRelocator.StagingSource(window);
        bool ok = LayoutRelocator.TryMaterialize(
            window, () => new FakeLayoutStream(staging, [(0, 16), (32, 16)]), 32, 1 << 20, null, out _);
        window.Dispose();

        Assert.True(ok);
        byte[] all = File.ReadAllBytes(path);
        Assert.Equal(96, all.Length);                       // 64 head + 32 image
        Assert.Equal(Enumerable.Repeat((byte)3, 16), all[80..96]);
    }

    [Fact]
    public void ExceedingTheStagingBudgetFallsBackWithoutTouchingTheFile()
    {
        byte[] body = [.. Enumerable.Repeat((byte)1, 16), .. Enumerable.Repeat((byte)2, 16)];
        var (path, window) = Window(body);
        var staging = new LayoutRelocator.StagingSource(window);
        bool ok = LayoutRelocator.TryMaterialize(
            window, () => new FakeLayoutStream(staging, [(16, 16), (0, 16)]), 32, 8, null, out var why);
        window.Dispose();

        Assert.False(ok);
        Assert.Contains("budget", why);
        Assert.Equal([.. new byte[64], .. body], File.ReadAllBytes(path));   // unchanged
    }

    [Fact]
    public void IdentityLayoutIsAByteForByteNoOp()
    {
        byte[] body = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        var (path, window) = Window(body);
        var staging = new LayoutRelocator.StagingSource(window);
        bool ok = LayoutRelocator.TryMaterialize(
            window, () => new FakeLayoutStream(staging, [(0, 32)]), 32, 1 << 20, null, out _);
        window.Dispose();

        Assert.True(ok);
        Assert.Equal([.. new byte[64], .. body], File.ReadAllBytes(path));
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test FpkgVirtualSource.Tests --filter LayoutRelocatorTests`
Expected: FAIL — `LayoutRelocator` does not exist.

- [ ] **Step 3: Implement `LayoutRelocator`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace FpkgVirtualSource;

/// <summary>
/// Replays LibProsperoPkg's deferred inner-layout permutation in place, inside the final package,
/// instead of copying the spool through it into a second file.
///
/// <para><b>Why this is not just a copy loop.</b> The library picks its physical layout
/// (<c>SelectPhysicalLayout</c>: gap coalescing, then relocation alignment) only after the data is
/// written, so the move is a permutation of byte ranges within one image. Destination is usually
/// below source — but <c>Coalesce</c> appends leftovers to a running end that can sit above a
/// movable extent's source, so destination &gt; source is possible and a naive forward copy would
/// overwrite bytes the reader has not consumed yet.</para>
///
/// <para><b>Copy-on-write, no knowledge of the library's extents.</b> We never see the extent
/// table — it is an internal type. Instead <see cref="StagingSource"/> sits under the library's own
/// layout stream and sees every physical read, while this pass, before overwriting destination
/// range D, hands the pre-image of D to the staging map. A later read landing in a staged range is
/// served from memory; a staged range is dropped once fully consumed. Live staged bytes are the
/// displaced working set, which is <c>MovedBytes</c>-shaped and small; if it ever exceeds the
/// budget we abandon the pass, leaving the file untouched, and the caller falls back to a spool.
/// </para>
/// </summary>
internal static class LayoutRelocator
{
    /// <summary>The block granularity the pass copies in. Matches the outer-PFS block size.</summary>
    private const int ChunkSize = 65536;

    internal static bool TryMaterialize(
        WindowStream target,
        Func<Stream> openSource,
        long length,
        long budgetBytes,
        Action<string>? log,
        out string? fallbackReason)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(openSource);
        fallbackReason = null;

        var staging = StagingSource.Current
            ?? throw new InvalidOperationException("No staging source was installed for the relocation pass.");
        staging.Begin(budgetBytes);

        byte[] buffer = new byte[ChunkSize];
        long written = 0;
        try
        {
            using Stream source = openSource();
            while (written < length)
            {
                int want = (int)Math.Min(ChunkSize, length - written);
                // Stage the pre-image of what we are about to overwrite, THEN read, THEN write:
                // the read may legitimately come from the range we just staged.
                staging.PreserveBeforeOverwrite(written, want);
                source.ReadExactly(buffer.AsSpan(0, want));
                target.Position = written;
                target.Write(buffer.AsSpan(0, want));
                written += want;
                staging.Release(written);
            }
        }
        catch (StagingBudgetExceededException ex)
        {
            fallbackReason = ex.Message;
            staging.Abandon();
            return false;
        }

        target.SetLength(length);
        staging.End();
        log?.Invoke($"Inner layout write: materialised in place ({length:N0} bytes, peak staging " +
                    $"{staging.PeakStagedBytes:N0} bytes).");
        return true;
    }

    private sealed class StagingBudgetExceededException(string message) : Exception(message);

    /// <summary>
    /// The window the library's layout stream reads through, with a copy-on-write map in front of
    /// it so a read can still see bytes the relocation pass has already overwritten.
    /// </summary>
    internal sealed class StagingSource(WindowStream inner) : Stream
    {
        [ThreadStatic] private static StagingSource? current;
        private readonly SortedList<long, byte[]> staged = new();
        private long budget;
        private long stagedBytes;
        private bool active;

        internal static StagingSource? Current => current;
        internal long PeakStagedBytes { get; private set; }

        internal StagingSource Install() { current = this; return this; }

        internal void Begin(long budgetBytes)
        {
            budget = budgetBytes;
            active = true;
            staged.Clear();
            stagedBytes = 0;
        }

        internal void End() { active = false; staged.Clear(); stagedBytes = 0; }
        internal void Abandon() => End();

        /// <summary>Copies the current bytes of [offset, offset+count) aside before they are lost.</summary>
        internal void PreserveBeforeOverwrite(long offset, int count)
        {
            if (!active) return;
            long available = Math.Min(count, Math.Max(0, inner.Length - offset));
            if (available <= 0) return;
            if (stagedBytes + available > budget)
                throw new StagingBudgetExceededException(
                    $"in-place layout relocation needs more than the {budget:N0}-byte staging budget");

            byte[] copy = new byte[available];
            inner.Position = offset;
            inner.ReadExactly(copy);
            staged[offset] = copy;
            stagedBytes += available;
            PeakStagedBytes = Math.Max(PeakStagedBytes, stagedBytes);
        }

        /// <summary>Drops staged ranges that lie entirely below the reader's consumed frontier.</summary>
        internal void Release(long consumedUpTo)
        {
            if (!active) return;
            while (staged.Count != 0)
            {
                long key = staged.Keys[0];
                byte[] value = staged.Values[0];
                if (key + value.Length > consumedUpTo) break;
                staged.RemoveAt(0);
                stagedBytes -= value.Length;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            // A staged range wins over the file: the file may already hold relocated bytes.
            foreach (var (start, bytes) in staged)
            {
                if (Position >= start && Position < start + bytes.Length)
                {
                    int take = (int)Math.Min(buffer.Length, start + bytes.Length - Position);
                    bytes.AsSpan((int)(Position - start), take).CopyTo(buffer);
                    Position += take;
                    return take;
                }
                if (start > Position)
                {
                    // Do not read past the start of a staged range in one go.
                    int clamp = (int)Math.Min(buffer.Length, start - Position);
                    return ReadFromFile(buffer[..clamp]);
                }
            }
            return ReadFromFile(buffer);
        }

        private int ReadFromFile(Span<byte> buffer)
        {
            inner.Position = Position;
            int read = inner.Read(buffer);
            Position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && ReferenceEquals(current, this)) current = null;
            base.Dispose(disposing);
        }
    }
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test FpkgVirtualSource.Tests --filter LayoutRelocatorTests`
Expected: PASS, 4 tests. If `SwappedExtentsSurviveTheInPlacePass` fails with block-2 data in both halves, the staging map is being consulted after the write rather than before — `PreserveBeforeOverwrite` must run before `ReadExactly`.

- [ ] **Step 5: Commit**

```bash
git add FpkgVirtualSource/LayoutRelocator.cs FpkgVirtualSource.Tests/LayoutRelocatorTests.cs
git commit -m "feat: in-place layout relocation with copy-on-write staging

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `InPlaceImage` — the façade the patch sites call

**Files:**
- Create: `FpkgVirtualSource/InPlaceImage.cs`
- Test: `FpkgVirtualSource.Tests/InPlaceImageTests.cs`

**Interfaces:**
- Consumes: `WindowStream` (Task 1), `LayoutRelocator` (Task 2).
- Produces — every one of these is an IL patch target, so the signatures are contractual:
  - `public static bool Enabled { get; set; }` (default `false`; the CLI turns it on)
  - `public static long MaxRelocationBytes { get; set; }` (default `512L * 1024 * 1024`)
  - `public static Action<string>? Log { get; set; }`
  - `public static string SpoolPathFor(string spoolPath, string outputPath, bool prepositioned)` — site 16
  - `public static bool IsWindow(string? path)` — site 17
  - `public static string GuardFullPath(string path)` — site 17
  - `public static Stream Open(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)` — sites 18, 19, 22
  - `public static bool DeferLayout()` — site 20
  - `public static bool MaterializeInPlace(string path, Func<Stream> open, long length)` — site 21
  - `public static long LengthOf(string path)` — site 23
  - `public static bool TryDeleteWindow(string path)` — site 16's cleanup
  - `public static string? FallbackReason { get; }` and `public static void Reset()` — CLI reporting

- [ ] **Step 1: Write the failing tests**

```csharp
using System.IO;
using Xunit;

namespace FpkgVirtualSource.Tests;

public class InPlaceImageTests : IDisposable
{
    public InPlaceImageTests() => InPlaceImage.Reset();
    public void Dispose() => InPlaceImage.Reset();

    [Fact]
    public void DisabledIsCompletelyInert()
    {
        InPlaceImage.Enabled = false;
        Assert.Equal("/tmp/spool.dat",
            InPlaceImage.SpoolPathFor("/tmp/spool.dat", "/out/x.pkg", prepositioned: true));
        Assert.True(InPlaceImage.DeferLayout());
        Assert.False(InPlaceImage.IsWindow("/tmp/spool.dat"));
    }

    [Fact]
    public void SpoolPathBecomesAWindowOnlyWhenPrepositioned()
    {
        InPlaceImage.Enabled = true;
        Assert.Equal("/tmp/spool.dat",
            InPlaceImage.SpoolPathFor("/tmp/spool.dat", "/out/x.pkg", prepositioned: false));

        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", "/out/x.pkg", prepositioned: true);
        Assert.Equal("fpkgwin:/out/x.pkg@65536", window);
        Assert.True(InPlaceImage.IsWindow(window));
        Assert.False(InPlaceImage.DeferLayout());
    }

    [Fact]
    public void GuardFullPathLeavesWindowsAloneAndResolvesEverythingElse()
    {
        Assert.Equal("fpkgwin:/out/x.pkg@65536", InPlaceImage.GuardFullPath("fpkgwin:/out/x.pkg@65536"));
        Assert.Equal(Path.GetFullPath("rel.dat"), InPlaceImage.GuardFullPath("rel.dat"));
    }

    [Fact]
    public void OpenCreateReservesTheHeadAndReportsTheWindowLength()
    {
        InPlaceImage.Enabled = true;
        string pkg = Path.Combine(Path.GetTempPath(), "ip-" + Guid.NewGuid().ToString("N") + ".pkg");
        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", pkg, prepositioned: true);

        using (var s = InPlaceImage.Open(window, FileMode.Create, FileAccess.ReadWrite,
                                         FileShare.None, 1 << 20, FileOptions.SequentialScan))
        {
            s.Write(new byte[131072], 0, 131072);
            s.SetLength(131072);
        }

        Assert.Equal(65536 + 131072, new FileInfo(pkg).Length);
        Assert.Equal(131072, InPlaceImage.LengthOf(window));
        File.Delete(pkg);
    }

    [Fact]
    public void OpenFallsThroughToARealFileForOrdinaryPaths()
    {
        string path = Path.Combine(Path.GetTempPath(), "plain-" + Guid.NewGuid().ToString("N"));
        using (var s = InPlaceImage.Open(path, FileMode.Create, FileAccess.ReadWrite,
                                         FileShare.None, 4096, FileOptions.None))
            Assert.IsType<FileStream>(s);
        File.Delete(path);
    }

    [Fact]
    public void TryDeleteWindowNeverDeletesTheOutputPackage()
    {
        InPlaceImage.Enabled = true;
        string pkg = Path.Combine(Path.GetTempPath(), "keep-" + Guid.NewGuid().ToString("N") + ".pkg");
        File.WriteAllBytes(pkg, new byte[16]);
        string window = InPlaceImage.SpoolPathFor("/tmp/spool.dat", pkg, prepositioned: true);

        Assert.True(InPlaceImage.TryDeleteWindow(window));   // handled — and a no-op
        Assert.True(File.Exists(pkg));
        Assert.False(InPlaceImage.TryDeleteWindow("/tmp/spool.dat"));   // not ours; caller deletes
        File.Delete(pkg);
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test FpkgVirtualSource.Tests --filter InPlaceImageTests`
Expected: FAIL — `InPlaceImage` does not exist.

- [ ] **Step 3: Implement `InPlaceImage`**

```csharp
using System;
using System.Globalization;
using System.IO;

namespace FpkgVirtualSource;

/// <summary>
/// Builds the package in one file instead of two.
///
/// <para>Stock LibProsperoPkg writes the inner <c>pfs_image.dat</c> to a spool in
/// <c>--temp-dir</c> (stage 1) and then reads it back through the outer-PFS writer, which encrypts
/// and hashes it into the final package at offset 0x10000 (stage 3). Both files are full size at
/// the same moment, so a build needs twice the package size in free space.</para>
///
/// <para>With this shim the spool path becomes a <c>fpkgwin:&lt;package&gt;@65536</c> window: stage 1
/// writes the plaintext image straight into the package's outer-PFS data region, and stage 3 reads
/// block <c>j</c> from the very offset it writes it back to. That is safe because the outer writer
/// consumes its source strictly sequentially, one 64 KiB block at a time, always ahead of its own
/// writes, and every structural block it emits is allocated after the data blocks.</para>
///
/// <para>Inert unless the CLI sets <see cref="Enabled"/>: every entry point returns its input
/// unchanged, so an unconfigured or unpatched build behaves exactly as before.</para>
/// </summary>
public static class InPlaceImage
{
    /// <summary>The scheme marking a path as a byte range of another file.</summary>
    public const string Scheme = "fpkgwin:";

    /// <summary>Package offset of the outer PFS, and so of outer data block 0.</summary>
    public const long OuterPfsOffset = 65536;

    /// <summary>Whether the CLI has switched the single-file build on.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Ceiling on the displaced bytes an in-place layout relocation may hold in memory.</summary>
    public static long MaxRelocationBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Where notices go — the build's normal output, because this changes where bytes land.</summary>
    public static Action<string>? Log { get; set; } = Console.Out.WriteLine;

    /// <summary>Why the in-place path gave up, if it did. Non-null means the build used a spool.</summary>
    public static string? FallbackReason { get; private set; }

    private static string? windowPath;
    private static long windowLength;

    /// <summary>Clears per-build state. The CLI calls this before every build.</summary>
    public static void Reset()
    {
        Enabled = false;
        FallbackReason = null;
        windowPath = null;
        windowLength = 0;
    }

    /// <summary>
    /// Site 16. Redirects stage 1's spool to the final package's PFS data region. Only when the
    /// outer PFS is already prepositioned into the output (<c>PrepositionedFihOutputPath</c>) —
    /// otherwise stage 3 would not be writing where we put the plaintext.
    /// </summary>
    public static string SpoolPathFor(string spoolPath, string outputPath, bool prepositioned)
    {
        if (!Enabled || !prepositioned || string.IsNullOrWhiteSpace(outputPath))
            return spoolPath;
        windowPath = Scheme + Path.GetFullPath(outputPath) + "@" +
                     OuterPfsOffset.ToString(CultureInfo.InvariantCulture);
        windowLength = 0;
        Log?.Invoke("Single-file build: the inner image is written directly into the package at " +
                    $"0x{OuterPfsOffset:X}; no spool copy is made.");
        return windowPath;
    }

    /// <summary>True for a path this shim owns.</summary>
    public static bool IsWindow(string? path) =>
        path is not null && path.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>Site 17. <c>Path.GetFullPath</c> would mangle a window path into a real one.</summary>
    public static string GuardFullPath(string path) => IsWindow(path) ? path : Path.GetFullPath(path);

    /// <summary>Site 20. The layout permutation must be materialised, not deferred to the reader.</summary>
    public static bool DeferLayout() => !Enabled || windowPath is null;

    /// <summary>Sites 18, 19, 22. A window path opens as a range of the package; anything else is a file.</summary>
    public static Stream Open(string path, FileMode mode, FileAccess access, FileShare share,
                              int bufferSize, FileOptions options)
    {
        if (!TryParse(path, out string file, out long offset))
            return new FileStream(path, mode, access, share, bufferSize, options);

        // The package is opened shared: stage 3 holds its own handle on the same file while our
        // read window feeds it. Create must clear the window only, never the reserved head.
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var fs = new FileStream(file, mode == FileMode.Create ? FileMode.OpenOrCreate : mode,
                                access == FileAccess.Read ? FileAccess.Read : FileAccess.ReadWrite,
                                FileShare.ReadWrite, bufferSize, options);
        if (fs.Length < offset && access != FileAccess.Read) fs.SetLength(offset);
        var window = new WindowStream(fs, offset, truncateOnOpen: mode == FileMode.Create);
        if (access == FileAccess.Read)
            return new LayoutRelocator.StagingSource(window).Install();
        TrackedWindow = window;
        return window;
    }

    /// <summary>The most recent writable window, whose high-water mark is the inner image length.</summary>
    internal static WindowStream? TrackedWindow
    {
        get;
        set { field = value; if (value is not null) windowLength = Math.Max(windowLength, value.HighWaterMark); }
    }

    /// <summary>Site 23. The declared length of a window, which no <c>FileInfo</c> can report.</summary>
    public static long LengthOf(string path)
    {
        if (!IsWindow(path)) return new FileInfo(path).Length;
        long tracked = TrackedWindow?.HighWaterMark ?? 0;
        return Math.Max(windowLength, tracked);
    }

    /// <summary>Site 21. Replays the layout permutation inside the package instead of into a copy.</summary>
    public static bool MaterializeInPlace(string path, Func<Stream> open, long length)
    {
        if (!TryParse(path, out _, out _)) return false;

        var window = TrackedWindow
            ?? throw new InvalidOperationException("No in-place window is open for the relocation pass.");
        if (LayoutRelocator.TryMaterialize(window, open, length, MaxRelocationBytes, Log, out string? why))
        {
            windowLength = length;
            return true;
        }

        FallbackReason = why;
        Log?.Invoke("warning: " + why + "; falling back to a spool copy for this build, " +
                    "which needs about twice the package size in free space.");
        return false;
    }

    /// <summary>Cleanup guard: a window path must never reach <c>File.Delete</c>.</summary>
    public static bool TryDeleteWindow(string path) => IsWindow(path);

    private static bool TryParse(string? path, out string file, out long offset)
    {
        file = string.Empty;
        offset = 0;
        if (!IsWindow(path)) return false;
        string rest = path![Scheme.Length..];
        int at = rest.LastIndexOf('@');
        if (at <= 0 || !long.TryParse(rest[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            throw new InvalidDataException("Malformed in-place window path: " + path);
        file = rest[..at];
        return true;
    }
}
```

Note: `TrackedWindow` uses the `field` keyword (C# 14, available on .NET 10). If the project's `LangVersion` rejects it, use a plain backing field.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test FpkgVirtualSource.Tests --filter InPlaceImageTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Pin the NAPS precondition in a test**

The in-place path is only safe while nothing re-reads the plaintext image after stage 3, which holds because the CLI never sets `NapsIntegrityProvider`. Pin it so a future CLI change cannot quietly break it:

```csharp
    [Fact]
    public void TheCliNeverSetsANapsIntegrityProvider()
    {
        // ProsperoNapsMeta re-reads the physical inner image for obdg/obcc when an integrity
        // provider is configured, and BuildFromFsTreeToSpool then materialises a third copy.
        // Both are incompatible with building in place. The CLI must never set it.
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "fpkg-cli", "Program.cs"));
        Assert.DoesNotContain("NapsIntegrityProvider", program);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
```

- [ ] **Step 6: Run the tests and commit**

Run: `dotnet test FpkgVirtualSource.Tests --filter InPlaceImageTests`
Expected: PASS, 7 tests.

```bash
git add FpkgVirtualSource/InPlaceImage.cs FpkgVirtualSource.Tests/InPlaceImageTests.cs
git commit -m "feat: InPlaceImage facade for single-file package builds

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Patch sites 17–21 — the inner-image assembler

**Files:**
- Modify: `tools/VirtualSourcePatcher/Sites.cs` (import the new shim methods)
- Modify: `tools/VirtualSourcePatcher/Rewrites.cs` (add `InPlaceInnerImage`)
- Modify: `tools/VirtualSourcePatcher/VirtualSourcePatcherProgram.cs` (resolve + call)

**Interfaces:**
- Consumes: `InPlaceImage.GuardFullPath`, `.Open`, `.DeferLayout`, `.MaterializeInPlace` from Task 3.
- Produces: a patched `LibProsperoPkg.patched.dll` in which the assembler resolves window paths.

- [ ] **Step 1: Dump the sites and confirm the shapes you are about to patch**

```bash
cd /Users/rrocha/Developer/fpkg-cli
dotnet run --project tools/VirtualSourcePatcher -- --dump LibProsperoPkg.dll \
  'ProsperoPs5InnerImageAssembler.BuildToSpool'
dotnet run --project tools/VirtualSourcePatcher -- --dump LibProsperoPkg.dll \
  'ProsperoPs5InnerImageAssembler.MaterializePhysicalLayout'
```

Expected in `BuildToSpool`: a `call Path::GetFullPath`, then a `ldc.i4.1` feeding the `deferLayout` argument of the `BuildCore` call. Expected in `MaterializePhysicalLayout`: a three-argument instance method `(string, Func<Stream>, long)` returning void.

- [ ] **Step 2: Add the shim imports to `Sites.Shim`**

```csharp
            // Sites 16-23. The single-file build: the inner image is written into the package's
            // outer-PFS data region and transformed in place, so no spool copy exists. Every one
            // of these is inert while InPlaceImage.Enabled is false.
            var inPlace = Type(shim, "FpkgVirtualSource.InPlaceImage");
            InPlaceSpoolPath   = importer.Import(Method(inPlace, "SpoolPathFor", "System.String", 3));
            InPlaceGuardPath   = importer.Import(Method(inPlace, "GuardFullPath", "System.String", 1));
            InPlaceOpen        = importer.Import(Method(inPlace, "Open", "System.IO.Stream", 6));
            InPlaceDeferLayout = importer.Import(Method(inPlace, "DeferLayout", "System.Boolean", 0));
            InPlaceMaterialize = importer.Import(Method(inPlace, "MaterializeInPlace", "System.Boolean", 3));
            InPlaceLengthOf    = importer.Import(Method(inPlace, "LengthOf", "System.Int64", 1));
            InPlaceTryDelete   = importer.Import(Method(inPlace, "TryDeleteWindow", "System.Boolean", 1));
```

with the matching `internal IMethod X { get; }` properties.

- [ ] **Step 3: Write the rewrite**

```csharp
    /// <summary>
    /// Sites 17-21. Makes ProsperoPs5InnerImageAssembler write its inner image through a
    /// <c>fpkgwin:</c> window — the final package's outer-PFS data region — instead of a spool file.
    ///
    /// <para>17: <c>Path.GetFullPath(spoolPath)</c> would mangle the window path into a real one.
    /// 18/19: the three <c>BuildFileIO.Open</c> calls that touch the spool route through the shim;
    /// the <c>FileStream</c> local at the metadata-write site is retyped to <c>Stream</c>, because
    /// a window is not a <c>FileStream</c>. 20: <c>deferLayout: true</c> becomes a shim call, so
    /// the layout permutation is materialised rather than replayed by the reader. 21:
    /// <c>MaterializePhysicalLayout</c> gets a head guard that does the move in place.</para>
    ///
    /// <para>Inert when disabled: <c>GuardFullPath</c> and <c>Open</c> fall through to
    /// <c>Path.GetFullPath</c> and a plain <c>FileStream</c>, <c>DeferLayout</c> returns true (the
    /// stock constant) and <c>MaterializeInPlace</c> returns false, so the original body runs.</para>
    /// </summary>
    internal static void InPlaceInnerImage(TypeDef assembler, MethodDef buildToSpool,
                                           MethodDef buildImage, MethodDef buildCore,
                                           MethodDef materialize, Sites.Shim shim)
```

Implementation notes for each leg (write them as separate private helpers, mirroring `GuardGetFullPath` and `RedirectFileStream`):

- **17** — find the single `call System.String System.IO.Path::GetFullPath(System.String)` in `buildToSpool`; `Sites.Expect` there is exactly one; replace its operand with `shim.InPlaceGuardPath`. Same shape as the existing `GuardGetFullPath`, but a straight operand swap rather than a branch, because the shim method has the same signature.
- **18/19** — in `buildCore` and `buildImage`, find every `call FileStream BuildFileIO::Open(string, FileMode, FileAccess, FileShare, int32, FileOptions)`. `Sites.Expect` exactly one in `buildCore` and exactly two in `buildImage`. Replace each operand with `shim.InPlaceOpen`. Then, in `buildImage`, find the local whose type is `System.IO.FileStream` and which is the store target of the first of those calls; set `local.Type = module.CorLibTypes.GetTypeRef("System.IO", "Stream").ToTypeSig()` and retarget every `callvirt` whose declaring type is `System.IO.FileStream` in that method to the same member on `System.IO.Stream`. `Sites.Expect` that the retargeted members are exactly `set_Position`, `Write`, `SetLength` and `Dispose`.
- **20** — in `buildToSpool`, locate the `call ... BuildCore(...)`; the instruction four positions back must be `ldc.i4.1` (the `deferLayout` argument — `Sites.Expect` it). Replace it with `call bool InPlaceImage::DeferLayout()`.
- **21** — prepend to `materialize`, using the same three-instruction head guard as `SuppressAboutRightSprx`, but with three arguments:
  `ldarg.1; ldarg.2; ldarg.3; call InPlaceImage::MaterializeInPlace; brfalse <original first>; ret`.
  `Sites.Expect(!materialize.IsStatic)` and that nothing branches to or handles at the original first instruction.

- [ ] **Step 4: Wire it into the patcher program**

```csharp
            var buildToSpool = Sites.Method(assembler, "BuildToSpool",
                "LibProsperoPkg.PFS.ProsperoPs5InnerImageResult", 3);
            var buildImage = Sites.Method(assembler, "BuildImage", "System.Byte[]", 15);
            var buildCore = Sites.Method(assembler, "BuildCore",
                "LibProsperoPkg.PFS.ProsperoPs5InnerImageResult", 4);
            var materialize = Sites.Method(assembler, "MaterializePhysicalLayout", "System.Void", 3);
            ...
            Rewrites.InPlaceInnerImage(assembler, buildToSpool, buildImage, buildCore, materialize, shim);
```

- [ ] **Step 5: Run the patch and prove it still produces a working, unchanged build**

```bash
./fpkg patch
dotnet test fpkg-cli.Tests    # SyntheticPackage builds a real package through the patched dll
```

Expected: the patch reports its sites without a shape failure, and the whole existing suite passes. Nothing has switched in-place on yet, so this proves the sites are inert.

- [ ] **Step 6: Commit**

```bash
git add tools/VirtualSourcePatcher
git commit -m "feat: patch sites 17-21, inner-image assembler writes through a window

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Patch sites 22–23 — the outer-PFS writer

**Files:**
- Modify: `tools/VirtualSourcePatcher/Rewrites.cs`, `VirtualSourcePatcherProgram.cs`

**Interfaces:**
- Consumes: `InPlaceImage.Open`, `.LengthOf`.
- Produces: an outer-PFS writer that reads its source from the package it is writing, and that grows the package rather than truncating it.

- [ ] **Step 1: Dump and confirm the shapes**

```bash
dotnet run --project tools/VirtualSourcePatcher -- --dump LibProsperoPkg.dll \
  'ProsperoOuterPfsBuilder.BuildForPackageToFile'
dotnet run --project tools/VirtualSourcePatcher -- --dump LibProsperoPkg.dll \
  'ProsperoOuterFileSource.OpenRead'
```

Expected in `BuildForPackageToFile`: one `BuildFileIO::Open` whose `FileMode` argument is a ternary over `FileMode.Create`/`FileMode.CreateNew` and whose `FileShare` argument is `ldc.i4.0` (`FileShare.None`), and one `newobj FileInfo::.ctor(string)` followed by `callvirt FileInfo::get_Length`.

- [ ] **Step 2: Write the rewrite**

```csharp
    /// <summary>
    /// Sites 22-23. Lets the outer-PFS writer transform the inner image in place.
    ///
    /// <para>22: <c>ProsperoOuterFileSource.OpenRead</c> opens its source through the shim, so a
    /// window path yields a read view of the package's own data region.</para>
    ///
    /// <para>23: the output file must not be truncated — stage 1 already wrote the plaintext image
    /// into it — so <c>FileMode.Create</c> becomes <c>FileMode.OpenOrCreate</c>; and the write
    /// handle must tolerate our read handle on the same file, so <c>FileShare.None</c> becomes
    /// <c>FileShare.ReadWrite</c>. The later <c>image.SetLength(outputOffset + total)</c> only ever
    /// grows the file from there. Finally <c>new FileInfo(path).Length</c> cannot measure a window,
    /// so it routes to <c>InPlaceImage.LengthOf</c>.</para>
    ///
    /// <para>Inert when disabled: no path is ever a window, so <c>Open</c> and <c>LengthOf</c>
    /// behave as the calls they replaced. The two constants change unconditionally — see the test
    /// in Task 8 pinning that a <c>--spool</c> build is still byte-identical.</para>
    /// </summary>
    internal static void InPlaceOuterWriter(MethodDef buildForPackageToFile, MethodDef openRead,
                                            Sites.Shim shim)
```

Legs:
- **22** — single `BuildFileIO::Open` operand swap to `shim.InPlaceOpen` (`Sites.Expect` exactly one call, and that the result is stored to a `System.IO.Stream` local, so no retyping is needed).
- **23a** — in `buildForPackageToFile`, find the `BuildFileIO::Open` call; walk back to its six argument pushes. `Sites.Expect` the `FileShare` push is `ldc.i4.0`; replace it with `ldc.i4.3` (`FileShare.ReadWrite`). `Sites.Expect` the `FileMode` push is a branchy ternary ending in `ldc.i4.2` (`Create`) on one arm and `ldc.i4.1` (`CreateNew`) on the other; replace the `ldc.i4.2` with `ldc.i4.4` (`OpenOrCreate`) and the `ldc.i4.1` with `ldc.i4.4` as well — with the shim disabled the file never pre-exists, so `OpenOrCreate` and `CreateNew` behave identically, and a single mode keeps the two arms honest.
- **23b** — find `newobj FileInfo::.ctor(string)` + `callvirt FileInfo::get_Length`; `Sites.Expect` exactly one such pair; replace the pair with a single `call int64 InPlaceImage::LengthOf(string)` (nop the `newobj`, retarget the `callvirt`).

- [ ] **Step 3: Patch, then prove inertness again**

```bash
./fpkg patch && dotnet test fpkg-cli.Tests
```

Expected: PASS. The in-place path is still off, so this is purely a "did I break the stock path" gate.

- [ ] **Step 4: Commit**

```bash
git add tools/VirtualSourcePatcher
git commit -m "feat: patch sites 22-23, outer-PFS writer transforms in place

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Patch site 16 — redirect the spool, and keep cleanup off the package

**Files:**
- Modify: `tools/VirtualSourcePatcher/Rewrites.cs`, `VirtualSourcePatcherProgram.cs`

**Interfaces:**
- Consumes: `InPlaceImage.SpoolPathFor`, `.TryDeleteWindow`.
- Produces: the switch that actually turns the feature on for a build.

- [ ] **Step 1: Dump `BuildPublisherImage` and locate the three anchors**

```bash
dotnet run --project tools/VirtualSourcePatcher -- --dump LibProsperoPkg.dll \
  'ProsperoPkgBuilder.BuildPublisherImage'
```

Anchors: (a) the `ldstr ".pfs_image.dat"` concatenation that produces the spool path local; (b) the assignment of the prepositioned-output local from `PrepositionedFihOutputPath`; (c) the `TryDeleteTemp` calls in the `finally` blocks.

- [ ] **Step 2: Write the rewrite**

```csharp
    /// <summary>
    /// Site 16. The one switch: stage 1's spool path becomes
    /// <c>InPlaceImage.SpoolPathFor(spool, output, prepositioned)</c>, which returns a
    /// <c>fpkgwin:</c> window when the CLI has enabled the single-file build AND the outer PFS is
    /// already prepositioned into the final package — the only configuration in which the
    /// plaintext would land where stage 3 is going to read it.
    ///
    /// <para>The call is inserted after BOTH the spool local and the prepositioned-output local
    /// have been assigned, so all three arguments are live. Cleanup is guarded separately:
    /// <c>TryDeleteTemp</c> must never be handed a window path, because the file behind it is the
    /// package the build just produced.</para>
    ///
    /// <para>Inert when disabled: <c>SpoolPathFor</c> returns its first argument unchanged.</para>
    /// </summary>
    internal static void InPlaceSpoolRedirect(MethodDef buildPublisherImage, MethodDef tryDeleteTemp,
                                              Sites.Shim shim)
```

Legs:
- Locate the `stloc` of the spool local (the one fed by `ldstr ".pfs_image.dat"`). `Sites.Expect` exactly one.
- Locate the `stloc` of the prepositioned-output local (fed by the `PrepositionedFihOutputPath` getter through `Path.GetFullPath`). `Sites.Expect` it is assigned after the spool local.
- Immediately after the later of the two, insert: `ldloc <spool>; ldloc <output>; ldloc <flag2>; call InPlaceImage::SpoolPathFor; stloc <spool>`.
- In `tryDeleteTemp`, prepend `ldarg.0; call InPlaceImage::TryDeleteWindow; brtrue <new ret>` using the same mutate-the-`ret` technique documented on site 13, so existing branches stay bound.

- [ ] **Step 3: Verify the patch reports every site and the stock path still builds**

```bash
./fpkg patch && dotnet test fpkg-cli.Tests
```

Expected: PASS. In-place is still disabled at runtime (no CLI flag yet), so this remains an inertness gate.

- [ ] **Step 4: Commit**

```bash
git add tools/VirtualSourcePatcher
git commit -m "feat: patch site 16, redirect the inner-image spool into the package

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: CLI wiring — default on, `--spool` opts out

**Files:**
- Modify: `fpkg-cli/Program.cs` (flag parsing near `:741`, shim wiring near `:808`, help near `:182`, banner near `:1235`)

**Interfaces:**
- Consumes: `InPlaceImage.Enabled`, `.Log`, `.FallbackReason`, `.Reset()`.
- Produces: the user-facing behaviour.

- [ ] **Step 1: Write the failing test**

```csharp
// fpkg-cli.Tests/InPlaceFlagTests.cs
[Fact]
public void SpoolFlagIsAdvertisedAndRecognised()
{
    string help = RunFpkg("help", []);
    Assert.Contains("--spool", help);
    Assert.Contains("unknown option", RunFpkg("build", ["--source", ".", "--out", ".", "--in-place"]));
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test fpkg-cli.Tests --filter InPlaceFlagTests`
Expected: FAIL — `--spool` is not in the help text.

- [ ] **Step 3: Implement the wiring**

Add `"spool"` to the build command's known-flag set, then next to the existing `CntEntryPolicy` wiring:

```csharp
        // The single-file build is the default: the inner image is written straight into the
        // package instead of into a full-size spool in --temp-dir, which halves the free space a
        // build needs. --spool restores the two-file path, and is also the parity oracle the
        // in-place path is tested against.
        FpkgVirtualSource.InPlaceImage.Reset();
        bool spool = flags.ContainsKey("spool");
        if (!spool && selection.Patched is null)
        {
            Console.WriteLine("warning: LibProsperoPkg is not patched, so the single-file build is " +
                              "unavailable; this build will use a spool and needs about twice the " +
                              "package size in free space. Run `fpkg patch` to enable it.");
            spool = true;
        }
        FpkgVirtualSource.InPlaceImage.Enabled = !spool;
```

Use whatever local the surrounding code already holds for the resolved patch selection in place of `selection.Patched` — `PatchStamp.Select` is what decides whether the patched DLL is in play, and the CLI must not enable in-place against a stock DLL (Review Focus 5).

Help text, under the existing `--temp-dir` entry:

```
  --spool                 build through a full-size inner-image spool in --temp-dir
                            (the pre-0.7 behaviour). The default writes the inner
                            image straight into the package, so a build needs about
                            the package's own size in free space rather than twice it.
```

And extend the banner so the chosen path is visible in every build log:

```csharp
        Console.WriteLine($"  temp:   {tempDir}" +
                          (FpkgVirtualSource.InPlaceImage.Enabled
                              ? "  (metadata only; the inner image goes straight into the package)"
                              : ""));
```

After the build returns, surface a fallback if one happened:

```csharp
        if (FpkgVirtualSource.InPlaceImage.FallbackReason is { } reason)
            warnings.Add("the single-file build fell back to a spool: " + reason);
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test fpkg-cli.Tests --filter InPlaceFlagTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add fpkg-cli/Program.cs fpkg-cli.Tests/InPlaceFlagTests.cs
git commit -m "feat: single-file build on by default, --spool opts out

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: The parity harness — prove the bytes and the saving

**Files:**
- Create: `fpkg-cli.Tests/InPlaceParityTests.cs`

**Interfaces:**
- Consumes: the whole stack. This is the task that decides whether the change ships.

The fixture must be big enough and awkward enough to exercise a real layout permutation: duplicate blocks (to make dedup leave gaps for `Coalesce` to fill), incompressible data (so sizes are not degenerate), and a nested tree.

- [ ] **Step 1: Write the failing parity test**

```csharp
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Fpkg.Tests;

/// <summary>
/// The single-file build must be a pure disk-space optimisation: the same source must produce
/// the same package, byte for byte, whether the inner image was spooled to --temp-dir or written
/// straight into the package and transformed in place.
///
/// <para>If this test ever fails, the in-place path is wrong — not "expected to differ". The
/// package's own digest chain covers the inner image, so any divergence is a real content or
/// layout bug, and `--spool` is the oracle.</para>
/// </summary>
public class InPlaceParityTests(ITestOutputHelper output)
{
    /// <summary>Compressible, incompressible and duplicated data, so the layout planner has
    /// something to coalesce and relocate rather than a degenerate identity plan.</summary>
    private static string BuildSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "ip-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
        File.WriteAllText(Path.Combine(root, "sce_sys", "param.json"), SyntheticPackage.ParamJson);

        var rng = new Random(1234);
        byte[] dup = new byte[4 << 20];
        rng.NextBytes(dup);
        foreach (var (rel, size, dedup) in new (string, int, bool)[]
                 {
                     ("data/a.bin", 24 << 20, false),
                     ("data/b.bin", 16 << 20, true),
                     ("data/deep/c.bin", 12 << 20, true),
                     ("data/text.txt", 8 << 20, false),
                 })
        {
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var f = File.Create(path);
            for (int w = 0; w < size; w += dup.Length)
            {
                if (!dedup) rng.NextBytes(dup);
                f.Write(dup, 0, Math.Min(dup.Length, size - w));
            }
        }
        return root;
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    [Fact]
    public void InPlaceAndSpoolBuildsAreByteIdentical()
    {
        string source = BuildSource();
        string inPlace = SyntheticPackage.BuildAt(source, "inplace", []);
        string spooled = SyntheticPackage.BuildAt(source, "spool", ["--spool"]);

        output.WriteLine($"in-place: {new FileInfo(inPlace).Length:N0} bytes, {Sha256(inPlace)}");
        output.WriteLine($"spool:    {new FileInfo(spooled).Length:N0} bytes, {Sha256(spooled)}");

        Assert.Equal(new FileInfo(spooled).Length, new FileInfo(inPlace).Length);
        Assert.Equal(Sha256(spooled), Sha256(inPlace));
    }

    [Fact]
    public void InPlaceBuildLeavesNoFullSizeFileInTheTempDir()
    {
        string source = BuildSource();
        string temp = Path.Combine(Path.GetTempPath(), "ip-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        long peak = 0;
        using var watcher = new Timer(_ =>
        {
            long now = Directory.Exists(temp)
                ? Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                           .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } })
                : 0;
            Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), now));
        }, null, 0, 50);

        string pkg = SyntheticPackage.BuildAt(source, "inplace-temp", ["--temp-dir", temp]);
        watcher.Change(Timeout.Infinite, Timeout.Infinite);

        long package = new FileInfo(pkg).Length;
        output.WriteLine($"package {package:N0} bytes, peak temp {peak:N0} bytes");
        // The inner image is the bulk of the package; if it were still spooled, peak temp would be
        // on the order of the package itself. Half is a generous ceiling that still fails loudly.
        Assert.True(peak < package / 2,
            $"peak temp {peak:N0} is not clearly below half the package ({package:N0}); " +
            "the inner image is probably still being spooled");
    }

    [Fact]
    public void InPlaceBuildPassesTheLibrarysOwnFullVerifier()
    {
        string pkg = SyntheticPackage.BuildAt(BuildSource(), "inplace-verify", []);
        var (exit, log) = SyntheticPackage.RunFpkg(["verify", pkg, "--full",
                                                    "--passcode", SyntheticPackage.Passcode]);
        output.WriteLine(log);
        Assert.Equal(0, exit);
    }
}
```

- [ ] **Step 2: Extend `SyntheticPackage` with the helpers the test needs**

`SyntheticPackage` currently builds one fixed package. Add, keeping its existing behaviour untouched:

```csharp
    /// <summary>The param.json every test source needs, exposed so other fixtures can reuse it.</summary>
    internal const string ParamJson = /* move the existing literal here */;

    /// <summary>Builds <paramref name="source"/> into its own output dir and returns the .pkg.</summary>
    internal static string BuildAt(string source, string tag, string[] extraArgs)
    {
        string output = Path.Combine(Path.GetTempPath(), $"ip-out-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        Run(Cli(), ["build", "--source", source, "--out", output, .. extraArgs]);
        string[] built = Directory.GetFiles(output, "*.pkg");
        if (built.Length != 1)
            throw new InvalidOperationException($"expected one .pkg in {output}, found {built.Length}");
        return built[0];
    }

    /// <summary>Runs the CLI and returns its exit code and combined output, for tests that assert on both.</summary>
    internal static (int Exit, string Log) RunFpkg(string[] args) { /* wrap the existing Run() */ }
```

- [ ] **Step 3: Run the parity test**

Run: `dotnet test fpkg-cli.Tests --filter InPlaceParityTests -v n`
Expected on a correct implementation: PASS, 3 tests.

**If `InPlaceAndSpoolBuildsAreByteIdentical` fails, do not weaken it.** Diagnose in this order, because it localises the fault fastest:
1. `fpkg inner-list <pkg> --sha256` on both packages — if the inner file digests match, the divergence is physical layout, which means the relocation pass (Task 2) replayed the permutation differently from `PhysicalLayoutStream`.
2. `cmp -l` the two packages and take the first differing offset. Below 0x10000 it is the FIH/CNT; at 0x10000 + *n*·65536 it is outer data block *n*, so map *n* back to the inner image offset.
3. If the first difference is at the very start of the data region, suspect the `FileMode.Create` → `OpenOrCreate` change (site 23a) leaving stale bytes where the stock path would have had a fresh file.

- [ ] **Step 4: Add the two Review Focus cases that the happy path misses**

```csharp
    [Fact]
    public void ABudgetTooSmallToRelocateFallsBackAndStillMatches()
    {
        string source = BuildSource();
        string oracle = SyntheticPackage.BuildAt(source, "oracle", ["--spool"]);
        // One byte of staging: any real relocation must abandon the in-place pass.
        string fellBack = SyntheticPackage.BuildAt(source, "fallback",
                                                   ["--relocation-budget", "1"]);
        Assert.Equal(Sha256(oracle), Sha256(fellBack));
    }

    [Fact]
    public void ASourceWithAnIdentityLayoutStillBuildsInPlace()
    {
        // A single file with no duplicate blocks gives the planner nothing to move, so
        // MaterializePhysicalLayout is never called and the image is already final in place.
        string root = Path.Combine(Path.GetTempPath(), "ip-flat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
        File.WriteAllText(Path.Combine(root, "sce_sys", "param.json"), SyntheticPackage.ParamJson);
        byte[] data = new byte[8 << 20];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(Path.Combine(root, "only.bin"), data);

        Assert.Equal(Sha256(SyntheticPackage.BuildAt(root, "flat-spool", ["--spool"])),
                     Sha256(SyntheticPackage.BuildAt(root, "flat-inplace", [])));
    }
```

The first needs a hidden `--relocation-budget <bytes>` build flag wired to `InPlaceImage.MaxRelocationBytes` — add it in `fpkg-cli/Program.cs` next to the `--spool` wiring, undocumented in the help text and described in code as a test hook.

- [ ] **Step 5: Run the full suite and commit**

```bash
dotnet test
git add fpkg-cli.Tests fpkg-cli/Program.cs
git commit -m "test: byte-parity, peak-temp and fallback harness for the single-file build

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: Documentation

**Files:**
- Modify: `README.md`, `CHANGELOG.md`, `NOTES.md`

- [ ] **Step 1: README — the build section**

Document that a build now needs roughly the package's own size in free space on the **output** volume rather than twice it across output plus `--temp-dir`; that `--temp-dir` still holds the CNT and the NAPS sidecar but no longer the inner image; and that `--spool` restores the old behaviour. State plainly that the two paths are asserted byte-identical by the test suite.

- [ ] **Step 2: CHANGELOG — a new entry**

Lead with the user-visible number (peak free space needed: ~2× → ~1×), name `--spool`, and note the automatic fallback.

- [ ] **Step 3: NOTES — correct the 0.6.4 temp table**

The "0.6.4 — the ~50% temp saving is real" section's peak-temp table now describes only `--spool` builds. Add a row or a following paragraph with the measured in-place peak from Task 8's harness, and note that the exFAT argument in that section no longer applies to the default path, because the big file is no longer in the temp dir at all.

- [ ] **Step 4: Commit**

```bash
git add README.md CHANGELOG.md NOTES.md
git commit -m "docs: single-file build replaces the inner-image spool

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage.** Sites 16–23 are each owned by a task (16 → Task 6; 17–21 → Task 4; 22–23 → Task 5). The shim's three units are Tasks 1–3. The CLI surface is Task 7, parity and peak-disk evidence Task 8, docs Task 9. The fallback requirement is implemented in Task 2, surfaced in Task 3, reported in Task 7 and tested in Task 8.

**Placeholders.** The IL legs in Tasks 4–6 are specified as transformations plus shape assertions rather than literal dnlib code, because the instruction indices must be read from `--dump` against the release binary in hand — step 1 of each of those tasks is that dump. Every other step carries the actual code.

**Type consistency.** `InPlaceImage.Open` returns `Stream` at all three call sites; the only site whose local is declared `FileStream` (`BuildImage`, `:2672`) is retyped in Task 4. `LayoutRelocator.StagingSource` is installed by `InPlaceImage.Open` on the read path and retrieved by `TryMaterialize` through `StagingSource.Current` — one owner, one lifetime.

**Review Focus.** (1) budget exceeded → Task 2 step 1 and Task 8 step 4; (2) identity layout → Task 2 step 1 and Task 8 step 4; (3) cleanup must not delete the package → Task 3 step 1 and Task 6's `TryDeleteTemp` guard; (4) `--temp-dir` semantics → Task 7 banner and Task 8's peak-temp test; (5) unpatched DLL → Task 7 step 3.
