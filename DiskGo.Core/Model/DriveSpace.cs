namespace DiskGo.Core;

/// <summary>
/// Capacity of the volume a scan ran against. Sizes are bytes.
/// <paramref name="Serial"/> is the number Windows stored when the volume was
/// formatted, or null when this result is not a single live volume.
/// </summary>
public readonly record struct DriveSpace(long Total, long Free, long Accounted = -1, uint? Serial = null)
{
    public static DriveSpace Unknown { get; } = new(0, 0);

    public long Used => Total - Free;

    public bool IsKnown => Total > 0;

    public double UsedPercent => Total == 0 ? 0d : 100d * Used / Total;

    /// <summary>
    /// Bytes in use that belong to no file: the file table, directory indexes
    /// and the journal. Zero unless the scan covered a whole volume and said
    /// how many bytes the files account for, because a folder scan would
    /// otherwise report the rest of the disk as reserved.
    /// </summary>
    public long Reserved => IsKnown && Accounted >= 0 ? Math.Max(0, Used - Accounted) : 0;

    /// <summary>Share of the whole volume, from 0 to 100. Zero when the volume size is not known.</summary>
    public double PercentOf(long bytes) => Total <= 0 ? 0d : 100d * bytes / Total;
}
