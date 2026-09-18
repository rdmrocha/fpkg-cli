using System.Buffers.Binary;
using System.Text;
using LibProsperoPkg.PKG;

namespace Fpkg.Cli.RepairPlayGo;

/// <summary>
/// Rebuilds a CNT container from a parsed entry list and reseals the whole digest chain, in the
/// exact order <c>ProsperoPkgBuilder.LayOutEntries</c> + <c>FinishContainer</c> use. The order is
/// load-bearing: GENERAL_DIGESTS is sealed from the header BEFORE the body is written, and the
/// per-entry body digests are taken AFTER, so reordering the steps changes the output.
/// </summary>
internal static class CntReseal
{
    private const uint DigestsId        = 1;
    private const uint EntryKeysId      = 16;
    private const uint ImageKeyId       = 32;
    private const uint GeneralDigestsId = 128;
    private const uint MetasId          = 256;
    private const uint ImagedigsId      = 0x040A;  // 1034 — the "mandatory" descriptor entry
    private const uint ParamJsonId      = 0x2000;  // 8192

    /// <summary>Content type 34 (PS5 AL / no-data) is the only class that omits the game digest.</summary>
    private const uint ContentTypeAl = 34;

    /// <summary>
    /// ENTRY_KEYS is 0xB80 bytes exactly in the publisher profile; the builder keys the body-size
    /// rounding (64 KiB rather than 512 KiB) off that same test.
    /// </summary>
    private const uint PublisherEntryKeysSize = 0xB80;

    // Ids contributing to the system- and playgo-digests. ComputeConcatOverEntries sorts by id at
    // runtime, so the order written here is irrelevant — only membership matters.
    private static readonly uint[] SystemMediaIds =
        [0x1006, 0x100D, 0x1200, 0x1220, 0x1240, 0x1280, 0x12A0, 0x12C0, 0x2040, 0x2060];

    /// <summary>
    /// Four ids, including 0x3000. The upstream source tree at ~/Developer/LibProsperoPKG lists
    /// only three; it predates the 0.6.9 assembly this CLI loads, which has
    /// <c>new uint[4] { 4097u, 8208u, 8209u, 12288u }</c>. Verified against the stored
    /// PlaygoDigest of both the original and the oracle package: the four-id set reproduces both,
    /// the three-id set reproduces neither.
    /// </summary>
    private static readonly uint[] PlaygoIds = [0x1001, 0x2010, 0x2011, 0x3000];

