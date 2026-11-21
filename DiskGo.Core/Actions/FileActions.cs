using System.Runtime.InteropServices;
using System.Text;

namespace DiskGo.Core;

/// <summary>Where a deleted file went.</summary>
public enum DeleteMode
{
    /// <summary>Move it to the recycle bin, so it can be restored.</summary>
    RecycleBin,

    /// <summary>Remove it outright. Nothing is kept that would let it be restored.</summary>
    Permanent,
}

/// <summary>
/// Changes the files a scan found: delete one, rename one, or copy paths to
/// the clipboard. Each call names the paths it acts on. Nothing here searches
/// for files to remove, and a path the scan never saw is refused rather than
/// touched, so a stale result cannot act on a file that has since moved.
///
/// The scan is updated to match what the disk now holds. Sizes roll up again
/// from the files that remain, and the duplicate counts are worked out again
/// because a deleted copy changes the size of its group.
/// </summary>
public static partial class FileActions
{
    private const uint RecycleFlags = 0x0040 | 0x0010 | 0x0400; // no confirmation, no progress, allow undo
    private const uint PermanentFlags = 0x0040 | 0x0010; // no confirmation, no progress, and no undo
    private const uint CopyMoveFlags = 0x0040 | 0x0010 | 0x0200; // no confirmation, no progress, no directory confirmation

    /// <summary>
    /// Deletes <paramref name="path"/>. A folder takes everything inside it.
    /// Returns the files the scan no longer contains.
    /// </summary>
    public static IReadOnlyList<FileEntry> Delete(ScanResult result, string path, DeleteMode mode)
        => DeleteMany(result, [path], mode);

    /// <summary>
    /// Deletes multiple scanned paths in a single batch and updates tree totals,
    /// extension totals, and duplicate counts once at the end.
    /// </summary>
    public static IReadOnlyList<FileEntry> DeleteMany(ScanResult result, IEnumerable<string> paths, DeleteMode mode)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(paths);

        var targets = new List<(FileEntry? File, FolderNode? Folder, string FullPath)>();
        var fileSet = new HashSet<FileEntry>(ReferenceEqualityComparer.Instance);
        var folderSet = new HashSet<FolderNode>(ReferenceEqualityComparer.Instance);

        foreach (string path in paths)
        {
            FileEntry? file = result.FindFile(path);
            FolderNode? folder = file is null ? result.FindFolder(path) : null;
            if (file is null && folder is null)
                throw new ArgumentException($"{path} is not part of this scan.", nameof(path));
            if (folder == result.Root)
                throw new ArgumentException("The root of the scan cannot be deleted. Delete the files inside it instead.", nameof(path));

            if (file is not null && !fileSet.Add(file))
                continue;
            if (folder is not null && !folderSet.Add(folder))
                continue;

            targets.Add((file, folder, file?.FullPath ?? folder!.FullPath));
        }

        if (targets.Count == 0)
            return [];

        var gone = new List<FileEntry>();
        foreach ((FileEntry? file, FolderNode? folder, string targetPath) in targets)
        {
            if (IsDescendantOfAny(file?.DirectoryNode ?? folder?.Parent, folderSet))
                continue;

            RemoveFromDisk(targetPath, mode);

            if (file is not null)
            {
                (file.DirectoryNode ?? result.FindFolder(Path.GetDirectoryName(file.FullPath) ?? ""))?.RemoveFile(file);
                gone.Add(file);
            }
            else if (folder is not null)
            {
                CollectFilesInside(folder, gone);
                folder.Parent?.RemoveChild(folder);
            }
        }

