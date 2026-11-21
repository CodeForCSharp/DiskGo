namespace DiskGo.Core.Ntfs;

/// <summary>
/// One row of an <c>$ATTRIBUTE_LIST</c>. It says "the attribute of this type
/// continues in that other file record", which is how NTFS splits a file whose
/// attributes no longer fit in its own record. The base record is never listed
/// back to itself here; these are only the extension records.
/// </summary>
internal readonly record struct AttributeListEntry(int Type, ulong ExtensionRecord);

/// <summary>Reads the resident value of an <c>$ATTRIBUTE_LIST</c> attribute.</summary>
internal static class AttributeListReader
{
    private const int End = -1;

    public static List<AttributeListEntry> Read(ReadOnlySpan<byte> value)
    {
        var entries = new List<AttributeListEntry>();
        var seen = new HashSet<ulong>();
        int offset = 0;

        while (offset + 26 <= value.Length)
        {
            int type = BitConverter.ToInt32(value.Slice(offset, 4));
            if (type == End || type == 0)
                break;

            int length = BitConverter.ToUInt16(value.Slice(offset + 4, 2));
            if (length < 26 || offset + length > value.Length)
                break;

            ulong record = BitConverter.ToUInt64(value.Slice(offset + 16, 8)) & 0x0000_FFFF_FFFF_FFFFUL;
            if (seen.Add(record))
                entries.Add(new AttributeListEntry(type, record));

            offset += length;
        }

        return entries;
    }
}
