namespace DiskGo.Core.Ntfs;

/// <summary>
/// A saved copy of one volume's Master File Table.
///
/// The raw records are not enough on their own: the sizes DiskGo reports
/// depend on the cluster size, and the records can only be split apart when
/// their length is known. Both are written into a short header before the
/// records, which are copied in the same order they appear on disk.
/// </summary>
public static class MftDump
{
    /// <summary>The first eight bytes of every dump DiskGo writes.</summary>
    public static ReadOnlySpan<byte> Magic => "DGMFT001"u8;

    private const int HeaderLength = 32;

    /// <summary>
    /// Copies the MFT of <paramref name="driveLetter"/> to <paramref name="filePath"/>.
    /// The volume is opened exactly as a scan opens it, so this also requires
    /// an elevated process. Returns the number of file records written.
    /// </summary>
    public static long Write(char driveLetter, string filePath, CancellationToken cancellationToken = default, IProgress<long>? recordsWritten = null)
    {
        using var volume = VolumeReader.Open(driveLetter);

        byte[] bootSector = new byte[512];
        volume.Read(0, bootSector);
        if (!VolumeBootRecord.TryRead(bootSector, out VolumeBootRecord boot))
            throw new InvalidDataException($"Drive {driveLetter}: is not an NTFS volume.");

        List<DataRun> runs = VolumeReader.RetrievalPointers(char.ToUpperInvariant(driveLetter) + @":\$MFT", boot.ClusterSize);
        if (runs.Count == 0)
        {
            byte[] firstRecords = new byte[boot.FileRecordSize * 16];
            volume.Read(boot.MftByteOffset, firstRecords);
            runs = MftLayout.ReadRuns(firstRecords.AsSpan(0, boot.FileRecordSize), boot.ClusterSize);
        }

        if (runs.Count == 0)
            throw new InvalidDataException("The MFT record does not contain a readable $DATA attribute.");

        using var output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        WriteHeader(output, boot);

        long records = 0;
        int chunk = MftScanner.ChunkBytes - MftScanner.ChunkBytes % boot.FileRecordSize;
        foreach (DataRun run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run.Sparse || run.Offset < 0)
                continue;

            long remaining = run.Clusters * boot.ClusterSize;
            long position = run.Offset * boot.ClusterSize;
            long aligned = remaining - remaining % chunk;
            if (aligned > 0)
            {
                volume.ReadPipelined(position, aligned, chunk, chunkBytes =>
                {
                    output.Write(chunkBytes);
                    records += chunkBytes.Length / boot.FileRecordSize;
                    recordsWritten?.Report(records);
                }, cancellationToken);
                position += aligned;
                remaining -= aligned;
            }

            if (remaining > 0)
            {
                int ask = (int)remaining;
                ask -= ask % boot.FileRecordSize;
                if (ask > 0)
                {
                    var tail = new byte[ask];
                    volume.Read(position, tail);
                    output.Write(tail);
                    records += ask / boot.FileRecordSize;
                    recordsWritten?.Report(records);
                }
            }
        }

        return records;
    }

    /// <summary>True when the file starts with a DiskGo dump header or a raw NTFS file record.</summary>
    public static bool IsDump(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] start = new byte[Magic.Length];
        if (input.Read(start) != start.Length)
            return false;

        return start.AsSpan().SequenceEqual(Magic) || BitConverter.ToInt32(start) == 0x454C4946;
    }

    /// <summary>
    /// Reads a dump back and builds the same tree a live scan of that volume
    /// would have produced, without opening the volume again. Accepts the dump
    /// DiskGo writes and a raw run of file records. A raw dump has no cluster
    /// size of its own, so sizes are only as good as the one supplied.
    /// </summary>
    public static ScanResult Read(
        string filePath, string volumeRoot, CancellationToken cancellationToken = default,
        int rawClusterSize = 4096, string? upcaseFile = null)
    {
        using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        (int clusterSize, int recordSize, bool raw) = ReadHeader(input, rawClusterSize);

        var records = new List<MftFileRecord>();
        byte[] record = new byte[recordSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int done = 0;
            while (done < record.Length)
            {
                int read = input.Read(record, done, record.Length - done);
                if (read == 0)
                {
                    if (done != 0)
                        throw new InvalidDataException("Did not read correct number of bytes from MFT file.");

                    if (raw && records.Count == 0)
                        throw new InvalidDataException("The MFT file contains no file records.");

                    NtfsUpcase? upcase = LoadUpcase(upcaseFile);
                    (FolderNode root, List<FileEntry> files) = TreeBuilder.FromMft(
                        records, volumeRoot, restrictTo: null, PathFilter.Parse("", "", false), cancellationToken, names: upcase);
                    root.SortBySizeDescending();
                    return new ScanResult(
                        root.FullPath,
                        ScanMethod.MasterFileTable,
                        false,
                        root,
                        files,
                        ExtensionSummary.From(files),
                        DriveSpace.Unknown,
                        TimeSpan.Zero,
                        raw ? ["The dump has no cluster size, so sizes were computed with " + clusterSize + " byte clusters."] : []);
                }

                if (read < 0)
                    throw new IOException("The MFT file could not be read.");

                done += read;
            }

            if (BitConverter.ToInt32(record.AsSpan(0, 4)) != 0x454C4946 && records.Count == 0)
                throw new InvalidDataException("MFT file reader got out of step.");

            records.Add(MftFileRecord.Parse(record, (ulong)records.Count, clusterSize));
        }
    }

    private static NtfsUpcase? LoadUpcase(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"The upcase table {filePath} is not there.", filePath);

        byte[] bytes = File.ReadAllBytes(filePath);
        return NtfsUpcase.TryFrom(bytes)
            ?? throw new InvalidDataException($"{filePath} is not an NTFS upcase table.");
    }

    private static void WriteHeader(Stream output, VolumeBootRecord boot)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        BitConverter.TryWriteBytes(header[8..], boot.ClusterSize);
        BitConverter.TryWriteBytes(header[12..], boot.FileRecordSize);
        BitConverter.TryWriteBytes(header[16..], boot.BytesPerSector);
        output.Write(header);
    }

    /// <summary>
    /// A DiskGo dump carries its sizes in the header. A raw dump starts with a
    /// FILE record, and the record length is read from that record's own header.
    /// </summary>
    private static (int ClusterSize, int RecordSize, bool Raw) ReadHeader(Stream input, int rawClusterSize)
    {
        byte[] header = new byte[HeaderLength];
        int read = input.Read(header);
        if (read != header.Length)
            throw new InvalidDataException("The MFT file is too short to be one.");

        if (header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            int clusterSize = BitConverter.ToInt32(header.AsSpan(8, 4));
            int recordSize = BitConverter.ToInt32(header.AsSpan(12, 4));
            if (clusterSize is < 512 or > 1_048_576 || recordSize is < 512 or > 65_536)
                throw new InvalidDataException("The MFT dump header is not credible.");

            return (clusterSize, recordSize, false);
        }

        if (BitConverter.ToInt32(header.AsSpan(0, 4)) != 0x454C4946)
            throw new InvalidDataException("The file is not an MFT dump.");

        // The update-sequence count is one more than the number of sectors, so
        // the record is (count - 1) sectors of 512 bytes.
        int sectors = BitConverter.ToUInt16(header.AsSpan(6, 2)) - 1;
        int rawRecordSize = sectors * 512;
        if (rawRecordSize is < 512 or > 65_536)
            throw new InvalidDataException("The first file record does not say how long it is.");

        input.Position = 0;
        return (rawClusterSize, rawRecordSize, true);
    }
}
