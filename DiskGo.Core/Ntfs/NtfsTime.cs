namespace DiskGo.Core.Ntfs;

/// <summary>NTFS stores timestamps as the number of 100-nanosecond intervals since 1 January 1601, in UTC.</summary>
internal static class NtfsTime
{
    public static DateTime ToLocal(long fileTime)
    {
        if (fileTime <= 0)
            return DateTime.MinValue;

        try
        {
            return DateTime.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }
}
