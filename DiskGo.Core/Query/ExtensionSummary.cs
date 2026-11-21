namespace DiskGo.Core;

/// <summary>Groups files by extension. Files without one share the "(none)" row, and the largest group comes first.</summary>
public static class ExtensionSummary
{
    public static List<ExtensionTotal> From(IEnumerable<FileEntry> files, CancellationToken cancellationToken = default)
    {
        var totals = new Dictionary<string, Mutable>(StringComparer.OrdinalIgnoreCase);
        foreach (FileEntry file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = file.Extension.Length == 0 ? "(none)" : file.Extension;
            if (!totals.TryGetValue(extension, out Mutable? total))
            {
                total = new Mutable();
                totals.Add(extension, total);
            }

            total.Count++;
            if (!file.CountsTowardTotal)
                continue;

            total.Size += file.Size;
            total.Allocated += file.AllocatedSize;
        }

        return totals
            .Select(static pair => new ExtensionTotal(
                pair.Key == "(none)" ? "(none)" : pair.Key.ToLowerInvariant(),
                pair.Value.Count,
                pair.Value.Size,
                pair.Value.Allocated))
            .OrderByDescending(static total => total.Size)
            .ToList();
    }

    private sealed class Mutable
    {
        public long Count;
        public long Size;
        public long Allocated;
    }
}
