using System.Text;
using LibProsperoPkg.PKG;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// One CNT entry: the 32-byte meta record plus its payload, in both plaintext and stored form.
/// </summary>
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

    /// <summary>The entry's name from ENTRY_NAMES, or null when NameTableOffset == 0.</summary>
    internal string? Name;

    /// <summary>The 32-byte meta record, needed as the AES key/IV seed for encrypted entries.</summary>
    internal byte[] MetaBytes()
    {
        var m = new MetaEntry
        {
            id = (EntryId)Id, NameTableOffset = NameTableOffset,
            Flags1 = Flags1, Flags2 = Flags2, DataOffset = DataOffset, DataSize = DataSize,
        };
        return m.GetBytes();
    }
}

/// <summary>
/// Parses the CNT's meta table (at <c>entry_table_offset</c>, <c>entry_count</c> 32-byte
/// big-endian records) and every entry's payload, in both the on-disk id-sorted order and the
/// physical (ascending-DataOffset) order that <c>LayOutEntries</c> actually placed them in.
/// </summary>
internal sealed class CntEntryTable
{
    private const uint EntryNamesId = 512;

    // Homebrew packages carry no real DRM passcode; this is the fixed all-zero passcode used
    // throughout the CNT entry encryption for such packages.
    private static readonly string ZeroPasscode = new('0', 32);

    private readonly Dictionary<uint, CntEntry> _byId;

    /// <summary>Entries in PHYSICAL (pkg.Entries) order — ascending DataOffset.</summary>
    internal IReadOnlyList<CntEntry> Physical { get; }

    /// <summary>The same entries sorted ascending by id — the order of the on-disk meta table.</summary>
    internal IReadOnlyList<CntEntry> ById { get; }

    private CntEntryTable(List<CntEntry> byId)
    {
        ById = byId;
        Physical = byId.OrderBy(e => e.DataOffset).ToList();
        _byId = byId.ToDictionary(e => e.Id);
    }

    internal CntEntry this[uint id] => _byId[id];

    internal static CntEntryTable Parse(byte[] cnt)
    {
        uint entryCount = CntHeader.U32(cnt, CntHeader.EntryCount);
        int tableOffset = (int)CntHeader.U32(cnt, CntHeader.EntryTableOffset);
        string contentId = Encoding.ASCII.GetString(cnt, CntHeader.ContentId, 36).TrimEnd('\0');

        // The table on disk is already sorted ascending by id, so reading it sequentially
        // produces exactly the ById order — no separate sort needed for that one.
        var records = new List<CntEntry>((int)entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int off = tableOffset + i * 32;
            records.Add(new CntEntry
            {
                Id = CntHeader.U32(cnt, off),
                NameTableOffset = CntHeader.U32(cnt, off + 4),
                Flags1 = CntHeader.U32(cnt, off + 8),
                Flags2 = CntHeader.U32(cnt, off + 12),
                DataOffset = CntHeader.U32(cnt, off + 16),
                DataSize = CntHeader.U32(cnt, off + 20),
            });
        }

        // Names come from the ENTRY_NAMES entry: a NUL-separated blob indexed by NameTableOffset.
        var namesEntry = records.Find(e => e.Id == EntryNamesId);
        byte[]? names = namesEntry is null
            ? null
            : cnt.AsSpan((int)namesEntry.DataOffset, (int)namesEntry.DataSize).ToArray();

        foreach (var e in records)
        {
            e.Name = e.NameTableOffset == 0 || names is null
                ? null
                : ReadNulTerminated(names, (int)e.NameTableOffset);
        }

        foreach (var e in records)
        {
            if (e.Encrypted)
            {
                uint storedLength = Align(e.DataSize, 16);
                e.StoredPayload = cnt.AsSpan((int)e.DataOffset, (int)storedLength).ToArray();
                var meta = new MetaEntry
                {
                    id = (EntryId)e.Id, NameTableOffset = e.NameTableOffset,
                    Flags1 = e.Flags1, Flags2 = e.Flags2, DataOffset = e.DataOffset, DataSize = e.DataSize,
                };
                e.Payload = Entry.Decrypt(e.StoredPayload, contentId, ZeroPasscode, meta, publisherProfile: true);
            }
            else
            {
                e.StoredPayload = cnt.AsSpan((int)e.DataOffset, (int)e.DataSize).ToArray();
                // Independent copy: Payload and StoredPayload must never alias, so a later task
                // mutating one in place can never silently corrupt the other.
                e.Payload = (byte[])e.StoredPayload.Clone();
            }
        }

        return new CntEntryTable(records);
    }

    private static string ReadNulTerminated(byte[] blob, int offset)
    {
        int end = offset;
        while (end < blob.Length && blob[end] != 0) end++;
        return Encoding.ASCII.GetString(blob, offset, end - offset);
    }

    private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;
}
