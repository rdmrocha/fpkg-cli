using LibProsperoPkg.PKG;

namespace FpkgVirtualSource;

/// <summary>
/// make-patch's CNT entries (site 26). A patch's container carries four entries a full
/// application does not: 1035 and 1036 (the relocation-table header, 4 KiB each), and 8448/8449
/// (origin-param.json and target-param.json, the base's param.json). They are added while the
/// builder assembles its entry list — right before it orders and lays the list out — so the body
/// size, entry table, names and digest chain all account for them. 1035/1036 start as zeros:
/// their contents depend on the finished image and are filled in afterwards, at the same size.
///
/// <para>Inert unless the CLI calls <see cref="Configure"/>.</para>
/// </summary>
public static class PatchExtras
{
    public const uint RelocHeaderA = 1035, RelocHeaderB = 1036, OriginParam = 8448, TargetParam = 8449;
    public const int RelocHeaderSize = 4096;

    public static bool Enabled { get; private set; }
    private static byte[] originParam = [];
    private static byte[] targetParam = [];

    public static void Configure(byte[] originParamJson, byte[] targetParamJson)
    {
        originParam = originParamJson;
        targetParam = targetParamJson;
        Enabled = true;
    }

    public static void Reset() => Enabled = false;

    /// <param name="list">The builder's <c>List&lt;Entry&gt;</c>. Typed object so the patched
    /// call site's signature names no LibProsperoPkg type (a self-reference the runtime rejects).</param>
    public static void AddEntries(object list)
    {
        if (!Enabled) return;
        var entries = (List<Entry>)list;
        void Add(uint id, string? name, byte[] data)
        {
            if (entries.Any(e => (uint)e.Id == id))
                throw new InvalidOperationException($"make-patch: CNT entry {id} is already present");
            entries.Add(new GenericEntry((EntryId)id, name) { FileData = data });
        }
        Add(RelocHeaderA, null, new byte[RelocHeaderSize]);
        Add(RelocHeaderB, null, new byte[RelocHeaderSize]);
        Add(OriginParam, "origin-param.json", originParam);
        Add(TargetParam, "target-param.json", targetParam);
    }
}
