using System.Text.RegularExpressions;

namespace FpkgVirtualSource;

/// <summary>
/// The allow-list and the decision the "language filler dedup bypass" IL leg (site 10) consults,
/// and nothing else.
///
/// <para>
/// Sony's <c>img_create</c> writes 31 byte-identical <c>playgo-languages/NN-xx-XX.bin</c> fillers of
/// 1 MiB of zeros and still gives every one of them its OWN stored copy, so every language chunk
/// owns package bytes of its own and a measured extent can be derived for it. LibProsperoPkg
/// instead content-de-duplicates identical spans onto a single stored copy, which collapses all 31
/// onto one location and leaves the language chunks with nothing to derive an extent from. Site 10
/// rewrites <c>ProsperoPs5InnerImageAssembler.CompressToStream</c> so that, for the files named
/// here and for no others, the compression cache and the physical-block reuse table are bypassed.
/// </para>
///
/// <para><b>The invariant, and why it lives here rather than in the IL.</b>
/// <c>CompressToStream</c> already takes a <c>bool deduplicate</c>, and an optional parameter's
/// default is baked in at the CALL SITE, so the callee cannot tell "the caller defaulted to true"
/// from "the caller passed true deliberately". This class therefore never decides on its own
/// authority: <see cref="ShouldDisableDedup"/> takes the value the caller supplied and can only
/// ever turn <b>true into false</b>. An explicit <c>false</c> is returned unchanged, always, for
/// every path including a filler's — and a filler seen with dedup already off is reported
/// distinctly, because that would mean the library's own routing changed under us and we should
/// see it rather than quietly agree with it.
/// </para>
///
/// <para><b>Membership is exact, not pattern-based.</b> The CLI registers the 31 paths it has just
/// staged, by their full inner-tree path, immediately before the build; nothing else can match. The
/// list is empty in every other process and in every build that does not stage fillers, so the leg
/// is inert there and a normal file always takes the stock de-duplicating path.
/// <see cref="Register"/> also insists on the <c>playgo-languages/NN-&lt;code&gt;.bin</c> shape, so
/// a caller cannot widen the bypass to arbitrary content by mistake.
/// </para>
///
/// <para><b>Threading.</b> <c>CompressToStream</c> encodes blocks on several workers, so the set is
/// published as an immutable snapshot behind a volatile field and the counters are interlocked.
/// Registration happens once, on the build thread, before the builder is started.
/// </para>
/// </summary>
public static class PlayGoFillerDedup
{
    /// <summary>
    /// True when this path is a registered language filler and the physical layout should start it
    /// on a 64 KiB boundary.
    /// <para>
    /// PlayGo extents are derived by coalescing runs of files that share a chunk, so a chunk
    /// boundary that falls mid-block leaves two chunks sharing one block. Sony's own layout never
    /// does this: every filler in the oracle starts block-aligned, 65,536 apart, even though each
    /// stores only 16 bytes. The SDK gets that from WholeBlockRaw, which it derives from the path
    /// (keystone or executable) and which also forces StoreRaw -- for a 1 MiB filler that would
    /// reserve the whole megabyte. Aligning the cursor instead costs one block per filler.
    /// </para>
    /// Same allow-list and the same narrowness as <see cref="ShouldDisableDedup"/>: empty unless
    /// the CLI has just staged zero fillers, so the branch is unreachable in every other build.
    /// </summary>
    public static bool ShouldBlockAlign(string? fullPath) => IsFiller(fullPath);

    /// <summary>
    /// The only shape <see cref="Register"/> accepts: the <c>playgo-languages</c> directory, a
    /// 1-based two-digit chunk number in 01..31, a language code, and <c>.bin</c>. The library
    /// hands the IL leg a rooted inner path (<c>/playgo-languages/01-en-US.bin</c>), so both the
    /// rooted and unrooted spellings are stored and the leading slash is optional here.
    /// </summary>
    private static readonly Regex Shape = new(
        @"^/?playgo-languages/(0[1-9]|[12][0-9]|3[01])-[A-Za-z]{2,3}(-[A-Za-z0-9]{2,4})?\.bin$",
        RegexOptions.CultureInvariant);

    private static volatile HashSet<string> _exempt = new(StringComparer.Ordinal);
    private static volatile HashSet<string> _announced = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static int _overridden;
    private static int _alreadyDisabled;

    /// <summary>
    /// Where the override notices go. The default is the build's own stdout, deliberately: a
    /// change this narrow still changes how bytes are written, so it is stated on the normal
    /// output rather than on a debug channel. Tests point it somewhere they can read.
    /// </summary>
    public static Action<string>? Log { get; set; } = Console.Out.WriteLine;

