namespace DiskGo.Core.Ntfs;

/// <summary>
/// Builds a file list by reading the Master File Table directly.
///
/// The MFT is itself a file (<c>$MFT</c>, record 0). Its first records are
/// contiguous, so they can be read from the cluster number stored in the boot
/// sector. Those records contain the <c>$DATA</c> runs that say where the rest
/// of the table lives, which is how the whole table is located without ever
/// opening <c>$MFT</c> through the file system.
/// </summary>
internal static class MftScanner
{
    /// <summary>
    /// Bytes in one read. Two megabytes, kept in flight four at a time on a
    /// volume and eight at a time when the source is a dumped file. The record
    /// size divides this for every size NTFS uses, so a chunk never splits a record.
    /// </summary>
    internal const int ChunkBytes = 2 * 1024 * 1024;

    /// <summary>Reads kept in flight when the source is an MFT file rather than a volume.</summary>
    internal const int FilePipelineDepth = 8;

    public static MftScan ReadVolume(
        char driveLetter, CancellationToken cancellationToken, Action<long>? reportRecords = null, MftReadMode mode = MftReadMode.Auto)
        => ReadVolume(@"\\.\" + char.ToUpperInvariant(driveLetter) + ":", char.ToUpperInvariant(driveLetter) + @":\$MFT", cancellationToken, reportRecords, mode);

    /// <summary>Reads the MFT of a volume that has no drive letter, opened by its <c>\\?\Volume{guid}\</c> name.</summary>
    public static MftScan ReadVolume(
        string volumeName, CancellationToken cancellationToken, Action<long>? reportRecords = null, MftReadMode mode = MftReadMode.Auto)
        => ReadVolume(@"\\.\" + volumeName.TrimEnd('\\')[4..], volumeName.TrimEnd('\\') + @"\$MFT", cancellationToken, reportRecords, mode);

    private static MftScan ReadVolume(
        string device, string mftFile, CancellationToken cancellationToken, Action<long>? reportRecords, MftReadMode mode)
    {
        using var volume = VolumeReader.OpenPath(device);

        byte[] bootSector = new byte[512];
        volume.Read(0, bootSector);
        if (!VolumeBootRecord.TryRead(bootSector, out VolumeBootRecord boot))
            throw new InvalidDataException($"{device} is not an NTFS volume.");

        // Asking the file system where $MFT lives is the cheap way. Decoding
        // record 0 still works when that question is refused, so either answer
        // is enough to find the table.
        List<DataRun> runs = VolumeReader.RetrievalPointers(mftFile, boot.ClusterSize);
        if (runs.Count == 0)
        {
            byte[] firstRecords = new byte[boot.FileRecordSize * 16];
            volume.Read(boot.MftByteOffset, firstRecords);
            runs = MftLayout.ReadRuns(firstRecords.AsSpan(0, boot.FileRecordSize), boot.ClusterSize);
        }

        if (runs.Count == 0)
            throw new InvalidDataException("The MFT record does not contain a readable $DATA attribute.");

        bool usb = mode == MftReadMode.Auto && IsUsb(volume);
        bool overlap = mode switch
        {
            MftReadMode.Overlapped => true,
            MftReadMode.Synchronous => false,
            _ => !usb && ShouldOverlap(volume),
        };

        var records = new List<MftFileRecord>(capacity: 65_536);
        int clusterSize = boot.ClusterSize;
        int chunk = ChunkBytes - ChunkBytes % boot.FileRecordSize;
        var measured = new UsbProbe(usb);

        foreach (DataRun run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run.Sparse || run.Offset < 0)
                continue; // A hole marks a sparse run, and the MFT is never sparse.

            long remaining = run.Clusters * boot.ClusterSize;
            long position = run.Offset * boot.ClusterSize;
            overlap = ReadRun(volume, position, remaining, chunk, boot.FileRecordSize, overlap, measured, records, clusterSize, reportRecords, cancellationToken);
        }

        // Attribute lists, reparse points and the case table can live outside
        // their file record. The runs were noted above; the bytes are read now,
        // while the volume is still open.
        NtfsUpcase? upcase = null;
        foreach (MftFileRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.AttributeListStream is { } list && Read(volume, list, clusterSize) is byte[] listed)
                record.ApplyAttributeList(listed);
            if (record.ReparseStream is { } reparse && Read(volume, reparse, clusterSize) is byte[] point)
                record.ApplyReparse(point);
            if (record.Upcase is { } table)
                upcase = NtfsUpcase.TryFrom(table.Resident ?? Read(volume, table, clusterSize) ?? []);
        }

        return new MftScan(records, upcase);
    }

    /// <summary>
    /// Whether the drive is read with several requests in flight.
    ///
    /// An internal NVMe, SATA or SAS drive is, unless it reports a seek penalty,
    /// which is what a spinning disk does. A USB drive is read one chunk at a
    /// time for the first stretch and timed; it only switches when that stretch
    /// came back quickly and intact. Everything else — virtual disks, MMC,
    /// Storage Spaces, a bus that cannot be named — stays one chunk at a time.
    /// </summary>
    private static bool ShouldOverlap(VolumeReader volume)
    {
        if (volume.HasSeekPenalty())
            return false;

        return volume.BusType() switch
        {
            VolumeReader.StorageBus.Nvme or VolumeReader.StorageBus.Sata or VolumeReader.StorageBus.Sas => true,
            VolumeReader.StorageBus.Usb => false,
            _ => false,
        };
    }

