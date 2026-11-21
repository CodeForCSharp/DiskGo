namespace DiskGo.Core.Ntfs;

/// <summary>The fields DiskGo needs from a single <c>$FILE_NAME</c> attribute.</summary>
internal readonly record struct FileNameRecord(
    ulong ParentRecordNumber,
    string Name,
    long Size,
    long AllocatedSize,
    DateTime Created,
    DateTime Modified,
    DateTime Accessed,
    FileAttributes Attributes,
    bool IsDirectory,
    byte Namespace)
{
    /// <summary>Reads one <c>$FILE_NAME</c> attribute value. The buffer holds the value only, not its attribute header.</summary>
    public static bool TryRead(ReadOnlySpan<byte> value, out FileNameRecord record)
    {
        record = default;

        // Parent reference (8) plus the four timestamps, two sizes, two flags and the name length.
        if (value.Length < 66)
            return false;

        int nameLength = value[64];
        if (nameLength == 0 || 66 + nameLength * 2 > value.Length)
            return false;

        ulong parent = BitConverter.ToUInt64(value[..8]) & 0x0000_FFFF_FFFF_FFFFUL;
        string name = System.Text.Encoding.Unicode.GetString(value.Slice(66, nameLength * 2));
        if (!IsUsableName(name))
            return false;

        uint flags = BitConverter.ToUInt32(value[56..60]);
        record = new FileNameRecord(
            parent,
            name,
            BitConverter.ToInt64(value[40..48]),
            BitConverter.ToInt64(value[48..56]),
            NtfsTime.ToLocal(BitConverter.ToInt64(value[8..16])),
            NtfsTime.ToLocal(BitConverter.ToInt64(value[24..32])),
            NtfsTime.ToLocal(BitConverter.ToInt64(value[32..40])),
            (FileAttributes)flags,
            (flags & 0x1000_0000) != 0,
            value[65]);
        return true;
    }

    /// <summary>Rejects names that could not survive a round trip into a normal path.</summary>
    private static bool IsUsableName(string name)
    {
        if (name.Length is 0 or > 255)
            return false;

        foreach (char character in name)
        {
            if (character < ' ' || "<>:\"/\\|?*".Contains(character))
                return false;
        }

        return true;
    }
}
