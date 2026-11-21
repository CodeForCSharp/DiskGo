using DiskGo.Core;
using Xunit;

namespace DiskGo.Tests;

public class TreemapLayoutTests
{
    [Fact]
    public void CollectAndSquarifyFillContainerWithinBounds()
    {
        var root = new FolderNode("Root", @"C:\Root");
        root.AddFile(new FileEntry
        {
            Name = "big.bin",
            FullPath = @"C:\Root\big.bin",
            Extension = ".bin",
            Size = 600,
            AllocatedSize = 600,
        });
        root.AddFile(new FileEntry
        {
            Name = "mid.txt",
            FullPath = @"C:\Root\mid.txt",
            Extension = ".txt",
            Size = 300,
            AllocatedSize = 300,
        });
        root.AddFile(new FileEntry
        {
            Name = "small.log",
            FullPath = @"C:\Root\small.log",
            Extension = ".log",
            Size = 100,
            AllocatedSize = 100,
        });
        root.Seal();

        var items = new List<TreemapLayoutItem>();
        var ranked = new List<TreemapLayoutItem>();
        long total = TreemapLayout.Collect(root, 400f * 200f, byAllocated: true, items, ranked);

        Assert.Equal(1000, total);
        Assert.Equal(3, items.Count);

        TreemapLayout.Squarify(items, total, 0, 0, 400, 200);

        double areaSum = 0;
        foreach (TreemapLayoutItem item in items)
        {
            Assert.True(item.Width > 0);
            Assert.True(item.Height > 0);
            Assert.InRange(item.X, -0.01f, 400f);
            Assert.InRange(item.Y, -0.01f, 200f);
            Assert.InRange(item.X + item.Width, 0f, 400.01f);
            Assert.InRange(item.Y + item.Height, 0f, 200.01f);
            areaSum += item.Width * item.Height;
        }

        Assert.InRange(areaSum, 400 * 200 * 0.99, 400 * 200 * 1.01);
    }

    [Fact]
    public void CollectGroupsTinyTailIntoRemainderItem()
    {
        var root = new FolderNode("Many", @"C:\Many");
        root.AddFile(new FileEntry
        {
            Name = "dominant.iso",
            FullPath = @"C:\Many\dominant.iso",
            Extension = ".iso",
            Size = 1_000_000,
            AllocatedSize = 1_000_000,
        });

        for (int i = 0; i < 20; i++)
        {
            root.AddFile(new FileEntry
            {
                Name = $"tiny_{i}.txt",
                FullPath = $@"C:\Many\tiny_{i}.txt",
                Extension = ".txt",
                Size = 10,
                AllocatedSize = 10,
            });
        }
        root.Seal();

        var items = new List<TreemapLayoutItem>();
        var ranked = new List<TreemapLayoutItem>();
        long total = TreemapLayout.Collect(root, 200f * 100f, byAllocated: true, items, ranked);

        Assert.Equal(1_000_200, total);
        Assert.Equal(2, items.Count);
        Assert.False(items[0].IsRemainder);
        Assert.True(items[1].IsRemainder);
        Assert.Equal(20, items[1].RemainderCount);
        Assert.Equal(200, items[1].Size);
        Assert.Equal(1, items[1].RemainderStartIndex);
    }
}