    /// <summary>
    /// Rebuilds the CNT region from <paramref name="physical"/> in physical order and reseals every
    /// digest in the chain, returning a NEW buffer. <paramref name="cnt"/> supplies the original
    /// header region, which is copied and then patched in the copy; the input array is never
    /// written to.
    /// <para>
    /// Side effect: the <see cref="CntEntry"/> instances in <paramref name="physical"/> ARE mutated
    /// in place — <see cref="CntEntry.DataSize"/> is re-derived from <see cref="CntEntry.Payload"/>,
    /// <see cref="CntEntry.DataOffset"/> is reassigned by the layout walk, and the three derived
    /// payloads (METAS, DIGESTS, GENERAL_DIGESTS) are replaced. Callers can read the new layout back
    /// off the list afterwards.
    /// </para>
    /// </summary>
    internal static byte[] Seal(byte[] cnt, IReadOnlyList<CntEntry> physical,
                               string contentId, string passcode, Progress? progress = null)
    {
        ulong bodyOffset = CntHeader.U64(cnt, CntHeader.BodyOffset);
        ushort scEntryCount = CntHeader.U16(cnt, CntHeader.ScEntryCount);
        uint entryCount = (uint)physical.Count;
        var byId = physical.OrderBy(e => e.Id).ToList();
        var metas = physical.First(e => e.Id == MetasId);
        var digests = physical.First(e => e.Id == DigestsId);
        var generalDigests = physical.First(e => e.Id == GeneralDigestsId);

        // ---- 1. Lay out -------------------------------------------------------------------
        // Payload.Length is the single source of truth for every entry's size; the parsed DataSize
        // field is never trusted, so a caller that assigns only Payload cannot desync the two.
        foreach (var e in physical)
            e.DataSize = (uint)e.Payload.Length;
        metas.DataSize = entryCount * 32;   // METAS' payload IS the table it describes.
        // DIGESTS gets the same treatment, and for the same reason: step 5 below builds
        // `new byte[entryCount * 32]` and writes a row for EVERY entry, so the layout walk has to
        // reserve that much. Sizing it from the stale payload would reserve the OLD entry count's
        // worth on an insert and step 5's writes would then run past DIGESTS into the next entry.
        // A no-op on a package whose id set is unchanged — which is the only case reachable today
        // — but without it the insert-safety the step-5 comment claims is not actually there.
        digests.DataSize = entryCount * 32;

        // Step 5 writes RAW SHA3 hashes straight into the container at digests.DataOffset. That is
        // correct only while DIGESTS is stored in the clear (flags1 = 0x40000000 on this package,
        // encryption bit clear). If it ever carried the encryption bit, those writes would stamp
        // plaintext over ciphertext, digests.Payload would never be re-encrypted, and
        // digest_table_hash would be taken over the plaintext — so the container's own digest chain
        // would verify happily over the corruption. Refuse instead of writing that.
        if (digests.Encrypted)
            throw new InvalidOperationException(
                "this package's DIGESTS entry (1) is encrypted. The reseal writes the digest table " +
                "into the container in the clear and takes digest_table_hash over the plaintext, so " +
                "it cannot handle an encrypted DIGESTS entry: the result would be a corrupted table " +
                "carrying digests that still verified.");

        ulong num = bodyOffset;
        foreach (var e in physical)
        {
            e.DataOffset = checked((uint)num);
            num = Align(num + e.DataSize, 16);
        }

        ulong bodyAlignment =
            physical.First(e => e.Id == EntryKeysId).DataSize == PublisherEntryKeysSize ? 0x10000UL : 0x80000UL;
        // LayOutEntries additionally clamps body_size to at least 0x1E000 for content type 34
        // (PS5 AL / no-data). That class has no embedded CNT to repair, so this path is
        // unreachable here and the clamp is deliberately omitted.
        ulong bodySize = Align(num, bodyAlignment) - bodyOffset;

        // The twelve steps maintain the layout scalars and the digest chain, and nothing else. A
        // body_size change also moves the container end, so cnt_region_size, package_size,
        // mount_image_size, promote_size and pfs_image_offset would all have to be recomputed —
        // and pfs_image_offset in particular cannot simply be re-derived here, because a finalized
        // package stores the FIH-relative 0x10000 rather than the physical offset the builder
        // first writes. Carrying them through stale would be a silent geometry inconsistency, so
        // refuse loudly instead. This is the unimplemented body_size-bump path from the spec.
        ulong originalBodySize = CntHeader.U64(cnt, CntHeader.BodySize);
        if (bodySize != originalBodySize)
        {
            throw new InvalidOperationException(
                $"the resealed body_size changed from 0x{originalBodySize:X} to 0x{bodySize:X}; " +
                "this reseal maintains only the fields in its twelve steps, and a body_size change " +
                "also requires cnt_region_size, package_size, mount_image_size, promote_size and " +
                "pfs_image_offset to be recomputed — the body_size-bump path is not implemented.");
        }

        var outCnt = new byte[checked((int)(bodyOffset + bodySize))];
        // Header region verbatim; everything from body_offset on is rewritten below. The body is
        // left zero-filled first: the builder writes into a freshly SetLength'd file, so inter-entry
        // alignment padding and the 64-KiB tail are zero there too (confirmed by fixed point 1).
        Array.Copy(cnt, outCnt, Math.Min(cnt.Length, (int)bodyOffset));

        CntHeader.SetU32(outCnt, CntHeader.EntryCount, entryCount);
        BinaryPrimitives.WriteUInt16BigEndian(outCnt.AsSpan(CntHeader.EntryCount2), (ushort)entryCount);
        CntHeader.SetU32(outCnt, CntHeader.EntryTableOffset, metas.DataOffset);
        CntHeader.SetU64(outCnt, CntHeader.BodySize, bodySize);
        CntHeader.SetU32(outCnt, CntHeader.MainEntDataSize,
            checked((uint)physical.Take(scEntryCount - 1).Sum(e => (long)e.DataSize)));

        var mandatory = physical.First(e => e.Id == ImagedigsId);
        var imageKey = physical.FirstOrDefault(e => e.Id == ImageKeyId);
        CntHeader.SetU64(outCnt, CntHeader.MandatorySize, mandatory.DataOffset);
        CntHeader.SetU32(outCnt, CntHeader.DescMandatoryOffset, mandatory.DataOffset);
        CntHeader.SetU32(outCnt, CntHeader.DescMandatorySize, mandatory.DataSize);
        if (imageKey is not null)
        {
            CntHeader.SetU32(outCnt, CntHeader.DescImageKeyOffset, imageKey.DataOffset);
            CntHeader.SetU32(outCnt, CntHeader.DescImageKeySize, imageKey.DataSize);
        }

        // ---- 2. Meta table ----------------------------------------------------------------
        // Sorted ascending by id, 24 meaningful bytes per record. MetaEntry.Write leaves each
        // record's trailing 8 pad bytes untouched (`s.Position += 8`) rather than zeroing them; in
        // a builder-produced package it writes into a zero-filled buffer, so those bytes are zero
        // — verified for all 27 records of the test package by CntResealTests. The table is
        // therefore built fresh (zero-filled) rather than carried over from the old one, which
        // would have matched records positionally and gone silently wrong if the id set changed.
        var table = new byte[entryCount * 32];
        for (int i = 0; i < byId.Count; i++)
            byId[i].MetaBytes().AsSpan(0, 24).CopyTo(table.AsSpan(i * 32, 24));
        metas.Payload = table;

        // ---- 3. GENERAL_DIGESTS -----------------------------------------------------------
        // Sealed from the header BEFORE the body is written — the header-digest preimage is the
        // layout scalars just written above plus the (unchanged) mount descriptor.
        generalDigests.Payload = RebuildGeneralDigests(outCnt, generalDigests.Payload, physical, contentId);

        // ---- 4. Body ----------------------------------------------------------------------
        foreach (var e in physical)
            WriteEntry(outCnt, e, contentId, passcode);

        // ---- 5. DIGESTS -------------------------------------------------------------------
        // Sized from the live entry count, not the parsed payload, which would be short after an
        // insert. Slot 0 is DIGESTS itself and is never computed; it keeps whatever the payload
        // held (zero in a builder-produced package).
        var digestTable = new byte[entryCount * 32];
        digests.Payload.AsSpan(0, Math.Min(digests.Payload.Length, digestTable.Length)).CopyTo(digestTable);
        // The per-entry SHA3 pass walks the whole body, so the bytes it has digested so far are the
        // percentage source for this stage. Observation only: nothing below reads `digested` back.
        long digested = 0;
        for (int i = 1; i < byId.Count; i++)
        {
            var e = byId[i];
            int length = (int)(e.Encrypted ? Align(e.DataSize, 16) : e.DataSize);
            byte[] hash = ProsperoImageDigests.Sha3_256(outCnt.AsSpan((int)e.DataOffset, length));
            hash.CopyTo(digestTable.AsSpan(32 * i));
            hash.CopyTo(outCnt.AsSpan((int)digests.DataOffset + 32 * i));
            digested += length;
            progress?.Report(digested, (long)bodySize);
            progress?.Detail($"digest {e.Id,-6} {e.Name,-24} {length,9:N0} bytes -> " +
                             $"{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}…");
        }
        digests.Payload = digestTable;

        // ---- 6/7. body-digest and digest-table hash ---------------------------------------
        CntHeader.SetBytes(outCnt, CntHeader.BodyDigest,
            ProsperoImageDigests.Sha3_256(outCnt.AsSpan((int)bodyOffset, (int)bodySize)));
        CntHeader.SetBytes(outCnt, CntHeader.DigestTableHash, ProsperoImageDigests.Sha3_256(digestTable));

        // ---- 8/9. the two sc-entry rollups ------------------------------------------------
        // Semantic order, NOT the entry-table order (which starts with DIGESTS).
        var scEntries = new List<CntEntry> { physical.First(e => e.Id == EntryKeysId) };
        if (imageKey is not null) scEntries.Add(imageKey);
        scEntries.Add(generalDigests);
        scEntries.Add(metas);
        scEntries.Add(digests);

        using var sc1 = new MemoryStream();
        foreach (var e in scEntries)
            sc1.Write(outCnt, (int)e.DataOffset, (int)e.DataSize);
        CntHeader.SetBytes(outCnt, CntHeader.ScEntries1Hash, ProsperoImageDigests.Sha3_256(sc1.ToArray()));

        using var sc2 = new MemoryStream();
        foreach (var e in scEntries.Take(scEntries.Count - 1))
        {
            int size = e.Id == MetasId ? scEntryCount * 0x20 : (int)e.DataSize;
            sc2.Write(outCnt, (int)e.DataOffset, size);
        }
        CntHeader.SetBytes(outCnt, CntHeader.ScEntries2Hash, ProsperoImageDigests.Sha3_256(sc2.ToArray()));

        // ---- 10. desc_digest --------------------------------------------------------------
        uint descImageKeySize = CntHeader.U32(outCnt, CntHeader.DescImageKeySize);
        uint descMandatorySize = CntHeader.U32(outCnt, CntHeader.DescMandatorySize);
        if (descImageKeySize != 0 && descMandatorySize != 0)
        {
            var descDigest = new byte[64];
            ProsperoImageDigests.Sha3_256(
                outCnt.AsSpan((int)CntHeader.U32(outCnt, CntHeader.DescImageKeyOffset), (int)descImageKeySize))
                .CopyTo(descDigest, 0);
            ProsperoImageDigests.Sha3_256(
                outCnt.AsSpan((int)CntHeader.U32(outCnt, CntHeader.DescMandatoryOffset), (int)descMandatorySize))
                .CopyTo(descDigest, 32);
            CntHeader.SetBytes(outCnt, CntHeader.DescDigest, descDigest);
        }

        // ---- 11. package digest -----------------------------------------------------------
        // The [0x410..0x418) force mirrors FinishContainer, which seals over the FIH-relative
        // 0x10000 rather than the physical image offset. On an already-finalized package the field
        // already reads 0x10000, so this is a NO-OP here and the fixed points do NOT validate it.
        var packageDigestPreimage = outCnt.AsSpan(0, ProsperoImageDigests.PackageDigestRegionSize).ToArray();
        BinaryPrimitives.WriteUInt64BigEndian(
            packageDigestPreimage.AsSpan(CntHeader.PfsImageOffset), ProsperoImageDigests.FihRelativeImageOffset);
        CntHeader.SetBytes(outCnt, CntHeader.PackageDigest,
            ProsperoImageDigests.ComputePackageDigest(packageDigestPreimage));

        // ---- 12. RSA header wrap ----------------------------------------------------------
        var wrapPreimage = outCnt.AsSpan(0, 0x1000).ToArray();
        BinaryPrimitives.WriteUInt64BigEndian(
            wrapPreimage.AsSpan(CntHeader.PfsImageOffset), ProsperoImageDigests.FihRelativeImageOffset);
        CntHeader.SetBytes(outCnt, CntHeader.HeaderWrap, ProsperoPublisherRsa.BuildCntHeaderWrap(wrapPreimage));

        return outCnt;
    }

