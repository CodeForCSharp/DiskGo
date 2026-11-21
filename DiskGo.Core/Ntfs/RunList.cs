namespace DiskGo.Core.Ntfs;

/// <summary>
/// One extent of a non-resident attribute. <see cref="Clusters"/> is how long
/// it is. <see cref="Sparse"/> means NTFS reserved no clusters for it, which is
/// how sparse files and compressed files leave holes.
/// </summary>
internal readonly record struct DataRun(bool Sparse, long Clusters, long Offset = -1);

/// <summary>The on-disk size of one NTFS attribute, read from its run list rather than from the file name.</summary>
internal readonly record struct AttributeSize(long Logical, long Allocated, bool Compressed)
{
    public static AttributeSize None { get; } = new(0, 0, false);
}

/// <summary>
/// Decodes the run list stored at the end of a non-resident attribute header.
/// Each entry gives a length and a cluster offset relative to the previous
/// entry; an offset of zero is a hole and occupies no disk space.
/// </summary>
internal static class RunList
{
    public static List<DataRun> Decode(ReadOnlySpan<byte> data, int clusterSize)
    {
        var runs = new List<DataRun>();
        int index = 0;
        long clusters = 0;
        long cursor = 0;

        while (index < data.Length && data[index] != 0)
        {
            int lengthBytes = data[index] & 0x0F;
            int offsetBytes = data[index] >> 4;
            index++;
            if (lengthBytes is 0 or > 8 || offsetBytes > 8 || index + lengthBytes + offsetBytes > data.Length)
                break;

            long length = ReadUnsigned(data.Slice(index, lengthBytes));
            index += lengthBytes;
            long relative = offsetBytes == 0 ? 0 : ReadSigned(data.Slice(index, offsetBytes));
            index += offsetBytes;
            cursor += relative;

            clusters += length;
            if (clusters < 0 || clusters > (1L << 40) / Math.Max(clusterSize, 1))
                throw new InvalidDataException("An NTFS run list is not credible.");

            runs.Add(new DataRun(Sparse: offsetBytes == 0, Clusters: length, Offset: offsetBytes == 0 ? -1 : cursor));
        }

        return runs;
    }

    /// <summary>
    /// Logical size is the attribute's real size. Allocated size counts only
    /// the runs that reserve clusters, so holes in a sparse or compressed file
    /// contribute nothing. A compressed attribute is then rounded up to a
    /// compression unit, because that is the granularity NTFS allocates at.
    /// </summary>
    public static AttributeSize Measure(IReadOnlyList<DataRun> runs, int clusterSize, int compressionUnit, long logicalSize, bool compressed)
    {
        if (!compressed || compressionUnit <= 1)
        {
            long reserved = 0;
            foreach (DataRun run in runs)
            {
                if (!run.Sparse)
                    reserved += run.Clusters * clusterSize;
            }

            return new AttributeSize(logicalSize, reserved, compressed);
        }

        // NTFS groups the stream into compression units. A unit it could shrink
        // is stored as real clusters followed by a hole; a unit it could not is
        // stored in full and therefore costs the whole unit.
        long allocated = 0;
        long realInUnit = 0;
        long seenInUnit = 0;
        foreach (DataRun run in runs)
        {
            long left = run.Clusters;
            while (left > 0)
            {
                long take = Math.Min(left, compressionUnit - seenInUnit);
                if (!run.Sparse)
                    realInUnit += take;

                seenInUnit += take;
                left -= take;
                if (seenInUnit == compressionUnit)
                {
                    allocated += realInUnit * clusterSize;
                    realInUnit = 0;
                    seenInUnit = 0;
                }
            }
        }

        allocated += realInUnit * clusterSize;
        return new AttributeSize(logicalSize, allocated, compressed);
    }

    /// <summary>
    /// Decodes and measures a run list directly from <paramref name="data"/> without allocating a list.
    /// </summary>
    public static AttributeSize Measure(ReadOnlySpan<byte> data, int clusterSize, int compressionUnit, long logicalSize, bool compressed)
    {
        int index = 0;
        long totalClusters = 0;
        long reserved = 0;
        long allocated = 0;
        long realInUnit = 0;
        long seenInUnit = 0;
        bool useCompressionUnits = compressed && compressionUnit > 1;

        while (index < data.Length && data[index] != 0)
        {
            int lengthBytes = data[index] & 0x0F;
            int offsetBytes = data[index] >> 4;
            index++;
            if (lengthBytes is 0 or > 8 || offsetBytes > 8 || index + lengthBytes + offsetBytes > data.Length)
                break;

            long length = ReadUnsigned(data.Slice(index, lengthBytes));
            index += lengthBytes + offsetBytes;

            totalClusters += length;
            if (totalClusters < 0 || totalClusters > (1L << 40) / Math.Max(clusterSize, 1))
                throw new InvalidDataException("An NTFS run list is not credible.");

            bool sparse = offsetBytes == 0;
            if (!useCompressionUnits)
            {
                if (!sparse)
                    reserved += length * clusterSize;
            }
            else
            {
                long left = length;
                while (left > 0)
                {
                    long take = Math.Min(left, compressionUnit - seenInUnit);
                    if (!sparse)
                        realInUnit += take;

                    seenInUnit += take;
                    left -= take;
                    if (seenInUnit == compressionUnit)
                    {
                        allocated += realInUnit * clusterSize;
                        realInUnit = 0;
                        seenInUnit = 0;
                    }
                }
            }
        }

        if (useCompressionUnits)
        {
            allocated += realInUnit * clusterSize;
            return new AttributeSize(logicalSize, allocated, compressed);
        }

        return new AttributeSize(logicalSize, reserved, compressed);
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);
        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = ReadUnsigned(bytes);
        int signBit = bytes.Length * 8 - 1;
        if ((value & (1L << signBit)) != 0)
            value |= -1L << (signBit + 1);
        return value;
    }
}
