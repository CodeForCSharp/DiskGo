using DiskGo.Core;
using DiskGo.Core.Ntfs;
using Xunit;

namespace DiskGo.Tests;

/// <summary>
/// Builds the smallest legal MFT records that exercise the two things the
/// scanner used to get wrong: the size comes from <c>$DATA</c>, and attributes
/// that spilled into another record are still part of the same file.
/// </summary>
public class MftRecordTests
{
    private const int ClusterSize = 4096;

    [Fact]
    public void ResidentData_HasNoAllocatedSize()
    {
        byte[] record = new byte[1024];
        WriteHeader(record, flags: 0x0001);
        int next = WriteFileName(record, 56, parent: 5, "note.txt", declaredSize: 999);
        WriteResidentData(record, next, "hello"u8.ToArray());

        MftFileRecord parsed = MftFileRecord.Parse(record, 40, ClusterSize);

        Assert.True(parsed.InUse);
        Assert.Equal("note.txt", parsed.Name.Name);
        Assert.Equal(5, parsed.Data.Logical);
        Assert.Equal(0, parsed.Data.Allocated);
    }

    [Fact]
    public void SparseRun_DoesNotCountTheHole()
    {
        byte[] record = new byte[1024];
        WriteHeader(record, flags: 0x0001);
        int next = WriteFileName(record, 56, parent: 5, "big.bin", declaredSize: 0);
        WriteNonResidentData(record, next, logical: 4096L * 32, allocatedClusters: 8, sparseClusters: 24, compressed: false);

        MftFileRecord parsed = MftFileRecord.Parse(record, 41, ClusterSize);

        Assert.Equal(4096L * 32, parsed.Data.Logical);
        Assert.Equal(4096L * 8, parsed.Data.Allocated);
    }

    [Fact]
    public void CompressedUnit_CountsOnlyTheClustersThatWereStored()
    {
        byte[] record = new byte[1024];
        WriteHeader(record, flags: 0x0001);
        int next = WriteFileName(record, 56, parent: 5, "packed.bin", declaredSize: 0);
        WriteNonResidentData(record, next, logical: 4096L * 16, allocatedClusters: 4, sparseClusters: 12, compressed: true);

        MftFileRecord parsed = MftFileRecord.Parse(record, 42, ClusterSize);

        Assert.Equal(4096L * 16, parsed.Data.Logical);
        Assert.Equal(4096L * 4, parsed.Data.Allocated);
    }

    [Fact]
    public void AttributeList_AddsTheExtensionRecordToTheSameFile()
    {
        byte[] extension = new byte[1024];
        WriteHeader(extension, flags: 0x0001, baseRecord: 50);
        WriteNonResidentData(extension, 56, logical: 4096L * 10, allocatedClusters: 10, sparseClusters: 0, compressed: false);

        byte[] baseRecord = new byte[1024];
        WriteHeader(baseRecord, flags: 0x0001);
        int next = WriteFileName(baseRecord, 56, parent: 5, "split.bin", declaredSize: 100);
        next = WriteAttributeList(baseRecord, next, extensionRecord: 80);
        WriteNonResidentData(baseRecord, next, logical: 4096L * 6, allocatedClusters: 6, sparseClusters: 0, compressed: false);

        var records = new MftFileRecord[81];
        for (int i = 0; i < records.Length; i++)
            records[i] = MftFileRecord.Parse(new byte[1024], (ulong)i, ClusterSize);
        records[50] = MftFileRecord.Parse(baseRecord, 50, ClusterSize);
        records[80] = MftFileRecord.Parse(extension, 80, ClusterSize);

        Assert.True(records[80].IsExtension);
        Assert.Contains(80UL, records[50].ExtensionRecords);

        RecordSizes size = RecordResolver.SizeOf(records[50], records);
        Assert.Equal(4096L * 16, size.Logical);
        Assert.Equal(4096L * 16, size.Allocated);
    }

