namespace DiskGo.Core.Ntfs;

/// <summary>
/// Finds where the MFT itself is stored. Record 0 is <c>$MFT</c>, and its
/// unnamed <c>$DATA</c> attribute is the run list of every later record.
/// </summary>
internal static class MftLayout
{
    private const int AttributeEnd = -1;
    private const int AttributeData = 0x80;

    public static List<DataRun> ReadRuns(ReadOnlySpan<byte> mftRecord, int clusterSize)
    {
        if (mftRecord.Length < 48 || mftRecord[0] != 'F' || mftRecord[1] != 'I')
            throw new InvalidDataException("The first MFT record is not a valid FILE record.");

        ushort fixupOffset = BitConverter.ToUInt16(mftRecord[4..6]);
        ushort fixupCount = BitConverter.ToUInt16(mftRecord[6..8]);
        byte[] writable = mftRecord.ToArray();
        ApplyFixup(writable, fixupOffset, fixupCount);

        int offset = BitConverter.ToUInt16(writable.AsSpan(20, 2));
        while (offset + 8 < writable.Length)
        {
            int type = BitConverter.ToInt32(writable.AsSpan(offset, 4));
            if (type == AttributeEnd)
                break;

            int length = BitConverter.ToInt32(writable.AsSpan(offset + 4, 4));
            if (length < 8 || offset + length > writable.Length)
                break;

            bool resident = writable[offset + 8] == 0;
            int nameLength = writable[offset + 9];
            if (type == AttributeData && nameLength == 0 && !resident)
            {
                int runOffset = BitConverter.ToUInt16(writable.AsSpan(offset + 32, 2));
                if (runOffset < 0 || runOffset > length)
                    break;

                return RunList.Decode(writable.AsSpan(offset + runOffset, length - runOffset), clusterSize);
            }

            offset += length;
        }

        return [];
    }

    private static void ApplyFixup(Span<byte> record, int offset, int count)
    {
        if (count < 2 || offset < 0 || offset + count * 2 > record.Length)
            return;

        ushort sequenceNumber = BitConverter.ToUInt16(record.Slice(offset, 2));
        for (int sector = 1; sector < count; sector++)
        {
            int position = sector * 512 - 2;
            if (position + 2 > record.Length || BitConverter.ToUInt16(record.Slice(position, 2)) != sequenceNumber)
                return;

            record[position] = record[offset + sector * 2];
            record[position + 1] = record[offset + sector * 2 + 1];
        }
    }
}
