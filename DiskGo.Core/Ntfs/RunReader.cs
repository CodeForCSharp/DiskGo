namespace DiskGo.Core.Ntfs;

/// <summary>
/// The bytes of one attribute, either still inside the file record or out in
/// the runs the record points at.
/// </summary>
internal sealed class ExternalStream
{
    public byte[]? Resident { get; init; }

    public List<DataRun>? Runs { get; init; }

    public long LogicalSize { get; init; }
}

/// <summary>
/// Reads the bytes an attribute's run list points at. The volume handle is
/// unbuffered, so every read is a whole number of clusters; the logical size
/// then says how many of those bytes belong to the attribute.
/// </summary>
internal static class RunReader
{
    /// <summary>The largest attribute worth pulling into memory. Larger than this is treated as unreadable.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    public static byte[]? Read(VolumeReader volume, IReadOnlyList<DataRun> runs, int clusterSize, long logicalSize)
    {
        if (logicalSize <= 0 || logicalSize > MaxBytes || clusterSize <= 0)
            return null;

        var data = new byte[logicalSize];
        long filled = 0;
        foreach (DataRun run in runs)
        {
            if (filled >= logicalSize)
                break;

            long runBytes = run.Clusters * (long)clusterSize;
            if (runBytes <= 0)
                continue;

            long remaining = logicalSize - filled;
            if (run.Sparse || run.Offset < 0)
            {
                filled += Math.Min(runBytes, remaining);
                continue;
            }

            long ask = Math.Min(runBytes, RoundUp(remaining, clusterSize));
            if (ask > int.MaxValue)
                return null;

            var chunk = new byte[ask];
            volume.Read(run.Offset * clusterSize, chunk);
            int copy = (int)Math.Min(ask, remaining);
            chunk.AsSpan(0, copy).CopyTo(data.AsSpan((int)filled));
            filled += copy;
        }

        return data;
    }

    private static long RoundUp(long value, int unit)
    {
        long remainder = value % unit;
        return remainder == 0 ? value : value + unit - remainder;
    }
}
