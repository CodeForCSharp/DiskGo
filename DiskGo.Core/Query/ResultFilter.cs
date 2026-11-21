namespace DiskGo.Core;

/// <summary>
/// Narrows a finished scan without reading the disk again. The include and
/// exclude text is either a list of <c>*</c> and <c>?</c> patterns separated by
/// semicolons, or one regular expression when <see cref="ScanOptions.UseRegex"/>
/// was set. Matching ignores case, the same way Windows compares file names.
/// </summary>
public static class ResultFilter
{
    /// <summary>
    /// Returns a new result containing only the files <paramref name="options"/>
    /// keeps. Folder sizes are added up again from what remains, so a folder
    /// whose files were all removed reports zero rather than its old total.
    /// </summary>
    public static ScanResult Apply(ScanResult result, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        SearchQuery include = SearchQuery.Parse(options.IncludeFilter, options.FilterFullPath, options.UseRegex);
        SearchQuery exclude = SearchQuery.Parse(options.ExcludeFilter, options.FilterFullPath, options.UseRegex);

        var kept = new List<FileEntry>();
        foreach (FileEntry file in result.Files)
        {
            if (!include.IsEmpty && !include.Matches(file))
                continue;
            if (!exclude.IsEmpty && exclude.Matches(file))
                continue;

            kept.Add(file);
        }

        return Rebuild(result, kept, options);
    }

    private static ScanResult Rebuild(ScanResult source, List<FileEntry> files, ScanOptions options)
    {
        var root = new FolderNode(source.Root.Name, source.Root.FullPath);
        string rootTrimmed = source.Root.FullPath.TrimEnd(Path.DirectorySeparatorChar);
        var rebuiltFiles = new List<FileEntry>(files.Count);

        foreach (FileEntry file in files)
        {
            string directory = file.DirectoryPath;
            FolderNode folder = directory.Length == 0 ? root : FolderFor(root, rootTrimmed, directory);
            FileEntry copy = file.CloneForFolder(folder);
            folder.AddFile(copy);
            rebuiltFiles.Add(copy);
        }

        root.Seal();
        root.UseNewestModified(options.ShowNewestModified);
        root.SortBySizeDescending();
        return new ScanResult(
            source.RootPath,
            source.Method,
            source.FellBackFromMft,
            root,
            rebuiltFiles,
            ExtensionSummary.From(rebuiltFiles),
            source.Drive,
            source.Elapsed,
            source.Warnings);
    }

    private static FolderNode FolderFor(FolderNode root, string rootTrimmed, string directory)
    {
        ReadOnlySpan<char> dirSpan = directory.AsSpan().TrimEnd(Path.DirectorySeparatorChar);
        if (dirSpan.Equals(rootTrimmed.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return root;

        ReadOnlySpan<char> relative = dirSpan.Length > rootTrimmed.Length
            && dirSpan[rootTrimmed.Length] == Path.DirectorySeparatorChar
            && dirSpan.StartsWith(rootTrimmed.AsSpan(), StringComparison.OrdinalIgnoreCase)
                ? dirSpan[(rootTrimmed.Length + 1)..]
                : dirSpan;

        FolderNode node = root;
        while (!relative.IsEmpty)
        {
            int sep = relative.IndexOf(Path.DirectorySeparatorChar);
            ReadOnlySpan<char> segment = sep < 0 ? relative : relative[..sep];
            if (!segment.IsEmpty)
                node = node.GetOrAddChild(segment);
            if (sep < 0)
                break;
            relative = relative[(sep + 1)..];
        }

        return node;
    }
}
