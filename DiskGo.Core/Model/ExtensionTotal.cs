namespace DiskGo.Core;

/// <summary>One row of the "which kinds of file use the space" summary.</summary>
public readonly record struct ExtensionTotal(string Extension, long FileCount, long Size, long AllocatedSize)
{
    /// <summary>Share of the supplied total, from 0 to 100.</summary>
    public double PercentOf(long totalSize) => totalSize == 0 ? 0d : 100d * Size / totalSize;
}