        Finish(result, gone);
        return gone;
    }

    private static bool IsDescendantOfAny(FolderNode? start, HashSet<FolderNode> ancestors)
    {
        if (ancestors.Count == 0)
            return false;
        for (FolderNode? cur = start; cur is not null; cur = cur.Parent)
        {
            if (ancestors.Contains(cur))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Renames one file or folder. <paramref name="newName"/> is the new final
    /// segment, not a path: moving into another directory is not a rename.
    /// Returns the files whose path changed.
    /// </summary>
    public static IReadOnlyList<FileEntry> Rename(ScanResult result, string path, string newName)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A new name is the final part of the path, and it cannot be empty.", nameof(newName));
        if (newName is "." or "..")
            throw new ArgumentException($"'{newName}' is not a usable name.", nameof(newName));

        FileEntry? file = result.FindFile(path);
        FolderNode? folder = file is null ? result.FindFolder(path) : null;
        if (file is null && folder is null)
            throw new ArgumentException($"{path} is not part of this scan.", nameof(path));
        if (folder == result.Root)
            throw new ArgumentException("The root of the scan cannot be renamed.", nameof(path));

        string current = file?.FullPath ?? folder!.FullPath;
        string? parent = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(parent))
            throw new ArgumentException($"{path} has no parent folder to rename it in.", nameof(path));

        string renamed = Path.Combine(parent, newName);
        if (string.Equals(current, renamed, StringComparison.OrdinalIgnoreCase))
            return [];
        if (File.Exists(renamed) || Directory.Exists(renamed))
            throw new IOException($"{renamed} already exists.");

        if (file is not null)
            File.Move(current, renamed);
        else
            Directory.Move(current, renamed);

        // The parent indexes its children by name, so the folder has to leave
        // and come back under the new one. Its contents follow the new path.
        if (folder is not null)
        {
            FolderNode? parentNode = folder.Parent;
            parentNode?.RemoveChild(folder);
            folder.Retarget(renamed);
            parentNode?.Reattach(folder);
        }

        List<FileEntry> moved = file is not null ? [file] : FilesInside(folder!);
        foreach (FileEntry entry in moved)
        {
            if (folder is not null && entry.HasComputedPath)
                continue;
            Retarget(result, entry, current, renamed);
        }

        Finish(result, []);
        return moved;
    }

    /// <summary>
    /// Copies <paramref name="path"/> into <paramref name="destination"/>, which
    /// is a directory. The copy keeps the original name. The scan is unchanged:
    /// the new file was not part of it, and the original is still where it was.
    /// Returns the path of the copy.
    /// </summary>
    public static string CopyTo(ScanResult result, string path, string destination)
    {
        string source = RequireInside(result, path);
        string target = PrepareDestination(source, destination);
        ShellTransfer(source, target, function: 2);
        return target;
    }

    /// <summary>
    /// Moves <paramref name="path"/> into <paramref name="destination"/>, which
    /// is a directory, and updates the scan so the file is found at its new
    /// path. The scan root cannot be moved. Returns the files whose path changed.
    /// </summary>
    public static IReadOnlyList<FileEntry> MoveTo(ScanResult result, string path, string destination)
    {
        ArgumentNullException.ThrowIfNull(result);
        FileEntry? file = result.FindFile(path);
        FolderNode? folder = file is null ? result.FindFolder(path) : null;
        if (file is null && folder is null)
            throw new ArgumentException($"{path} is not part of this scan.", nameof(path));
        if (folder == result.Root)
            throw new ArgumentException("The root of the scan cannot be moved.", nameof(path));

        string source = file?.FullPath ?? folder!.FullPath;
        string target = PrepareDestination(source, destination);
        ShellTransfer(source, target, function: 1);

        if (folder is not null)
        {
            folder.Parent?.RemoveChild(folder);
            folder.Retarget(target);
            FolderNode? parent = result.FindFolder(Path.GetDirectoryName(target) ?? "");
            parent?.Reattach(folder);
        }

        List<FileEntry> moved = file is not null ? [file] : FilesInside(folder!);
        foreach (FileEntry entry in moved)
        {
            if (folder is not null && entry.HasComputedPath)
                continue;
            Retarget(result, entry, source, target);
        }

        Finish(result, []);
        return moved;
    }

    /// <summary>Puts one path on the clipboard, as text.</summary>
    public static void CopyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("There is no path to copy.", nameof(path));

        SetClipboardText(path);
    }

    /// <summary>Puts a path on the clipboard together with its logical and allocated size, so the size travels with the name.</summary>
    public static void CopyPathAndSize(string path, long size, long allocated)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("There is no path to copy.", nameof(path));

        SetClipboardText($"{path}\t{size}\t{allocated}");
    }

    /// <summary>Puts several paths on the clipboard, one per line.</summary>
    public static void CopyPaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            throw new ArgumentException("There is no path to copy.", nameof(paths));

        SetClipboardText(string.Join(Environment.NewLine, paths));
    }

    private static void RemoveFromDisk(string path, DeleteMode mode)
    {
        bool directory = Directory.Exists(path);
        if (!directory && !File.Exists(path))
            throw new FileNotFoundException($"{path} is no longer there.", path);

        // A folder has to go through the shell either way: File.Delete refuses
        // one, and the recycle bin is only reachable from the shell.
        if (mode == DeleteMode.Permanent && !directory)
        {
            File.Delete(path);
            return;
        }

        int result = ShellDelete(path, mode == DeleteMode.RecycleBin ? RecycleFlags : PermanentFlags);
        if (result != 0)
            throw new IOException($"{path} could not be deleted (shell error {result}).", result);
    }

    private static string RequireInside(ScanResult result, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        FileEntry? file = result.FindFile(path);
        FolderNode? folder = file is null ? result.FindFolder(path) : null;
        if (file is null && folder is null)
            throw new ArgumentException($"{path} is not part of this scan.", nameof(path));

        return file?.FullPath ?? folder!.FullPath;
    }

    /// <summary>The path the file will have inside <paramref name="destination"/>. Refuses a destination that is not a directory, or a name that is already taken.</summary>
    private static string PrepareDestination(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
            throw new DirectoryNotFoundException($"{destination} is not a directory to put the file in.");

        string name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
        string target = Path.Combine(destination, name);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"{target} already exists.");

        return target;
    }

    /// <summary>Copies (<paramref name="function"/> 2) or moves (<paramref name="function"/> 1) one path through the shell, which is what handles a folder and its contents.</summary>
    private static void ShellTransfer(string from, string to, uint function)
    {
        // The shell wants the destination directory, not the file's new full
        // path: it keeps the original name and places it there.
        string? directory = Path.GetDirectoryName(to);
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"{to} has no directory to put it in.");

        int result = ShellOperation(function, from, directory, CopyMoveFlags);
        if (result != 0)
            throw new IOException($"{from} could not be {(function == 1 ? "moved" : "copied")} (shell error {result}).", result);
    }

    private static int ShellDelete(string path, uint flags) => ShellOperation(3, path, null, flags);

    /// <summary>
    /// One shell file operation. The path lists are pointers to buffers that
    /// end in two nulls, which is the layout the call actually reads; a managed
    /// array in the struct is marshalled as a length-prefixed blob and the call
    /// rejects it.
    /// </summary>
    private static int ShellOperation(uint function, string from, string? to, uint flags)
    {
        nint source = DoubleNull(from);
        nint target = to is null ? 0 : DoubleNull(to);
        try
        {
            var operation = new ShellFileOp
            {
                Function = function,
                From = source,
                To = target,
                Flags = (ushort)flags,
            };
            return SHFileOperation(ref operation);
        }
        finally
        {
            Marshal.FreeHGlobal(source);
            if (target != 0)
                Marshal.FreeHGlobal(target);
        }
    }

    /// <summary>A path followed by two nulls: one ends the path, one ends the list.</summary>
    private static nint DoubleNull(string path)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(path + "\0\0");
        nint memory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        return memory;
    }

    private static void Retarget(ScanResult result, FileEntry file, string from, string to)
    {
        string updated = file.FullPath.Equals(from, StringComparison.OrdinalIgnoreCase)
            ? to
            : to + file.FullPath[from.Length..];

        FolderNode? parent = result.FindFolder(Path.GetDirectoryName(file.FullPath) ?? "");
        parent?.RemoveFile(file);

        var moved = new FileEntry
        {
            FullPath = updated,
            Name = Path.GetFileName(updated),
            Extension = Path.GetExtension(updated),
            Size = file.Size,
            AllocatedSize = file.AllocatedSize,
            Created = file.Created,
            Modified = file.Modified,
            Accessed = file.Accessed,
            Attributes = file.Attributes,
            MftRecordNumber = file.MftRecordNumber,
            LinkCount = file.LinkCount,
            IsReparsePoint = file.IsReparsePoint,
            ReparseTarget = file.ReparseTarget,
        };

        string? directory = Path.GetDirectoryName(updated);
        FolderNode? destination = directory is null ? null : result.FindFolder(directory);
        destination?.AddFile(moved);
        Replace(result, file, moved);
    }

    private static void Replace(ScanResult result, FileEntry from, FileEntry to)
    {
        int index = result.Files.IndexOf(from);
        if (index >= 0)
            result.Files[index] = to;
    }

    private static void Finish(ScanResult result, IReadOnlyList<FileEntry> gone)
    {
        if (gone.Count == 1)
        {
            result.Files.Remove(gone[0]);
        }
        else if (gone.Count > 1)
        {
            var removed = new HashSet<FileEntry>(gone, ReferenceEqualityComparer.Instance);
            result.Files.RemoveAll(removed.Contains);
        }

        result.Root.Seal();
        result.Extensions.Clear();
        result.Extensions.AddRange(ExtensionSummary.From(result.Files));

        // A deleted copy changes every remaining member of its group, so the
        // grouping is repeated whenever this scan had one.
        bool grouped = HadDuplicates(gone);
        foreach (FileEntry file in result.Files)
        {
            if (file.DuplicateCount > 1)
                grouped = true;
            file.DuplicateCount = 0;
            file.DuplicateSize = 0;
        }

        if (grouped)
            DuplicateFinder.Apply(result.Files, DuplicateMode.NameAndSize, CancellationToken.None);

        result.Index();
    }

    private static bool HadDuplicates(IReadOnlyList<FileEntry> gone)
    {
        foreach (FileEntry file in gone)
        {
            if (file.DuplicateCount > 1)
                return true;
        }

        return false;
    }

    private static List<FileEntry> FilesInside(FolderNode folder)
    {
        var files = new List<FileEntry>();
        CollectFilesInside(folder, files);
        return files;
    }

    private static void CollectFilesInside(FolderNode folder, List<FileEntry> files)
    {
        files.AddRange(folder.Files);
        foreach (FolderNode child in folder.Children)
            CollectFilesInside(child, files);
    }

    private static void SetClipboardText(string text)
    {
        if (!OpenClipboard(0))
            throw new IOException($"The clipboard is in use (Win32 error {Marshal.GetLastWin32Error()}).", Marshal.GetLastWin32Error());

        try
        {
            if (!EmptyClipboard())
                throw new IOException($"The clipboard could not be cleared (Win32 error {Marshal.GetLastWin32Error()}).", Marshal.GetLastWin32Error());

            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            nint memory = GlobalAlloc(0x0002, (nuint)bytes.Length); // movable
            if (memory == 0)
                throw new IOException("There is not enough memory to copy the path.");

            nint locked = GlobalLock(memory);
            try
            {
                Marshal.Copy(bytes, 0, locked, bytes.Length);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            // 13 is CF_UNICODETEXT. The clipboard owns the memory once this succeeds.
            if (SetClipboardData(13, memory) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                GlobalFree(memory);
                throw new IOException($"The path could not be copied (Win32 error {error}).", error);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ShellFileOp
    {
        public nint Window;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShellFileOp operation);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
}
