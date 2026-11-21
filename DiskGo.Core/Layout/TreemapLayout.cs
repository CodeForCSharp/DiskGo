namespace DiskGo.Core;

/// <summary>One item positioned inside a squarified treemap container.</summary>
public sealed class TreemapLayoutItem
{
    public TreemapLayoutItem(
        long size,
        FolderNode? folder,
        FileEntry? file,
        bool isRemainder = false,
        int remainderCount = 0,
        int remainderStartIndex = 0)
    {
        Size = size;
        Folder = folder;
        File = file;
        IsRemainder = isRemainder;
        RemainderCount = remainderCount;
        RemainderStartIndex = remainderStartIndex;
    }

    public long Size { get; }
    public FolderNode? Folder { get; }
    public FileEntry? File { get; }
    public bool IsRemainder { get; }
    public int RemainderCount { get; }
    public int RemainderStartIndex { get; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

/// <summary>
/// Pure squarified treemap item collection and rectangle layout algorithm,
/// independent of pixel rendering and UI frameworks.
/// </summary>
public static class TreemapLayout
{
    public static long Collect(
        FolderNode folder,
        float area,
        bool byAllocated,
        List<TreemapLayoutItem> items,
        List<TreemapLayoutItem> ranked)
    {
        items.Clear();
        ranked.Clear();

        long total = byAllocated ? folder.AllocatedSize : folder.Size;
        if (total <= 0 || area < 4)
            return 0;

        long minimum = Math.Max(1, (long)(total / area * 1.5));
        int maxItems = Math.Clamp((int)(area / 2), 512, 4096);

        ranked.Capacity = Math.Max(ranked.Capacity, folder.Children.Count + folder.Files.Count);
        foreach (FolderNode child in folder.Children)
        {
            long size = byAllocated ? child.AllocatedSize : child.Size;
            if (size > 0)
                ranked.Add(new TreemapLayoutItem(size, child, null));
        }

        foreach (FileEntry file in folder.Files)
        {
            long size = byAllocated ? file.AllocatedSize : file.Size;
            if (size > 0)
                ranked.Add(new TreemapLayoutItem(size, null, file));
        }

        ranked.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        int count = 0;
        long collectedSum = 0;
        for (int i = 0; i < ranked.Count; i++)
        {
            TreemapLayoutItem item = ranked[i];
            if ((count > 0 && item.Size < minimum) || count >= maxItems)
                break;
            items.Add(item);
            collectedSum += item.Size;
            count++;
        }

        long remainder = total - collectedSum;
        int skippedCount = ranked.Count - count;
        if (remainder > 0 && skippedCount > 0)
        {
            items.Add(new TreemapLayoutItem(
                remainder,
                null,
                null,
                isRemainder: true,
                remainderCount: skippedCount,
                remainderStartIndex: count));
        }

        return collectedSum + Math.Max(0, remainder);
    }

    public static void Squarify(IList<TreemapLayoutItem> items, long total, float x, float y, float width, float height)
    {
        int start = 0;
        while (start < items.Count && width > 1 && height > 1 && total > 0)
        {
            bool horizontal = width >= height;
            float length = horizontal ? height : width;
            float side = horizontal ? width : height;
            int end = start;
            double row = items[start].Size;
            double worst = Worst(row, items[start].Size, length, total, side);
            while (end + 1 < items.Count)
            {
                double next = row + items[end + 1].Size;
                double candidate = Worst(next, items[end + 1].Size, length, total, side);
                if (candidate > worst)
                    break;
                end++;
                row = next;
                worst = candidate;
            }

            float thickness = end == items.Count - 1
                ? side
                : Math.Min(side, (float)(side * row / total));
            float cursor = 0;
            for (int i = start; i <= end; i++)
            {
                float share = row == 0 ? 0 : (float)(length * items[i].Size / row);
                if (i == end && (cursor + share > length || Math.Abs((cursor + share) - length) < 1.5f))
                    share = Math.Max(0, length - cursor);

                if (horizontal)
                {
                    items[i].X = x;
                    items[i].Y = y + cursor;
                    items[i].Width = thickness;
                    items[i].Height = share;
                }
                else
                {
                    items[i].X = x + cursor;
                    items[i].Y = y;
                    items[i].Width = share;
                    items[i].Height = thickness;
                }

                cursor += share;
            }

            if (horizontal)
            {
                x += thickness;
                width -= thickness;
            }
            else
            {
                y += thickness;
                height -= thickness;
            }

            total -= (long)row;
            start = end + 1;
        }
    }

    private static double Worst(double row, double item, float length, long total, float side)
    {
        if (row <= 0 || total <= 0 || side <= 0)
            return double.MaxValue;
        double thickness = side * row / total;
        double itemLength = length * item / row;
        return thickness == 0 ? double.MaxValue : Math.Max(itemLength / thickness, thickness / itemLength);
    }
}