    /// <summary>True when the drive is attached by USB, so the first stretch of the table is timed before overlapping.</summary>
    private static bool IsUsb(VolumeReader volume) => volume.BusType() == VolumeReader.StorageBus.Usb;

    /// <summary>
    /// How many bytes of a USB drive are timed before it is trusted with
    /// overlapped reads, and how fast that stretch has to come back. 300 MB/s
    /// is the line: below it the drive stays one chunk at a time.
    /// </summary>
    internal const long UsbProbeBytes = 64L * 1024 * 1024;
    internal const double UsbProbeMegabytesPerSecond = 300d;

    /// <summary>The first stretch of a USB drive, timed to decide whether the rest may be overlapped.</summary>
    private sealed class UsbProbe(bool active)
    {
        private readonly System.Diagnostics.Stopwatch _watch = new();
        private long _bytes;

        public bool Active => active && _bytes < UsbProbeBytes;

        public void Add(long bytes)
        {
            if (!active || _bytes >= UsbProbeBytes)
                return;

            if (_bytes == 0)
                _watch.Start();
            _bytes += bytes;
        }

        /// <summary>True once the measured stretch was read intact and fast enough.</summary>
        public bool FastEnough(bool intact)
        {
            if (!active || !intact || _bytes < UsbProbeBytes)
                return false;

            _watch.Stop();
            double seconds = Math.Max(_watch.Elapsed.TotalSeconds, 0.001);
            return _bytes / seconds / (1024d * 1024d) >= UsbProbeMegabytesPerSecond;
        }
    }

    /// <summary>Reads one run. Returns whether later runs should still be overlapped.</summary>
    private static bool ReadRun(
        VolumeReader volume, long position, long remaining, int chunk, int recordSize, bool overlap, UsbProbe measured,
        List<MftFileRecord> records, int clusterSize, Action<long>? reportRecords, CancellationToken cancellationToken)
    {
        if (overlap)
        {
            long pipelined = remaining - remaining % chunk;
            if (pipelined > 0)
            {
                long read = TryPipeline(volume, position, pipelined, chunk, recordSize, clusterSize, records, reportRecords, cancellationToken);
                position += read;
                remaining -= read;
                if (read < pipelined)
                    overlap = false;
            }
        }

        bool intact = true;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int whole = (int)Math.Min(remaining, chunk);
            whole -= whole % recordSize;
            if (whole <= 0)
                break;

            var buffer = new byte[whole];
            try
            {
                volume.Read(position, buffer);
            }
            catch (IOException) when (records.Count == 0)
            {
                // The first read is the one most likely to fail while the drive
                // settles, and it is the one that is tried again. One more
                // failure gives up instead of looping on a drive that is down.
                try
                {
                    volume.Read(position, buffer);
                }
                catch (IOException retry)
                {
                    throw new IOException("Could not read the first MFT record.", retry);
                }
            }
            catch (IOException)
            {
                // Anything after that ends this run. What was already read stays,
                // and the next run is read one chunk at a time.
                intact = false;
                break;
            }

            ParseChunk(buffer, records, recordSize, clusterSize);
            reportRecords?.Invoke(records.Count);
            measured.Add(whole);

            position += whole;
            remaining -= whole;

            if (measured.Active && measured.FastEnough(intact))
                overlap = true;
        }

        if (measured.Active)
            overlap = false;

        return overlap;
    }

    /// <summary>
    /// Reads as much of a run as it can with several reads in flight. Returns
    /// how many bytes were actually consumed. A drive that stops answering
    /// part way through ends the overlapped part there; the caller reads the
    /// rest one chunk at a time, so nothing is skipped and nothing is read twice.
    /// </summary>
    private static long TryPipeline(
        VolumeReader volume, long position, long length, int chunk, int recordSize, int clusterSize,
        List<MftFileRecord> records, Action<long>? reportRecords, CancellationToken cancellationToken)
    {
        long consumed = 0;
        try
        {
            volume.ReadPipelined(position, length, chunk, bytes =>
            {
                ParseChunk(bytes, records, recordSize, clusterSize);
                consumed += bytes.Length;
                reportRecords?.Invoke(records.Count);
            }, cancellationToken);
        }
        catch (IOException)
        {
        }

        return consumed;
    }

    private static void ParseChunk(ReadOnlySpan<byte> chunk, List<MftFileRecord> records, int recordSize, int clusterSize)
    {
        for (int offset = 0; offset + recordSize <= chunk.Length; offset += recordSize)
        {
            ulong number = (ulong)records.Count;
            records.Add(MftFileRecord.Parse(chunk.Slice(offset, recordSize), number, clusterSize));
        }
    }

    private static byte[]? Read(VolumeReader volume, ExternalStream stream, int clusterSize)
    {
        if (stream.Resident is not null)
            return stream.Resident;
        if (stream.Runs is null)
            return null;

        try
        {
            return RunReader.Read(volume, stream.Runs, clusterSize, stream.LogicalSize);
        }
        catch (IOException)
        {
            return null;
        }
    }

}

/// <summary>The records of one volume, plus the case table those records name files with.</summary>
internal readonly record struct MftScan(List<MftFileRecord> Records, NtfsUpcase? Upcase);
