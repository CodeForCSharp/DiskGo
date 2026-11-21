namespace DiskGo.Core;

/// <summary>
/// Groups files that look like copies of one another and writes the result
/// back onto each <see cref="FileEntry"/>. Comparison is by name and size, not
/// by content: hashing every file would throw away the speed the MFT read is
/// there to provide. The name is
/// reduced first, so a file Windows has renamed as a copy still matches the
/// original.
/// </summary>
public static class DuplicateFinder
{
    public static void Apply(IReadOnlyList<FileEntry> files, DuplicateMode mode, CancellationToken cancellationToken, IEqualityComparer<string>? names = null)
    {
        if (mode == DuplicateMode.None || files.Count == 0)
            return;

        bool compareDate = mode == DuplicateMode.NameSizeAndDate;
        names ??= StringComparer.OrdinalIgnoreCase;
        var groups = new Dictionary<DuplicateKey, List<FileEntry>>(files.Count);

        foreach (FileEntry file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = new DuplicateKey(DuplicateName.Normalize(file.Name), file.Size, compareDate ? file.Modified : default, names);
            if (!groups.TryGetValue(key, out List<FileEntry>? group))
            {
                group = [];
                groups.Add(key, group);
            }

            group.Add(file);
        }

        foreach (List<FileEntry> group in groups.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long size = group.Count > 1 ? group[0].Size * group.Count : 0;
            foreach (FileEntry file in group)
            {
                file.DuplicateCount = group.Count;
                file.DuplicateSize = size;
            }
        }
    }

    private readonly record struct DuplicateKey(string Name, long Size, DateTime Modified, IEqualityComparer<string> Names)
    {
        public override int GetHashCode()
            => HashCode.Combine(Names.GetHashCode(Name), Size, Modified);

        public bool Equals(DuplicateKey other)
            => Size == other.Size && Modified == other.Modified && Names.Equals(Name, other.Name);
    }
}
