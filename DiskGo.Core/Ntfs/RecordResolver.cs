namespace DiskGo.Core.Ntfs;

/// <summary>
/// Joins a base file record to the extension records its <c>$ATTRIBUTE_LIST</c>
/// points at, and returns the size of the whole file.
///
/// Extension records have no name of their own. Their data attributes belong
/// to the base record, so a file whose attributes spilled out of its first
/// record is counted once, at its real size, instead of being missed or
/// counted as several nameless fragments.
/// </summary>
internal static class RecordResolver
{
    public static RecordSizes SizeOf(MftFileRecord record, IReadOnlyList<MftFileRecord> records)
    {
        RecordSizes size = record.Data;
        if (record.ExtensionRecords.Count == 0)
            return size;

        var visited = new HashSet<ulong> { record.RecordNumber };
        var pending = new Queue<ulong>(record.ExtensionRecords);

        while (pending.Count > 0)
        {
            ulong number = pending.Dequeue();
            if (!visited.Add(number) || number >= (ulong)records.Count)
                continue;

            MftFileRecord extension = records[(int)number];
            if (!extension.InUse || extension.BaseRecordNumber != record.RecordNumber)
                continue;

            size = new RecordSizes(
                size.Logical + extension.Data.Logical,
                size.Allocated + extension.Data.Allocated,
                size.Found || extension.Data.Found);

            foreach (ulong further in extension.ExtensionRecords)
                pending.Enqueue(further);
        }

        return size;
    }
}