    /// <summary>
    /// Recomputes the GENERAL_DIGESTS record. Read/Set/Write keep the library in charge of that
    /// record's on-disk shape; Write deliberately skips bytes 4..28, so the new payload is built on
    /// top of the old one to carry them (and any digest slot this scheme does not set) through.
    /// </summary>
    private static byte[] RebuildGeneralDigests(
        byte[] cnt, byte[] original, IReadOnlyList<CntEntry> physical, string contentId)
    {
        var payload = (byte[])original.Clone();
        GeneralDigestsEntry gd;
        using (var read = new MemoryStream(payload, writable: false))
            gd = GeneralDigestsEntry.Read(read);

        gd.Set(GeneralDigest.HeaderDigest, ProsperoImageDigests.ComputeHeaderDigest(
            cnt.AsSpan(0, ProsperoImageDigests.HeaderDigestPrefixSize),
            ProsperoImageDigests.ForceFihRelativeImageOffset(
                cnt.AsSpan(CntHeader.MountDescriptor, ProsperoImageDigests.HeaderDigestMountDescriptorSize))));

        uint contentType = CntHeader.U32(cnt, CntHeader.ContentType);
        bool includeGame = contentType != ContentTypeAl;
        byte[] game = cnt.AsSpan(CntHeader.PfsImageDigest, 32).ToArray();

        var descriptor = new byte[ProsperoImageDigests.ContentDescriptorSize];
        byte[] cid = Encoding.ASCII.GetBytes(contentId);
        Array.Copy(cid, 0, descriptor, 0, Math.Min(cid.Length, 36));
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x30), CntHeader.U32(cnt, CntHeader.DrmType));
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x34), contentType);
        gd.Set(GeneralDigest.ContentDigest, ProsperoImageDigests.ComputeContentDigest(
            descriptor, includeGame ? game : default, new byte[ProsperoImageDigests.DigestSize], includeGame));

        if (includeGame)
        {
            gd.Set(GeneralDigest.GameDigest, game);
            gd.Set(GeneralDigest.TargetDigest, game);
        }

        // Every entry here carries a materialised payload, which matters: the library filters on
        // `GenericEntry { FileData: not null }`, so an entry present in the table but without bytes
        // would contribute nothing and silently change these two digests.
        byte[]? system = ConcatOverEntries(physical, SystemMediaIds);
        if (system is not null) gd.Set(GeneralDigest.SystemDigest, system);
        byte[]? playgo = ConcatOverEntries(physical, PlaygoIds);
        if (playgo is not null) gd.Set(GeneralDigest.PlaygoDigest, playgo);

        var param = physical.FirstOrDefault(e => e.Id == ParamJsonId);
        if (param is not null)
            gd.Set(GeneralDigest.ParamDigest, ProsperoImageDigests.ComputeEntryDigest(param.Payload));

        using var write = new MemoryStream(payload, writable: true);
        gd.Write(write);
        return payload;
    }

    private static byte[]? ConcatOverEntries(IReadOnlyList<CntEntry> physical, uint[] ids)
    {
        var set = new HashSet<uint>(ids);
        var perEntry = physical
            .Where(e => set.Contains(e.Id))
            .OrderBy(e => e.Id)
            .Select(e => ProsperoImageDigests.ComputeEntryDigest(e.Payload))
            .ToList();
        return perEntry.Count == 0 ? null : ProsperoImageDigests.ComputeConcatDigest(perEntry);
    }

    /// <summary>
    /// Places one entry's payload at its new <see cref="CntEntry.DataOffset"/>. Encrypted entries
    /// are re-encrypted to their FULL <c>Align(DataSize,16)</c> stored length — the AES key and IV
    /// derive from the 32-byte meta record, so any entry that moved or resized must be re-encrypted
    /// rather than copied, and a short write would leave a truncated CBC tail behind.
    /// </summary>
    private static void WriteEntry(byte[] cnt, CntEntry e, string contentId, string passcode)
    {
        if (!e.Encrypted)
        {
            e.Payload.CopyTo(cnt.AsSpan((int)e.DataOffset));
            return;
        }

        var generic = new GenericEntry((EntryId)e.Id, e.Name) { FileData = e.Payload };
        generic.meta = new MetaEntry
        {
            id = (EntryId)e.Id, NameTableOffset = e.NameTableOffset,
            Flags1 = e.Flags1, Flags2 = e.Flags2, DataOffset = e.DataOffset, DataSize = e.DataSize,
        };
        using var ms = new MemoryStream();
        // publisherProfile: true — verified for this profile (EntryKeys.Keys[0].key.Length == 384).
        generic.WriteEncrypted(ms, contentId, passcode, publisherProfile: true);
        ms.ToArray().CopyTo(cnt.AsSpan((int)e.DataOffset));
    }

    private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;
}
