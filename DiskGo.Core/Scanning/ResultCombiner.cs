namespace DiskGo.Core;

/// <summary>
/// Puts several scans under one root so the rest of the program can treat them
/// as a single result. Used when the selection spans more than one volume,
/// where no single MFT contains every file.
/// </summary>
internal static class ResultCombiner
{
    public const string CombinedRoot = "Selection";

    public static (FolderNode Root, List<FileEntry> Files) Combine(IReadOnlyList<(FolderNode Root, List<FileEntry> Files)> parts)
    {
        var root = new FolderNode(CombinedRoot, CombinedRoot);
        var files = new List<FileEntry>();

        foreach ((FolderNode part, List<FileEntry> partFiles) in parts)
        {
            // The full path, not the last segment, because two selections can
            // share a name (C:\Work and D:\Work) and still be different trees.
            FolderNode child = root.GetOrAddChild(part.FullPath.TrimEnd(Path.DirectorySeparatorChar));
            Copy(part, child);
            files.AddRange(partFiles);
        }

        root.Seal();
        return (root, files);
    }

    /// <summary>
    /// Rebuilds the subtree. Sizes are not copied across, because adding each
    /// file rolls them up again; copying both would count every file twice.
    /// </summary>
    private static void Copy(FolderNode source, FolderNode target)
    {
        foreach (FileEntry file in source.Files)
        {
            file.PinFullPath();
            target.AddFile(file);
        }

        foreach (FolderNode child in source.Children)
            Copy(child, target.GetOrAddChild(child.Name));
    }
}
