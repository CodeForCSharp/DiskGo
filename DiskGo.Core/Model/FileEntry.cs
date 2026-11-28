using System.Diagnostics.CodeAnalysis;

namespace DiskGo.Core;

/// <summary>One file discovered by a scan. Folders are not stored; they are derived while the tree is built.</summary>
public sealed class FileEntry
{
    public FileEntry() { }

    [SetsRequiredMembers]
    internal FileEntry(FolderNode directoryNode, string name, string extension)
    {
        DirectoryNode = directoryNode;
        Name = name;
        Extension = extension;
    }

    private string? _fullPath;
    /// <summary>The folder this file sits in. Null only for a file built without one, such as an imported row.</summary>
    public FolderNode? Folder => DirectoryNode;

    internal FolderNode? DirectoryNode { get; set; }

    internal bool HasComputedPath => _fullPath is null && DirectoryNode is not null;

    public string DirectoryPath => DirectoryNode?.FullPath ?? Path.GetDirectoryName(_fullPath!) ?? "";

    internal void PinFullPath() => _fullPath ??= BuildFullPath();

    internal FileEntry CloneForFolder(FolderNode folder) => new(folder, Name, Extension)
    {
        _fullPath = HasComputedPath ? null : _fullPath,
        Size = Size,
        AllocatedSize = AllocatedSize,
        CountsTowardTotal = CountsTowardTotal,
        Created = Created,
        Modified = Modified,
        Accessed = Accessed,
        Attributes = Attributes,
        MftRecordNumber = MftRecordNumber,
        ParentRecordNumber = ParentRecordNumber,
        ShortName = ShortName,
        LinkCount = LinkCount,
        IsReparsePoint = IsReparsePoint,
        ReparseTarget = ReparseTarget,
        DuplicateCount = DuplicateCount,
        DuplicateSize = DuplicateSize,
    };

    /// <summary>Full path, without a trailing separator.</summary>
    public string FullPath
    {
        get => _fullPath ?? BuildFullPath();
        init => _fullPath = value;
    }

    private string BuildFullPath()
    {
        if (DirectoryNode is not { } dir)
            return Name;
        string dirPath = dir.FullPath;
        return dirPath.EndsWith(Path.DirectorySeparatorChar)
            ? string.Concat(dirPath, Name)
            : string.Concat(dirPath, "\\", Name);
    }

    /// <summary>The final segment of <see cref="FullPath"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Extension including the leading dot, or empty when there is none. Compared case-insensitively.</summary>
    public required string Extension { get; init; }

    /// <summary>Logical size in bytes, which is what applications see.</summary>
    public long Size { get; init; }

    /// <summary>Bytes actually reserved on disk, rounded up to clusters. Zero when the file is sparse or resident in the MFT.</summary>
    public long AllocatedSize { get; init; }

    /// <summary>
    /// False for every hard link after the first one of a record. Those names are
    /// real, but the bytes were already counted with the first name, so a folder
    /// total leaves them out. A file that is not a hard link is always counted.
    /// </summary>
    public bool CountsTowardTotal { get; init; } = true;

    public DateTime Created { get; init; }

    public DateTime Modified { get; init; }

    public DateTime Accessed { get; init; }

    public FileAttributes Attributes { get; init; }

    /// <summary>
    /// NTFS file record number, or 0 when the file came from a directory walk
    /// rather than the MFT. The number is only meaningful inside one scan.
    /// </summary>
    public ulong MftRecordNumber { get; init; }

    /// <summary>The record number of the directory that contains this file. Zero when the scan did not come from the MFT.</summary>
    public ulong ParentRecordNumber { get; init; }

    /// <summary>The DOS 8.3 short name, when the file has one besides its real name. Empty otherwise.</summary>
    public string ShortName { get; init; } = "";

    /// <summary>How many directories name this file. Greater than one when it is a hard link.</summary>
    public int LinkCount { get; init; } = 1;

    /// <summary>True when this is a symbolic link. The sizes are the link's own, not the file it points at.</summary>
    public bool IsReparsePoint { get; init; }

    /// <summary>What a symbolic link points at, or null when it is an ordinary file.</summary>
    public string? ReparseTarget { get; init; }

    /// <summary>How many other files share this file's duplicate key, including itself. Zero until duplicates are resolved, and 1 when it is unique.</summary>
    public int DuplicateCount { get; set; }

    /// <summary>Combined logical size of the duplicate group, including this file.</summary>
    public long DuplicateSize { get; set; }
}