    /// <summary>
    /// Adds one staged filler to the allow-list. Ordinal comparison throughout: these are package
    /// paths, not user text, and a culture-sensitive or case-insensitive match would be a way for
    /// an unrelated file to fall into the bypass.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path is not a <c>playgo-languages/NN-&lt;code&gt;.bin</c> filler. Loud on purpose: a
    /// silently widened bypass would change compression for files it has no business touching.
    /// </exception>
    public static void Register(string innerPath)
    {
        ArgumentNullException.ThrowIfNull(innerPath);
        if (!Shape.IsMatch(innerPath))
            throw new ArgumentException(
                $"'{innerPath}' is not a PlayGo language filler path. The dedup bypass is only ever " +
                "granted to playgo-languages/NN-<code>.bin, NN in 01..31.", nameof(innerPath));

        string bare = innerPath.StartsWith('/') ? innerPath[1..] : innerPath;
        lock (Gate)
            _exempt = new HashSet<string>(_exempt, StringComparer.Ordinal) { bare, "/" + bare };
    }

    /// <summary>Empties the allow-list and the counters. For tests, and for a second staging.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _exempt = new HashSet<string>(StringComparer.Ordinal);
            _announced = new HashSet<string>(StringComparer.Ordinal);
            _overridden = 0;
            _alreadyDisabled = 0;
        }
    }

    /// <summary>How many distinct fillers are exempt (each is stored rooted and unrooted).</summary>
    public static int Count => _exempt.Count / 2;

    /// <summary>How many distinct fillers have actually had dedup turned off for them.</summary>
    public static int OverriddenCount => Volatile.Read(ref _overridden);

    /// <summary>
    /// How many distinct fillers were met with dedup ALREADY off. Expected to be zero: a non-zero
    /// count means the library routed a filler down a path this leg was not derived against.
    /// </summary>
    public static int AlreadyDisabledCount => Volatile.Read(ref _alreadyDisabled);

    /// <summary>
    /// One line for the end of the build, so a run where the count is not 31 is obvious without
    /// reading the per-file notices.
    /// </summary>
    public static string Summary() =>
        $"playgo: dedup disabled for {OverriddenCount} of {Count} language filler(s)" +
        (AlreadyDisabledCount == 0
            ? ""
            : $"; {AlreadyDisabledCount} filler(s) ALREADY had dedup off — the library's own " +
              "routing has changed and site 10 must be re-derived");

    /// <summary>
    /// Whether this file is one of the registered fillers. Pure membership, no decision and no
    /// logging; <see cref="ShouldDisableDedup"/> is what the IL leg branches on.
    /// </summary>
    public static bool IsFiller(string? fullPath) =>
        fullPath is not null && _exempt.Contains(fullPath);

    /// <summary>
    /// The decision the patched <c>WriteBlock</c> branches on: true means "take the dedup MISS
    /// path for this block" — do not consult the reuse table, and do not register in it.
    ///
    /// <para>
    /// <paramref name="deduplicateRequested"/> is the value the CALLER passed to
    /// <c>CompressToStream</c>. When it is false this returns false, unconditionally and for every
    /// path: the caller has already asked for no de-duplication and it is not this shim's business
    /// to reinterpret that. Only true is ever turned into false, never the reverse.
    /// </para>
    /// </summary>
    public static bool ShouldDisableDedup(string? fullPath, bool deduplicateRequested)
    {
        if (!IsFiller(fullPath)) return false;

        if (!deduplicateRequested)
        {
            // Not an override — the caller already had it off. Reported because it means the
            // filler reached a call site this leg was not derived against.
            if (Announce(fullPath!))
            {
                Interlocked.Increment(ref _alreadyDisabled);
                Log?.Invoke($"playgo: dedup ALREADY disabled for {fullPath} by the caller — " +
                            "site 10 did not override it; the library's own routing has changed");
            }
            return false;
        }

        if (Announce(fullPath!))
        {
            Interlocked.Increment(ref _overridden);
            Log?.Invoke($"playgo: dedup disabled for {fullPath} (was enabled)");
        }
        return true;
    }

    /// <summary>
    /// The patched <c>EncodeBlock</c>'s conjunct: false for a filler, so it skips the shared
    /// compression cache.
    ///
    /// <para>
    /// This is not the <c>deduplicate</c> argument and it is not gated on it, because it can only
    /// ever skip a cache — there is no value of it that turns de-duplication ON. It is required for
    /// correctness rather than for size: a cache HIT returns a compacted entry whose <c>Data</c> is
    /// <c>Array.Empty</c> and whose length comes from <c>CachedStoredSize</c>, which the miss path
    /// then refuses with "Cached block has no canonical physical extent." A zero block cached by
    /// any earlier chunk-0 file would otherwise fail the build, non-deterministically with respect
    /// to the source content.
    /// </para>
    /// </summary>
    public static bool CompressionCacheAllowed(string? fullPath) => !IsFiller(fullPath);

    /// <summary>First sighting of this path, collapsing the four-or-more blocks each file has.</summary>
    private static bool Announce(string fullPath)
    {
        lock (Gate)
        {
            if (!_announced.Add(fullPath)) return false;
            // Both spellings, so the rooted and unrooted forms cannot each announce once.
            _announced.Add(fullPath.StartsWith('/') ? fullPath[1..] : "/" + fullPath);
            return true;
        }
    }
}
