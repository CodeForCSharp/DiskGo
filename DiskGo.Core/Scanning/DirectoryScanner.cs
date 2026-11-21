using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskGo.Core;

/// <summary>
/// The portable scan. It works for any directory Windows can enumerate:
/// non-NTFS volumes, network shares, removable drives and single folders.
/// It is far slower than <see cref="Ntfs.MftScanner"/> on a large NTFS volume
/// because every directory is a separate call into the file system.
///
/// Several threads list directories at once. One directory is only ever listed
/// by one thread, and that thread is also the only one that adds to its node,
/// so the tree itself needs no locking. The shared state is the queue of
/// directories still to list and the list of files found so far.
/// </summary>
internal static partial class DirectoryScanner
{
    private const uint FileListDirectory = 0x0001;
    private const uint ShareAll = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const int FileIdBothDirectoryInfo = 10;
    private const int ErrorNoMoreFiles = 18;
    private const int BufferBytes = 64 * 1024;

    public static (FolderNode Root, List<FileEntry> Files, List<string> Warnings) Scan(
        string path,
        PathFilter filter,
        CancellationToken cancellationToken,
        Action<long, long>? report = null,
        int workers = 0)
    {
        string full = VolumeMounts.IsVolumeName(path) ? path.Trim() : Path.GetFullPath(path);
        string rootPath = IsVolumeRoot(full)
            ? DriveRoot(full)
            : VolumeMounts.IsVolumeName(full)
                ? full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
                : full.TrimEnd(Path.DirectorySeparatorChar);
        var root = new FolderNode(RootLabel(rootPath), rootPath);

        var files = new ConcurrentBag<FileEntry>();
        var warnings = new ConcurrentBag<string>();
        var counted = new System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>();
        var pending = new ConcurrentQueue<(string Path, FolderNode Node)>();
        pending.Enqueue((rootPath, root));

        // A caller that named a count is obeyed. Otherwise a spinning disk is
        // listed by one thread, because several threads seeking the same platter
        // are slower than one, and everything else uses a handful of threads.
        int threads = workers > 0 ? workers : ThreadsFor(rootPath);
        long folders = 1;
        int outstanding = 1;
        using var work = new SemaphoreSlim(1, int.MaxValue);
        using var finished = new CountdownEvent(threads);
        Exception? failure = null;

        for (int i = 0; i < threads; i++)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    ListOne(pending, files, warnings, counted, filter, work, ref outstanding, ref folders, cancellationToken, report);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    // Wake every thread waiting for more work, including this one.
                    Interlocked.Exchange(ref outstanding, 0);
                    work.Release(threads);
                }
                finally
                {
                    finished.Signal();
                }
            })
            { IsBackground = true, Name = "DiskGo.DirectoryScanner" };
            thread.Start();
        }

        finished.Wait(cancellationToken);
        if (failure is not null)
            throw new IOException(failure.Message, failure);

        root.Seal();
        report?.Invoke(files.Count, Volatile.Read(ref folders));
        var fileList = new List<FileEntry>(files.Count);
        fileList.AddRange(files);
        return (root, fileList, [.. warnings]);
    }

    /// <summary>
    /// One thread when the volume has a seek penalty, and between 2 and 8
    /// otherwise. A path that is not a volume, or a volume that will not say,
    /// keeps the several threads: a folder on an SSD is the common case.
    /// </summary>
    private static int ThreadsFor(string rootPath)
    {
        char letter = ScanTargets.DriveLetter(rootPath);
        if (letter != '\0' && Ntfs.VolumeReader.VolumeSeeks(letter))
            return 1;

        return Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    private static void ListOne(
        ConcurrentQueue<(string Path, FolderNode Node)> pending,
        ConcurrentBag<FileEntry> files,
        ConcurrentBag<string> warnings,
        ConcurrentDictionary<ulong, byte> counted,
        PathFilter filter,
        SemaphoreSlim work,
        ref int outstanding,
        ref long folders,
        CancellationToken cancellationToken,
        Action<long, long>? report)
    {
        var extPool = new Dictionary<string, string>(128, StringComparer.Ordinal);
        var extLookup = extPool.GetAlternateLookup<ReadOnlySpan<char>>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pending.TryDequeue(out (string Path, FolderNode Node) item))
            {
                // Nothing queued. Either another thread is still listing a
                // directory that will queue more, or the walk is finished.
                if (Volatile.Read(ref outstanding) == 0)
                    return;

                work.Wait(cancellationToken);
                continue;
            }

            Enumerate(item.Path, item.Node, pending, files, warnings, counted, filter, extLookup, work, ref outstanding, ref folders, cancellationToken, report);

            if (Interlocked.Decrement(ref outstanding) == 0)
                work.Release(Environment.ProcessorCount);
        }
    }

    private static void Enumerate(
        string directory,
        FolderNode node,
        ConcurrentQueue<(string Path, FolderNode Node)> pending,
        ConcurrentBag<FileEntry> files,
        ConcurrentBag<string> warnings,
        ConcurrentDictionary<ulong, byte> counted,
        PathFilter filter,
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extLookup,
        SemaphoreSlim work,
        ref int outstanding,
        ref long folders,
        CancellationToken cancellationToken,
        Action<long, long>? report)
    {
        // One call returns a whole buffer of names, sizes and attributes, which
        // is far fewer round trips than asking for each entry on its own. The
        // \?\ prefix is what lets a path longer than 260 characters through.
        SafeFileHandle handle;
        try
        {
            handle = OpenDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{directory}: {ex.Message}");
            return;
        }

        using (handle)
        {
            // A directory that will not answer a 64KB question is asked again
            // with half of that, from a fresh handle: the first call already
            // moved this one. Only when both fail does the listing fall back
            // to asking for one entry at a time.
            if (ListBuffered(handle, directory, node, pending, files, warnings, counted, filter, extLookup, work, ref outstanding, ref folders, report, cancellationToken, BufferBytes))
                return;
        }

        try
        {
            using SafeFileHandle again = OpenDirectory(directory);
            if (ListBuffered(again, directory, node, pending, files, warnings, counted, filter, extLookup, work, ref outstanding, ref folders, report, cancellationToken, BufferBytes / 2))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{directory}: {ex.Message}");
        }

        ListOneByOne(directory, node, pending, files, warnings, counted, filter, extLookup, work, ref outstanding, ref folders, report, cancellationToken);
    }

    /// <summary>Reads the directory in blocks of <paramref name="bufferBytes"/>. Returns false when the directory rejected that size.</summary>
    private static bool ListBuffered(
        SafeFileHandle handle,
        string directory,
        FolderNode node,
        ConcurrentQueue<(string Path, FolderNode Node)> pending,
        ConcurrentBag<FileEntry> files,
        ConcurrentBag<string> warnings,
        ConcurrentDictionary<ulong, byte> counted,
        PathFilter filter,
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extLookup,
        SemaphoreSlim work,
        ref int outstanding,
        ref long folders,
        Action<long, long>? report,
        CancellationToken cancellationToken,
        int bufferBytes)
    {
        var buffer = new byte[bufferBytes];
        bool first = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GetFileInformationByHandleEx(handle, FileIdBothDirectoryInfo, buffer, buffer.Length))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreFiles)
                    return true;

                // A failure on the first call means this buffer size was refused.
                // A failure later means the listing itself broke, and the caller
                // should not start it again from the top.
                if (!first)
                    warnings.Add($"{directory}: the directory could not be listed (Win32 error {error}).");
                return !first;
            }

            first = false;
            ReadEntries(buffer, directory, node, pending, files, warnings, counted, filter, extLookup, work, ref outstanding, ref folders, report);
        }
    }

    /// <summary>The last resort: one entry per call, which every directory Windows can open will answer.</summary>
    private static unsafe void ListOneByOne(
        string directory,
        FolderNode node,
        ConcurrentQueue<(string Path, FolderNode Node)> pending,
        ConcurrentBag<FileEntry> files,
        ConcurrentBag<string> warnings,
        ConcurrentDictionary<ulong, byte> counted,
        PathFilter filter,
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extLookup,
        SemaphoreSlim work,
        ref int outstanding,
        ref long folders,
        Action<long, long>? report,
        CancellationToken cancellationToken)
    {
        FindData found;
        FindData* data = &found;
        nint find = FindFirstFileEx(ToNative(directory.TrimEnd('\\') + @"\*"), 1, data, 0, 0, 0);
        if (find == new nint(-1))
        {
            warnings.Add($"{directory}: the directory could not be listed (Win32 error {Marshal.GetLastWin32Error()}).");
            return;
        }

        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = FixedChars(new ReadOnlySpan<char>(data->Name, 260));
                if (name is "." or "..")
                    continue;

                long size = ((long)data->SizeHigh << 32) | data->SizeLow;
                ReadEntry(directory, name, FixedChars(new ReadOnlySpan<char>(data->ShortName, 14)), 0, size, size, data->Attributes, data->Created, data->Accessed, data->Written,
                    node, pending, files, counted, filter, extLookup, work, ref outstanding, ref folders, report);
            }
            while (FindNextFile(find, data));
        }
        finally
        {
            FindClose(find);
        }
    }

    private static void ReadEntries(
        ReadOnlySpan<byte> buffer,
        string directory,
        FolderNode node,
        ConcurrentQueue<(string Path, FolderNode Node)> pending,
        ConcurrentBag<FileEntry> files,
        ConcurrentBag<string> warnings,
        ConcurrentDictionary<ulong, byte> counted,
        PathFilter filter,
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extLookup,
        SemaphoreSlim work,
        ref int outstanding,
        ref long folders,
        Action<long, long>? report)
    {
        // FILE_ID_BOTH_DIR_INFO: the name starts at byte 104, and a short name,
        // when there is one, is carried alongside it.
        int offset = 0;
        while (offset + 104 <= buffer.Length)
        {
            int next = BitConverter.ToInt32(buffer.Slice(offset, 4));
            long created = BitConverter.ToInt64(buffer.Slice(offset + 8, 8));
            long accessed = BitConverter.ToInt64(buffer.Slice(offset + 16, 8));
            long written = BitConverter.ToInt64(buffer.Slice(offset + 24, 8));
            long size = BitConverter.ToInt64(buffer.Slice(offset + 40, 8));
            long allocated = BitConverter.ToInt64(buffer.Slice(offset + 48, 8));
            var attributes = (FileAttributes)BitConverter.ToUInt32(buffer.Slice(offset + 56, 4));
            int nameBytes = BitConverter.ToInt32(buffer.Slice(offset + 60, 4));
            int shortBytes = buffer[offset + 68] * 2;
            ulong fileId = BitConverter.ToUInt64(buffer.Slice(offset + 96, 8));
            if (nameBytes < 0 || offset + 104 + nameBytes > buffer.Length)
                break;

            string name = System.Text.Encoding.Unicode.GetString(buffer.Slice(offset + 104, nameBytes));
            string shortName = shortBytes > 0 && offset + 70 + shortBytes <= buffer.Length
                ? System.Text.Encoding.Unicode.GetString(buffer.Slice(offset + 70, shortBytes))
                : "";
            if (name is "." or "..")
            {
                if (next <= 0)
                    break;
                offset += next;
                continue;
            }

            ReadEntry(directory, name, shortName, fileId, size, allocated, attributes, created, accessed, written,
                node, pending, files, counted, filter, extLookup, work, ref outstanding, ref folders, report);

            if (next <= 0)
                break;
            offset += next;
        }

        _ = warnings;
    }

    private static void ReadEntry(
        string directory,
        string name,
        string shortName,
        ulong fileId,
        long size,
        long allocated,
        FileAttributes attributes,
        long created,
        long accessed,
        long written,
        FolderNode node,
        ConcurrentQueue<(string Path, FolderNode Node)> pending,
        ConcurrentBag<FileEntry> files,
        ConcurrentDictionary<ulong, byte> counted,
        PathFilter filter,
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extLookup,
        SemaphoreSlim work,
        ref int outstanding,
        ref long folders,
        Action<long, long>? report)
    {
        // Reparse points are junctions and symbolic links. Following them would double count, and can loop,
        // so the link is recorded and its target is not walked.
        bool reparse = (attributes & FileAttributes.ReparsePoint) != 0;

        if ((attributes & FileAttributes.Directory) != 0)
        {
            string entry = directory.EndsWith(Path.DirectorySeparatorChar) ? directory + name : directory + Path.DirectorySeparatorChar + name;
            ReparseInfo link = reparse && ReparsePoint.TryRead(entry, out ReparseInfo info) ? info : default;
            FolderNode child = node.GetOrAddChild(name);
            child.SetOwnModified(FileTime(written));
            Interlocked.Increment(ref folders);
            if (reparse)
                child.MarkReparse(link);
            else if (!filter.ExcludesDirectory(entry))
            {
                // Count the directory before queueing it. The reverse order
                // lets the last worker decide the walk is finished in between.
                Interlocked.Increment(ref outstanding);
                pending.Enqueue((entry, child));
                work.Release();
            }
        }
        else
        {
            ReparseInfo link = default;
            bool needEntry = reparse || (attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0;
            if (needEntry)
            {
                string entry = directory.EndsWith(Path.DirectorySeparatorChar) ? directory + name : directory + Path.DirectorySeparatorChar + name;
                if (reparse && ReparsePoint.TryRead(entry, out ReparseInfo info))
                    link = info;

                // The directory listing reports the logical size as allocated for a
                // compressed or sparse file. Ask for the reserved size in that one case.
                if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
                    allocated = AllocatedSize(entry, size);
            }

            // Two hard links share a file id. The first name this scan meets
            // keeps the bytes; every later name is listed and adds nothing.
            bool counts = fileId == 0 || counted.TryAdd(fileId, 0);
            var file = new FileEntry(node, name, InternExtension(name, extLookup))
            {
                Size = size,
                AllocatedSize = allocated,
                CountsTowardTotal = counts,
                Created = FileTime(created),
                Modified = FileTime(written),
                Accessed = FileTime(accessed),
                Attributes = attributes,
                ShortName = shortName.Equals(name, StringComparison.OrdinalIgnoreCase) ? "" : shortName,
                IsReparsePoint = link.IsLink,
                ReparseTarget = link.IsLink ? link.Target : null,
            };

            if (filter.Accepts(file))
            {
                files.Add(file);
                node.AddFile(file);
            }
        }

        if (((files.Count + Volatile.Read(ref folders)) & 0x0FFF) == 0)
            report?.Invoke(files.Count, Volatile.Read(ref folders));
    }

    private static string InternExtension(string name, Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extLookup)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return "";

        ReadOnlySpan<char> ext = name.AsSpan(dot);
        if (extLookup.TryGetValue(ext, out string? existing))
            return existing;

        string created = ext.ToString();
        extLookup.Dictionary[created] = created;
        return created;
    }

    private static SafeFileHandle OpenDirectory(string directory)
    {
        SafeFileHandle handle = CreateFile(ToNative(directory), FileListDirectory, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"{directory} could not be opened (Win32 error {error}).", error);
        }

        return handle;
    }

    /// <summary>The long-path form: <c>\\?\C:\Dir</c>, or <c>\\?\UNC\server\share</c>.</summary>
    private static string ToNative(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\" , StringComparison.Ordinal))
            return full;

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + full[2..];

        return @"\\?\" + full;
    }

    private static DateTime FileTime(long value)
    {
        if (value <= 0)
            return DateTime.MinValue;

        try
        {
            return DateTime.FromFileTime(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool TryReadFile(string path, FileAttributes attributes, ReparseInfo link, out FileEntry file)
    {
        file = null!;
        try
        {
            var info = new FileInfo(path);
            file = new FileEntry
            {
                FullPath = info.FullName,
                Name = info.Name,
                Extension = info.Extension,
                Size = info.Length,
                AllocatedSize = AllocatedSize(info.FullName, info.Length),
                Created = info.CreationTime,
                Modified = info.LastWriteTime,
                Accessed = info.LastAccessTime,
                Attributes = attributes,
                IsReparsePoint = link.IsLink,
                ReparseTarget = link.IsLink ? link.Target : null,
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Bytes actually reserved for the file, which is smaller than the logical
    /// size for compressed and sparse files. A file that cannot be queried
    /// keeps its logical size rather than contributing nothing.
    /// </summary>
    private static long AllocatedSize(string path, long logicalSize)
    {
        uint high;
        uint low = GetCompressedFileSizeW(path, out high);
        if (low == 0xFFFF_FFFF && Marshal.GetLastWin32Error() != 0)
            return logicalSize;

        return ((long)high << 32) | low;
    }

    private static bool IsVolumeRoot(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length == 2 && trimmed[1] == ':';
    }

    private static string DriveRoot(string path) => char.ToUpperInvariant(path[0]) + @":\";

    private static string RootLabel(string rootPath)
        => rootPath.TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } trimmed ? trimmed : rootPath;

    /// <summary>The characters of a fixed Windows name buffer, up to the first null.</summary>
    private static string FixedChars(ReadOnlySpan<char> chars)
    {
        int end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int fileInformationClass, byte[] buffer, int bufferSize);

    /// <summary>
    /// <c>WIN32_FIND_DATAW</c>. Packing is 4 because a file time is two
    /// <c>DWORD</c>s, not an 8-byte-aligned <c>long</c>, and the names are fixed
    /// character arrays so the pointer can be handed over without marshalling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct FindData
    {
        public FileAttributes Attributes;
        public long Created;
        public long Accessed;
        public long Written;
        public uint SizeHigh;
        public uint SizeLow;
        private uint _reserved0;
        private uint _reserved1;
        public fixed char Name[260];
        public fixed char ShortName[14];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial nint FindFirstFileEx(string pattern, int infoLevel, FindData* found, int searchOp, nint filter, int flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool FindNextFile(nint find, FindData* found);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint find);
}
