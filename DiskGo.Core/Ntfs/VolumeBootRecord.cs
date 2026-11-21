namespace DiskGo.Core.Ntfs;

/// <summary>The NTFS boot sector values that say where the MFT is and how big its records are.</summary>
internal readonly record struct VolumeBootRecord(
    int BytesPerSector,
    int SectorsPerCluster,
    long MftCluster,
    int FileRecordSize,
    int ClusterSize)
{
    public long MftByteOffset => MftCluster * ClusterSize;

    public static bool TryRead(ReadOnlySpan<byte> sector, out VolumeBootRecord boot)
    {
        boot = default;
        if (sector.Length < 80 || sector[3] != 'N' || sector[4] != 'T' || sector[5] != 'F' || sector[6] != 'S')
            return false;

        int bytesPerSector = BitConverter.ToUInt16(sector[11..13]);
        int sectorsPerCluster = sector[13];
        long mftCluster = BitConverter.ToInt64(sector[48..56]);
        sbyte recordSize = unchecked((sbyte)sector[64]);
        if (bytesPerSector is < 512 or > 65536 || sectorsPerCluster == 0 || mftCluster <= 0)
            return false;

        int clusterSize = bytesPerSector * sectorsPerCluster;
        int fileRecordSize = recordSize > 0
            ? recordSize * clusterSize
            : 1 << -recordSize;
        if (fileRecordSize is < 512 or > 65536)
            return false;

        boot = new VolumeBootRecord(bytesPerSector, sectorsPerCluster, mftCluster, fileRecordSize, clusterSize);
        return true;
    }
}
