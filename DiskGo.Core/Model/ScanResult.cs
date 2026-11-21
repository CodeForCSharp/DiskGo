namespace DiskGo.Core;

/// <summary>Which reader produced a <see cref="ScanResult"/>.</summary>
public enum ScanMethod
{
    /// <summary>The directory tree was read straight from the NTFS Master File Table.</summary>
    MasterFileTable,

    /// <summary>The directory tree was walked through the normal file APIs. Used for everything that is not a readable NTFS volume.</summary>
    DirectoryWalk,
}

/// <summary>Everything one scan produced. The object is immutable after <see cref="DiskScanner.Scan"/> returns it.</summary>
public sealed class ScanResult
{
    internal ScanResult(
        string rootPath,
        ScanMethod method,
        bool fellBackFromMft,
        FolderNode root,
        List<FileEntry> files,
        List<ExtensionTotal> extensions,
        DriveSpace drive,
        TimeSpan elapsed,
        IReadOnlyList<string> warnings)
    {
        RootPath = rootPath;
        Method = method;
        FellBackFromMft = fellBackFromMft;
        Root = root;
        Files = files;
        Extensions = extensions;
        Drive = drive;
        Elapsed = elapsed;
        Warnings = warnings;
    }

    /// <summary>
    /// Kept for callers that mutate the tree and signal an index refresh.
    /// Lookups walk the tree directly so no per-file path dictionary is retained.
    /// </summary>
    internal void Index() { }

    public string RootPath { get; }

    public ScanMethod Method { get; }

    /// <summary>True when an MFT read was attempted and the directory walk was used because it failed.</summary>
    public bool FellBackFromMft { get; }

    public FolderNode Root { get; }

    /// <summary>Every file, in discovery order. Sort a copy; the list itself stays put.</summary>
    public List<FileEntry> Files { get; }

    /// <summary>File types ordered by the space they use, largest first. Files with no extension are grouped as "(none)".</summary>
    public List<ExtensionTotal> Extensions { get; }

    public DriveSpace Drive { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>Non-fatal problems, such as directories Windows refused to open. The scan still completed.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Files that belong to a duplicate group, ordered so members of one group
    /// sit together and the largest groups come first. Empty unless the scan
    /// was asked to find duplicates.
    /// </summary>
    public IEnumerable<FileEntry> DuplicateFiles()
        => Files
            .Where(static file => file.DuplicateCount > 1)
            .OrderByDescending(static file => file.DuplicateSize)
            .ThenBy(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>The file at <paramref name="path"/>, or null when this scan did not see it.</summary>
    public FileEntry? FindFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        string? directory = Path.GetDirectoryName(path);
        FolderNode? parent = string.IsNullOrEmpty(directory) ? Root : FindFolder(directory);
        if (parent is null)
            return null;

        ReadOnlySpan<char> fileName = Path.GetFileName(path.AsSpan());
        foreach (FileEntry file in parent.Files)
        {
            if (file.Name.AsSpan().Equals(fileName, StringComparison.OrdinalIgnoreCase)
                && file.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                return file;
        }

        foreach (FolderNode child in parent.Children)
        {
            if (!child.IsFileGroup)
                continue;
            foreach (FileEntry file in child.Files)
            {
                if (file.Name.AsSpan().Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    && file.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }

        return null;
    }

    /// <summary>The folder at <paramref name="path"/>, or null when this scan did not see it.</summary>
    public FolderNode? FindFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (Root.FullPath == ResultCombiner.CombinedRoot)
        {
            string wanted = path.TrimEnd(Path.DirectorySeparatorChar);
            if (wanted.Equals(ResultCombiner.CombinedRoot, StringComparison.OrdinalIgnoreCase))
                return Root;

            foreach (FolderNode part in Root.Children)
            {
                if (wanted.Equals(part.Name, StringComparison.OrdinalIgnoreCase))
                    return part;
                if (wanted.Length > part.Name.Length
                    && wanted[part.Name.Length] == Path.DirectorySeparatorChar
                    && wanted.StartsWith(part.Name, StringComparison.OrdinalIgnoreCase))
                    return part.FindFromRelative(wanted.AsSpan(part.Name.Length + 1));
            }
        }

        return Root.Find(path);
    }

    /// <summary>
    /// Replaces one folder with a freshly scanned copy of it. Files that lived
    /// inside the old folder leave the result, and the files of
    /// <paramref name="replacement"/> join it. Totals and duplicate groups are
    /// worked out again. The replacement has to cover the same path.
    /// </summary>
    public void ReplaceFolder(string path, ScanResult replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        FolderNode? existing = FindFolder(path);
        if (existing is null)
            throw new ArgumentException($"{path} is not a folder in this scan.", nameof(path));
        if (existing == Root)
            throw new ArgumentException("The root is replaced by scanning it again, not by replacing a folder.", nameof(path));
        if (existing.Parent is null)
            throw new ArgumentException($"{path} has no parent to hang the new scan on.", nameof(path));

        string expected = existing.FullPath.TrimEnd(Path.DirectorySeparatorChar);
        string actual = replacement.Root.FullPath.TrimEnd(Path.DirectorySeparatorChar);
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{replacement.Root.FullPath} does not cover {existing.FullPath}.", nameof(replacement));

        foreach (FileEntry file in FilesInside(existing))
            Files.Remove(file);
        Files.AddRange(replacement.Files);

        existing.Parent.ReplaceChild(existing.Name, replacement.Root);
        Root.Seal();

        Extensions.Clear();
        Extensions.AddRange(ExtensionSummary.From(Files));

        bool grouped = false;
        foreach (FileEntry file in Files)
        {
            if (file.DuplicateCount > 1)
                grouped = true;
            file.DuplicateCount = 0;
            file.DuplicateSize = 0;
        }

        if (grouped)
            DuplicateFinder.Apply(Files, DuplicateMode.NameAndSize, CancellationToken.None);

        Index();
    }

    private static List<FileEntry> FilesInside(FolderNode folder)
    {
        var files = new List<FileEntry>(folder.Files);
        foreach (FolderNode child in folder.Children)
            files.AddRange(FilesInside(child));
        return files;
    }
}
