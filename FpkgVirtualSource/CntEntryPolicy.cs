namespace FpkgVirtualSource;

/// <summary>
/// Two CNT-level parity corrections, both measured against the Windows-built the large title package
/// and confirmed on the reference fixture.
///
/// <para><b>1. The five licence entries are stored in plaintext.</b> Sony's package carries
/// <c>license.dat</c> (1024), <c>license.info</c> (1025), <c>nptitle.dat</c> (1026),
/// <c>uds/npbind.dat</c> (8224) and <c>trophy2/npbind.dat</c> (8225) with <c>Flags1 = 0</c>.
/// LibProsperoPkg gives all five <c>Flags1 = 0x80000000</c> (encrypted), with identical sizes and
/// identical name handling — the encryption bit is the only difference.
/// <c>ProsperoCntEntryPolicy.Resolve</c> was probed directly across every
/// <c>ProsperoVolumeType</c> and every spelling of <c>applicationDrmType</c>: all 24 combinations
/// return the encrypted flag, so no build option reaches this and site 13 is required.
/// </para>
///
/// <para><b>2. <c>sce_sys/about/right.sprx</c> is in NEITHER Windows-built package.</b> Sony's
/// folder-to-GP5 wrapper excludes the whole <c>about/</c> directory from the package inputs
/// (<c>GENERATED_SCE_SYS_DIRECTORIES = frozenset({"about"})</c>), and its packer only writes what
/// the GP5 names. the reference fixture's build log names the exclusion outright — <c>sce_sys/about/right.sprx
/// (reserved/SDK-generated sce_sys artifact)</c> — and its package's <c>sce_sys/</c> holds only
/// <c>keystone</c> and <c>pfs-version.dat</c>. LibProsperoPkg instead SYNTHESISES the file from an
/// embedded resource in <c>ProsperoPkgBuilder.EnsureAboutRightSprx</c>, so quarantining the source
/// directory does not stop it. Site 14 turns that method into a no-op, and is ON by default.
/// </para>
///
/// <para>Inner-file counts are not a safe comparison against a reference package: a source tree
/// carrying host junk the SDK's generator has no rule against can offset a file we correctly drop,
/// leaving two different file sets at the same count. Compare membership.</para>
///
/// <para>Both are inert unless the CLI switches them on, so an unpatched or unconfigured build
/// behaves exactly as before.</para>
/// </summary>
public static class CntEntryPolicy
{
    /// <summary>The bit LibProsperoPkg sets in <c>Flags1</c> to mark a CNT entry encrypted.</summary>
    public const uint EncryptedFlag = 0x80000000;

    /// <summary>
    /// The five entry ids Sony stores in plaintext. Ids, not names: the name a given id carries is
    /// the library's business, and matching on the id is what the oracle comparison actually
    /// established.
    /// </summary>
    private static readonly uint[] PlaintextIds = [1024, 1025, 1026, 8224, 8225];

    /// <summary>Where the notices go — the build's normal output, because this changes the package.</summary>
    public static Action<string>? Log { get; set; } = Console.Out.WriteLine;

    /// <summary>Whether site 13 clears the encryption bit on the five licence entries.</summary>
    public static bool PlaintextLicenseEntries { get; set; }

    /// <summary>Whether site 14 suppresses the synthesised <c>sce_sys/about/right.sprx</c>.</summary>
    public static bool SuppressRightSprx { get; set; }

    /// <summary>Resets both switches and the announce set. For tests and repeat builds.</summary>
    public static void Reset()
    {
        PlaintextLicenseEntries = false;
        SuppressRightSprx = false;
        lock (Gate) _announced.Clear();
    }

    private static readonly HashSet<uint> _announced = [];
    private static readonly object Gate = new();

    /// <summary>
    /// Site 13. Runs on the <c>Flags1</c> of every profile
    /// <c>ProsperoCntEntryPolicy.Resolve</c> returns and, for the five licence entries only,
    /// clears the encryption bit.
    ///
    /// <para>It CLEARS a bit and never sets one, and it only ever acts on those five ids, so there
    /// is no input for which it can encrypt something the library meant to leave alone. An entry
    /// the library already resolved as plaintext is returned untouched — reaching that state would
    /// mean the library's own policy had changed, and the right response is to agree with it
    /// rather than to overrule it.</para>
    ///
    /// <para><b>Only primitives cross this boundary, deliberately.</b> Taking or returning
    /// <c>ProsperoCntEntryProfile</c> would make the patched LibProsperoPkg depend on
    /// FpkgVirtualSource depending on LibProsperoPkg, and resolving that cycle at run time fails
    /// with "only single file assemblies are supported" the moment the first licence entry is
    /// laid out — measured, not feared. Site 13's IL reads the other two record fields and
    /// constructs the replacement itself.</para>
    /// </summary>
    public static uint AdjustFlags1(uint flags1, uint id, string? relativeName)
    {
        if (!PlaintextLicenseEntries) return flags1;
        if (Array.IndexOf(PlaintextIds, id) < 0) return flags1;
        if ((flags1 & EncryptedFlag) == 0) return flags1;

        if (Announce(id))
            Log?.Invoke($"  cnt:    entry {id} ({relativeName}) stored in plaintext " +
                        $"(Flags1 0x{flags1:X8} -> 0x{flags1 & ~EncryptedFlag:X8}), " +
                        "matching the Windows oracle");
        return flags1 & ~EncryptedFlag;
    }

    /// <summary>
    /// Site 14. True when <c>EnsureAboutRightSprx</c> should return without doing anything.
    /// </summary>
    public static bool ShouldSkipRightSprx()
    {
        if (!SuppressRightSprx) return false;
        if (Announce(uint.MaxValue))
            Log?.Invoke("  cnt:    sce_sys/about/right.sprx not synthesised; the Windows oracle's " +
                        "package does not contain it");
        return true;
    }

    /// <summary>First sighting of this id, so a per-entry notice is printed once.</summary>
    private static bool Announce(uint id)
    {
        lock (Gate) return _announced.Add(id);
    }
}