    [Fact]
    public void MetadataRecords_AreCountedOnlyWhenTheScanAsksForThem()
    {
        var records = new MftFileRecord[16];
        for (int i = 0; i < records.Length; i++)
            records[i] = MftFileRecord.Parse(new byte[1024], (ulong)i, ClusterSize);

        byte[] bitmap = new byte[1024];
        WriteHeader(bitmap, flags: 0x0001);
        int next = WriteFileName(bitmap, 56, parent: 5, "$Bitmap", declaredSize: 0);
        WriteNonResidentData(bitmap, next, logical: 4096L * 4, allocatedClusters: 4, sparseClusters: 0, compressed: false);
        records[6] = MftFileRecord.Parse(bitmap, 6, ClusterSize);

        (FolderNode plain, List<FileEntry> plainFiles) = TreeBuilder.FromMft(records, @"C:\", null, PathFilter.Parse("", "", false), CancellationToken.None);
        Assert.Empty(plainFiles);
        Assert.Equal(0, plain.Size);

        (FolderNode withMetadata, List<FileEntry> metadataFiles) = TreeBuilder.FromMft(records, @"C:\", null, PathFilter.Parse("", "", false), includeMetadata: true, CancellationToken.None);

        FileEntry file = Assert.Single(metadataFiles);
        Assert.Equal("$Bitmap", file.Name);
        Assert.Equal(4096L * 4, withMetadata.Size);
    }

    [Fact]
    public void HardLink_IsListedOnceInEachDirectory()
    {
        byte[] record = new byte[1024];
        WriteHeader(record, flags: 0x0001);
        int next = WriteFileName(record, 56, parent: 5, "original.txt", declaredSize: 100);
        next = WriteFileName(record, next, parent: 30, "alias.txt", declaredSize: 100);
        next = WriteFileName(record, next, parent: 5, "ORIGIN~1.TXT", declaredSize: 100, nameSpace: 2);
        WriteResidentData(record, next, "hello"u8.ToArray());

        var records = new MftFileRecord[41];
        for (int i = 0; i < records.Length; i++)
            records[i] = MftFileRecord.Parse(new byte[1024], (ulong)i, ClusterSize);
        records[5] = Directory(5, parent: 5, name: ".");
        records[30] = Directory(30, parent: 5, name: "links");
        records[40] = MftFileRecord.Parse(record, 40, ClusterSize);

        (FolderNode root, List<FileEntry> files) = TreeBuilder.FromMft(records, @"C:\", null, PathFilter.Parse("", "", false), CancellationToken.None);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.FullPath == @"C:\original.txt" && file.LinkCount == 2 && file.ShortName == "ORIGIN~1.TXT");
        Assert.Contains(files, file => file.FullPath == @"C:\links\alias.txt" && file.LinkCount == 2);
        Assert.Equal(5, root.Size);
        Assert.Equal(1, files.Count(static file => file.CountsTowardTotal));
    }

    [Fact]
    public void UnresolvableParent_IsSkippedInsteadOfFailingTheScan()
    {
        var records = new MftFileRecord[45];
        for (int i = 0; i < records.Length; i++)
            records[i] = MftFileRecord.Parse(new byte[1024], (ulong)i, ClusterSize);

        records[5] = Directory(5, parent: 5, name: ".");

        // In use, but it has no file name, so the directory cannot be placed.
        // The second file used to crash: the failed lookup was cached as "".
        byte[] nameless = new byte[1024];
        WriteHeader(nameless, flags: 0x0001);
        records[20] = MftFileRecord.Parse(nameless, 20, ClusterSize);
        records[30] = FileRecord(30, parent: 20, "orphan-a.txt");
        records[31] = FileRecord(31, parent: 20, "orphan-b.txt");

        // A parent cycle must not recurse, and must not be turned into a path either.
        records[21] = Directory(21, parent: 22, name: "a");
        records[22] = Directory(22, parent: 21, name: "b");
        records[32] = FileRecord(32, parent: 21, "loop.txt");

        records[40] = FileRecord(40, parent: 5, "keep.txt");

        (FolderNode root, List<FileEntry> files) = TreeBuilder.FromMft(records, @"C:\", null, PathFilter.Parse("", "", false), CancellationToken.None);

        FileEntry kept = Assert.Single(files);
        Assert.Equal(@"C:\keep.txt", kept.FullPath);
        Assert.Equal(5, root.Size);
    }

    [Fact]
    public void ReparsePoint_KeepsTheTargetAndIsNotFollowed()
    {
        byte[] record = new byte[1024];
        WriteHeader(record, flags: 0x0001);
        int next = WriteFileName(record, 56, parent: 5, "shortcut.txt", declaredSize: 0);
        WriteReparse(record, next, @"\??\D:\Real\file.txt");

        var records = new MftFileRecord[6];
        for (int i = 0; i < records.Length; i++)
            records[i] = MftFileRecord.Parse(new byte[1024], (ulong)i, ClusterSize);
        records[5] = Directory(5, parent: 5, name: ".");
        MftFileRecord parsed = MftFileRecord.Parse(record, 40, ClusterSize);
        records[0] = parsed;

        Assert.True(parsed.HasReparse);
        Assert.Equal(@"D:\Real\file.txt", parsed.Reparse.Target);

        (_, List<FileEntry> files) = TreeBuilder.FromMft([parsed], @"C:\", null, PathFilter.Parse("", "", false), CancellationToken.None);
        FileEntry link = Assert.Single(files);
        Assert.True(link.IsReparsePoint);
        Assert.Equal(@"D:\Real\file.txt", link.ReparseTarget);
    }

    [Fact]
    public void UpcaseTable_FoldsTheCharactersNtfsFolds()
    {
        byte[] table = new byte[NtfsUpcase.TableBytes];
        for (int i = 0; i < 65536; i++)
            BitConverter.TryWriteBytes(table.AsSpan(i * 2), (ushort)i);
        BitConverter.TryWriteBytes(table.AsSpan('i' * 2), (ushort)'I');
        BitConverter.TryWriteBytes(table.AsSpan(0x0131 * 2), (ushort)'I'); // dotless i, which ordinal comparison does not fold

        NtfsUpcase? upcase = NtfsUpcase.TryFrom(table);

        Assert.NotNull(upcase);
        Assert.True(upcase.Equals("i", "I"));
        Assert.True(upcase.Equals("\u0131", "I"));
        Assert.False(StringComparer.OrdinalIgnoreCase.Equals("\u0131", "I"));
        Assert.Equal(upcase.GetHashCode("I"), upcase.GetHashCode("\u0131"));
    }

    [Fact]
    public void UpcaseTable_CanLookAFolderUpWithoutCopyingTheSegment()
    {
        byte[] table = new byte[NtfsUpcase.TableBytes];
        for (int i = 0; i < 65536; i++)
            BitConverter.TryWriteBytes(table.AsSpan(i * 2), (ushort)char.ToUpperInvariant((char)i));

        NtfsUpcase? upcase = NtfsUpcase.TryFrom(table);
        Assert.NotNull(upcase);

        var records = new MftFileRecord[42];
        for (int i = 0; i < records.Length; i++)
            records[i] = MftFileRecord.Parse(new byte[1024], (ulong)i, ClusterSize);
        records[5] = Directory(5, parent: 5, name: ".");
        records[30] = Directory(30, parent: 5, name: "Photos");
        records[40] = FileRecord(40, parent: 30, "keep.txt");

        (FolderNode root, _) = TreeBuilder.FromMft(records, @"C:\", null, PathFilter.Parse("", "", false), CancellationToken.None, names: upcase);

        // The segment is a span of the path, and the upcase table folds case.
        // This used to throw: the table was not a span comparer, so the
        // dictionary refused the lookup.
        FolderNode? found = root.Find(@"c:\photos");

        Assert.NotNull(found);
        Assert.Equal("Photos", found.Name);
    }

    private static MftFileRecord Directory(ulong number, ulong parent, string name)
    {
        byte[] record = new byte[1024];
        WriteHeader(record, flags: 0x0003);
        WriteFileName(record, 56, parent, name, declaredSize: 0);
        return MftFileRecord.Parse(record, number, ClusterSize);
    }

    private static MftFileRecord FileRecord(ulong number, ulong parent, string name)
    {
        byte[] record = new byte[1024];
        WriteHeader(record, flags: 0x0001);
        int next = WriteFileName(record, 56, parent, name, declaredSize: 5);
        WriteResidentData(record, next, "hello"u8.ToArray());
        return MftFileRecord.Parse(record, number, ClusterSize);
    }

    private static int WriteReparse(byte[] record, int offset, string target)
    {
        byte[] text = System.Text.Encoding.Unicode.GetBytes(target);
        byte[] value = new byte[16 + text.Length];
        BitConverter.TryWriteBytes(value.AsSpan(0), 0xA0000003); // mount point
        BitConverter.TryWriteBytes(value.AsSpan(4), (ushort)(8 + text.Length));
        BitConverter.TryWriteBytes(value.AsSpan(10), (ushort)text.Length);
        BitConverter.TryWriteBytes(value.AsSpan(12), (ushort)text.Length);
        text.CopyTo(value.AsSpan(16));
        return WriteAttribute(record, offset, type: 0xC0, resident: true, nameLength: 0, value, nonResident: null);
    }

    private static void WriteHeader(byte[] record, ushort flags, ulong baseRecord = 0)
    {
        record[0] = (byte)'F';
        record[1] = (byte)'I';
        record[2] = (byte)'L';
        record[3] = (byte)'E';
        BitConverter.TryWriteBytes(record.AsSpan(4), (ushort)48); // fixup array lives in the header gap
        BitConverter.TryWriteBytes(record.AsSpan(6), (ushort)3);   // one entry per 512-byte sector, plus the number itself
        BitConverter.TryWriteBytes(record.AsSpan(20), (ushort)56);
        BitConverter.TryWriteBytes(record.AsSpan(22), flags);
        BitConverter.TryWriteBytes(record.AsSpan(32), baseRecord);

        // The sequence number is zero, and the saved sector bytes are zero too,
        // so applying the fixup does not alter the record.
    }

    private static int WriteFileName(byte[] record, int offset, ulong parent, string name, long declaredSize, byte nameSpace = 1)
    {
        byte[] value = new byte[66 + name.Length * 2];
        BitConverter.TryWriteBytes(value.AsSpan(0), parent);
        BitConverter.TryWriteBytes(value.AsSpan(40), declaredSize);
        BitConverter.TryWriteBytes(value.AsSpan(48), declaredSize);
        value[64] = (byte)name.Length;
        value[65] = nameSpace;
        System.Text.Encoding.Unicode.GetBytes(name).CopyTo(value.AsSpan(66));
        return WriteAttribute(record, offset, type: 0x30, resident: true, nameLength: 0, value, nonResident: null);
    }

    private static int WriteResidentData(byte[] record, int offset, byte[] content)
        => WriteAttribute(record, offset, type: 0x80, resident: true, nameLength: 0, content, nonResident: null);

    private static int WriteNonResidentData(byte[] record, int offset, long logical, long allocatedClusters, long sparseClusters, bool compressed)
    {
        var body = new NonResident(logical, allocatedClusters, sparseClusters, compressed);
        return WriteAttribute(record, offset, type: 0x80, resident: false, nameLength: 0, value: [], body);
    }

    private static int WriteAttributeList(byte[] record, int offset, ulong extensionRecord)
    {
        byte[] entry = new byte[26];
        BitConverter.TryWriteBytes(entry.AsSpan(0), 0x80);
        BitConverter.TryWriteBytes(entry.AsSpan(4), (ushort)26);
        BitConverter.TryWriteBytes(entry.AsSpan(16), extensionRecord);
        return WriteAttribute(record, offset, type: 0x20, resident: true, nameLength: 0, entry, nonResident: null);
    }

    private static int WriteAttribute(byte[] record, int offset, int type, bool resident, int nameLength, byte[] value, NonResident? nonResident)
    {
        BitConverter.TryWriteBytes(record.AsSpan(offset), type);
        record[offset + 8] = (byte)(resident ? 0 : 1);
        record[offset + 9] = (byte)nameLength;

        int length;
        if (resident)
        {
            int valueOffset = 24;
            BitConverter.TryWriteBytes(record.AsSpan(offset + 16), value.Length);
            BitConverter.TryWriteBytes(record.AsSpan(offset + 20), (ushort)valueOffset);
            value.CopyTo(record.AsSpan(offset + valueOffset));
            length = Align(valueOffset + value.Length);
        }
        else
        {
            NonResident body = nonResident!.Value;
            BitConverter.TryWriteBytes(record.AsSpan(offset + 32), (ushort)64); // run list follows the 64-byte header
            record[offset + 34] = (byte)(body.Compressed ? 4 : 0);             // 16-cluster compression unit
            if (body.Compressed)
                BitConverter.TryWriteBytes(record.AsSpan(offset + 12), (ushort)0x0001);
            BitConverter.TryWriteBytes(record.AsSpan(offset + 40), body.AllocatedClusters * ClusterSize);
            BitConverter.TryWriteBytes(record.AsSpan(offset + 48), body.Logical);

            int run = offset + 64;
            if (body.AllocatedClusters > 0)
            {
                record[run] = 0x21; // one byte of offset, one byte of length
                record[run + 1] = (byte)body.AllocatedClusters;
                record[run + 2] = 1; // relative cluster 1, so the run is not a hole
                run += 3;
            }

            if (body.SparseClusters > 0)
            {
                record[run] = 0x01; // a length with no offset is a hole
                record[run + 1] = (byte)body.SparseClusters;
                run += 2;
            }

            length = Align(run - offset);
        }

        BitConverter.TryWriteBytes(record.AsSpan(offset + 4), length);
        BitConverter.TryWriteBytes(record.AsSpan(offset + length), -1); // attribute list terminator
        return offset + length;
    }

    private static int Align(int value) => (value + 7) & ~7;

    private readonly record struct NonResident(long Logical, long AllocatedClusters, long SparseClusters, bool Compressed);
}
