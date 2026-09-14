using System.Security.Cryptography;
using System.Text.Json;

namespace Fpkg.Cli;

/// <summary>
/// Which file was chosen to load as "LibProsperoPkg", and which patches its stamp records as
/// having gone into it. <see cref="Path"/> is null when nothing patched was usable and the
/// stock LibProsperoPkg.dll should be loaded — in which case <see cref="Patches"/> is empty.
/// </summary>
internal readonly record struct PatchSelection(string? Path, IReadOnlyList<string> Patches)
{
    internal static PatchSelection Stock { get; } = new(null, []);

    /// <summary>The native RAD Oodle encoder leg (tools/OodlePatcher).</summary>
    internal bool OodleApplied => Has("oodle");

    /// <summary>
    /// The virtual-source leg (tools/VirtualSourcePatcher) that makes <c>ffpfsc:</c> paths
    /// resolve and, critically, overlays the container tree in <c>Populate</c>. Without it a
    /// container build produces an EMPTY package that still passes verification, so this is a
    /// correctness predicate, not a capability hint.
    /// </summary>
    internal bool FfpfscApplied => Has("ffpfsc");

    private bool Has(string patch) =>
        Patches.Any(p => string.Equals(p, patch, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The single reader for the patch stamps written beside a release folder's LibProsperoPkg.
///
/// There is deliberately only one of these. An earlier version parsed
/// LibProsperoPkg.patched.stamp twice — once here to decide what to load, once in
/// <c>fpkg version</c> to report it — and the second copy was unguarded, so a malformed
/// <c>patches</c> array made `fpkg version` throw while the resolver skipped it cleanly.
/// Callers that want to report what is loaded read <see cref="LibraryResolver.Selection"/>
/// instead of re-parsing.
///
/// Every rejection path prints one warning through the supplied <paramref name="warn"/> sink
/// and falls through to the next candidate rather than failing: a release folder with no patch
/// at all stays completely silent.
/// </summary>
internal static class PatchStamp
{
    internal const string UnifiedDllName = "LibProsperoPkg.patched.dll";
    internal const string UnifiedStampName = "LibProsperoPkg.patched.stamp";
    internal const string LegacyOodleDllName = "LibProsperoPkg.oodle.dll";
    internal const string LegacyOodleStampName = "LibProsperoPkg.oodle.stamp";
    internal const string StockDllName = "LibProsperoPkg.dll";

    /// <summary>
    /// Picks which assembly to load as "LibProsperoPkg", in preference order: the unified
    /// LibProsperoPkg.patched.dll (./fpkg patch), then the legacy LibProsperoPkg.oodle.dll
    /// (patch-oodle.sh's old direct output), then the stock LibProsperoPkg.dll.
    /// </summary>
    internal static PatchSelection Select(string releaseFolder, Action<string> warn)
    {
        string stock = Path.Combine(releaseFolder, StockDllName);

        string unifiedDll = Path.Combine(releaseFolder, UnifiedDllName);
        if (File.Exists(unifiedDll))
        {
            var patches = ReadUnifiedStamp(releaseFolder, stock, warn);
            if (patches is not null) return new PatchSelection(unifiedDll, patches);
            // Stale/unreadable/missing stamp: warning already printed, fall through.
        }

        string oodleDll = Path.Combine(releaseFolder, LegacyOodleDllName);
        if (File.Exists(oodleDll) && ValidateLegacyOodleStamp(releaseFolder, stock, warn))
            return new PatchSelection(oodleDll, ["oodle"]);

        return PatchSelection.Stock;
    }

    /// <summary>
    /// Validates LibProsperoPkg.patched.stamp (<c>./fpkg patch</c>'s <c>{"source":..,"patches":[..]}</c>)
    /// against the stock DLL's current hash. Returns the applied-patches list when it matches,
    /// null when the patched DLL should be ignored (printing one warning first).
    /// </summary>
    internal static IReadOnlyList<string>? ReadUnifiedStamp(
        string releaseFolder, string stock, Action<string> warn)
    {
        string stampPath = Path.Combine(releaseFolder, UnifiedStampName);

        if (!File.Exists(stampPath))
        {
            warn($"warning: {UnifiedDllName} is present but {UnifiedStampName} " +
                 "is missing; ignoring it. Re-run ./fpkg patch.");
            return null;
        }
        if (!File.Exists(stock))
        {
            warn($"warning: {UnifiedDllName} is present but the stock {StockDllName} " +
                 "it was patched from is missing from this release folder; ignoring it.");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stampPath));
            string? recorded = doc.RootElement.TryGetProperty("source", out var v) ? v.GetString() : null;
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stock)));

            if (!string.Equals(recorded, actual, StringComparison.OrdinalIgnoreCase))
            {
                warn($"warning: {UnifiedDllName} was built against source SHA-256 " +
                     $"{Short(recorded)} but {StockDllName} is now {Short(actual)}; ignoring " +
                     "the stale patched copy. Re-run ./fpkg patch.");
                return null;
            }

            if (!doc.RootElement.TryGetProperty("patches", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                warn($"warning: {UnifiedStampName} has no 'patches' array; ignoring " +
                     $"{UnifiedDllName}. Re-run ./fpkg patch.");
                return null;
            }

            // A non-string element (or any other malformed entry) throws out of GetString and is
            // caught below, exactly as an unparseable document is: `fpkg version` used to read
            // this same array without that net and threw instead of reporting "none".
            return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch (Exception ex)
        {
            warn($"warning: {UnifiedStampName} is unreadable ({ex.Message}); ignoring " +
                 $"{UnifiedDllName}.");
            return null;
        }
    }

    /// <summary>
    /// Validates the legacy LibProsperoPkg.oodle.stamp (patch-oodle.sh's old direct output,
    /// <c>{"sourceSha256":..}</c>) against the stock DLL's current hash. True when it matches;
    /// false (after printing one warning) when it should be ignored.
    /// </summary>
    internal static bool ValidateLegacyOodleStamp(string releaseFolder, string stock, Action<string> warn)
    {
        string stampPath = Path.Combine(releaseFolder, LegacyOodleStampName);

        if (!File.Exists(stampPath))
        {
            warn($"warning: {LegacyOodleDllName} is present but {LegacyOodleStampName} " +
                 "is missing; using the stock library (native Oodle disabled). " +
                 "Re-run ./fpkg patch.");
            return false;
        }
        if (!File.Exists(stock))
        {
            warn($"warning: {LegacyOodleDllName} is present but the stock {StockDllName} " +
                 "it was patched from is missing from this release folder; using the stock " +
                 "resolution path (native Oodle disabled).");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stampPath));
            string? recorded = doc.RootElement.TryGetProperty("sourceSha256", out var v) ? v.GetString() : null;
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stock)));

            if (string.Equals(recorded, actual, StringComparison.OrdinalIgnoreCase)) return true;

            warn($"warning: {LegacyOodleDllName} was built against SHA-256 {Short(recorded)} " +
                 $"but {StockDllName} is now {Short(actual)}; using the stock library " +
                 "(native Oodle disabled). Re-run ./fpkg patch.");
            return false;
        }
        catch (Exception ex)
        {
            warn($"warning: {LegacyOodleStampName} is unreadable ({ex.Message}); " +
                 "using the stock library (native Oodle disabled).");
            return false;
        }
    }

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha[..Math.Min(16, sha.Length)] + "…";
}
