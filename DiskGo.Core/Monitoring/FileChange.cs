namespace DiskGo.Core;

/// <summary>What happened to one file or folder. <see cref="Overflow"/> means some events were dropped and the list is incomplete.</summary>
public enum FileChangeKind
{
    Added,
    Removed,
    Modified,
    RenamedFrom,
    RenamedTo,
    Overflow,
}

/// <summary>
/// One change reported by a <see cref="DirectoryWatcher"/>. <see cref="Path"/> is
/// absolute. For <see cref="FileChangeKind.Overflow"/> it is the folder being
/// watched, not an individual file.
/// </summary>
public readonly record struct FileChange(FileChangeKind Kind, string Path);
