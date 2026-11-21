using DiskGo.Core.Ntfs;

namespace DiskGo.Core;

/// <summary>
/// Turns raw MFT records into the same tree a directory walk produces.
///
/// Records point at their parent by record number rather than by path, so the
/// path of every directory is resolved once and cached. Record 5 is the root
/// of an NTFS volume, whatever letter it happens to be mounted under.
/// </summary>
internal static class TreeBuilder
{
    private const ulong NtfsRootRecord = 5;

    // A failed lookup has to stay failed. An empty string would be a real cache
    // hit, and the next file in that directory was then sliced as if it were a path.
    private const string Resolving = "\0";
    private const string Unresolved = "\0\0";

    private static readonly FolderNode UnresolvedFolder = new("", "");
    private static readonly FolderNode ResolvingFolder = new("", "");

    public static (FolderNode Root, List<FileEntry> Files) FromMft(
        IReadOnlyList<MftFileRecord> records,
        string volumeRoot,
        IReadOnlyList<string>? restrictTo,
        PathFilter filter,
        CancellationToken cancellationToken,
        Action<long, long>? report = null,
        IEqualityComparer<string>? names = null)
        => FromMft(records, volumeRoot, restrictTo, filter, includeMetadata: false, cancellationToken, report, names);

    public static (FolderNode Root, List<FileEntry> Files) FromMft(
        IReadOnlyList<MftFileRecord> records,
        string volumeRoot,
        IReadOnlyList<string>? restrictTo,
        PathFilter filter,
        bool includeMetadata,
        CancellationToken cancellationToken,
        Action<long, long>? report = null,
        IEqualityComparer<string>? names = null)
    {
        string rootPath = NormalizeRoot(volumeRoot);
        string[] scopes = restrictTo is null ? [] : restrictTo.Select(NormalizeOne).ToArray();
        bool scoped = scopes.Length > 0;

        string?[]? directoryPaths = scoped ? new string?[records.Count] : null;
        FolderNode?[]? folderNodes = scoped ? null : new FolderNode?[records.Count];
        var extensionPool = new Dictionary<string, string>(256, StringComparer.Ordinal);
        var extLookup = extensionPool.GetAlternateLookup<ReadOnlySpan<char>>();

        var root = new FolderNode(RootName(rootPath), rootPath, names);
        var files = new List<FileEntry>(scoped ? 256 : Math.Min(records.Count, 65_536));
        long folders = 1;

        for (int index = 0; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MftFileRecord record = records[index];
            // 0..15 are NTFS metadata files such as $MFT and $Bitmap. They are
            // real allocated space, but only counted when the caller asked for
            // them, and never when the scan was narrowed to a folder: they have
            // no place inside one.
            if (!record.InUse || (record.RecordNumber < 16 && !(includeMetadata && !scoped)))
                continue;

            if (record.IsExtension)
                continue; // Its attributes are counted with the base record they belong to.

            if (record.IsDirectory)
            {
                FolderNode? folder = scoped
                    ? (ResolveDirectory(records, directoryPaths!, record.RecordNumber, rootPath) is string path
                        ? FolderFor(root, rootPath, path)
                        : null)
                    : ResolveFolder(records, folderNodes!, record.RecordNumber, root);

                if (folder is not null)
                {
                    folders++;
                    if (record.HasName)
                        folder.SetOwnModified(record.Name.Modified);
                    if (record.HasReparse)
                        folder.MarkReparse(record.Reparse);
                }
            }
            else if (record.LinkCount > 0)
            {
                // Each hard link is a real name in a real directory, so each one
                // is emitted. Only the first name that is actually kept carries
                // the bytes: every later name is the same clusters again.
                RecordSizes size = RecordResolver.SizeOf(record, records);
                bool counted = false;
                int linkCount = record.LinkCount;
                for (int i = 0; i < linkCount; i++)
                {
                    FileNameRecord link = record.GetLink(i);
                    FolderNode? folder;
                    if (scoped)
                    {
                        if (ResolveDirectory(records, directoryPaths!, link.ParentRecordNumber, rootPath) is not string directory)
                            continue;

                        string fullPath = directory.EndsWith(Path.DirectorySeparatorChar) ? directory + link.Name : directory + Path.DirectorySeparatorChar + link.Name;
                        if (!InScope(fullPath, scopes))
                            continue;
                        folder = FolderFor(root, rootPath, directory);
                    }
                    else
                    {
                        folder = ResolveFolder(records, folderNodes!, link.ParentRecordNumber, root);
                    }

                    if (folder is null)
                        continue;

                    FileEntry file = ToFile(folder, record, link, size, counts: !counted, extLookup);
                    if (!filter.Accepts(file))
                        continue;

                    counted = true;
                    files.Add(file);
                    folder.AddFile(file);
                }
            }

            if ((index & 0x3FFF) == 0)
                report?.Invoke(files.Count, folders);
        }

        files.TrimExcess();
        root.Seal();
        report?.Invoke(files.Count, folders);
        return (root, files);
    }

    private static FileEntry ToFile(
        FolderNode folder,
        MftFileRecord record,
        FileNameRecord link,
        RecordSizes size,
        bool counts,
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extLookup)
    {
        long logical = size.Found ? size.Logical : link.Size;
        long allocated = size.Found ? size.Allocated : link.AllocatedSize;
        return new FileEntry(folder, link.Name, InternExtension(link.Name, extLookup))
        {
            Size = logical,
            AllocatedSize = allocated,
            CountsTowardTotal = counts,
            Created = link.Created,
            Modified = link.Modified,
            Accessed = link.Accessed,
            Attributes = link.Attributes,
            MftRecordNumber = record.RecordNumber,
            ParentRecordNumber = link.ParentRecordNumber,
            ShortName = record.ShortName,
            LinkCount = record.LinkCount,
            IsReparsePoint = record.HasReparse,
            ReparseTarget = record.HasReparse ? record.Reparse.Target : null,
        };
    }

    private static string InternExtension(string name, Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extLookup)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0)
            return "";

        ReadOnlySpan<char> ext = name.AsSpan(dot);
        if (extLookup.TryGetValue(ext, out string? existing))
            return existing;

        string created = ext.ToString();
        extLookup.Dictionary[created] = created;
        return created;
    }

    private static FolderNode? ResolveFolder(IReadOnlyList<MftFileRecord> records, FolderNode?[] cache, ulong recordNumber, FolderNode root)
    {
        if (recordNumber == NtfsRootRecord)
            return root;
        if (recordNumber >= (ulong)cache.Length)
            return null;
        if (cache[recordNumber] is FolderNode cached)
            return ReferenceEquals(cached, ResolvingFolder) || ReferenceEquals(cached, UnresolvedFolder) ? null : cached;

        cache[recordNumber] = ResolvingFolder;

        if (recordNumber >= (ulong)records.Count)
            return FailFolder(cache, recordNumber);

        MftFileRecord record = records[(int)recordNumber];
        if (!record.InUse || record.IsExtension || !record.HasName)
            return FailFolder(cache, recordNumber);

        FolderNode? parent = ResolveFolder(records, cache, record.Name.ParentRecordNumber, root);
        if (parent is null)
            return FailFolder(cache, recordNumber);

        FolderNode node = parent.GetOrAddChild(record.Name.Name);
        cache[recordNumber] = node;
        return node;
    }

    private static FolderNode? FailFolder(FolderNode?[] cache, ulong recordNumber)
    {
        cache[recordNumber] = UnresolvedFolder;
        return null;
    }

    /// <summary>
    /// Walks parent links until it reaches the volume root, then joins the
    /// names on the way back down. The cache makes this effectively linear:
    /// each directory is resolved only once, however many children it has.
    /// </summary>
    private static string? ResolveDirectory(IReadOnlyList<MftFileRecord> records, string?[] cache, ulong recordNumber, string volumeRoot)
    {
        if (recordNumber == NtfsRootRecord)
            return volumeRoot;
        if (recordNumber >= (ulong)cache.Length)
            return null;
        if (cache[recordNumber] is string cached)
            return cached is Resolving or Unresolved ? null : cached;

        // A marker that is not a path. A cycle, or a record with no name, used to
        // leave "" behind, and the next lookup treated that as a resolved directory.
        cache[recordNumber] = Resolving;

        if (recordNumber >= (ulong)records.Count)
            return Fail(cache, recordNumber);

        MftFileRecord record = records[(int)recordNumber];
        if (!record.InUse || record.IsExtension || !record.HasName)
            return Fail(cache, recordNumber);

        string? parent = ResolveDirectory(records, cache, record.Name.ParentRecordNumber, volumeRoot);
        if (parent is null)
            return Fail(cache, recordNumber);

        string path = parent.EndsWith(Path.DirectorySeparatorChar) ? parent + record.Name.Name : parent + Path.DirectorySeparatorChar + record.Name.Name;
        cache[recordNumber] = path;
        return path;
    }

    private static string? Fail(string?[] cache, ulong recordNumber)
    {
        cache[recordNumber] = Unresolved;
        return null;
    }

    /// <summary>
    /// The folder at <paramref name="directory"/>, created on the way down.
    /// Null when the path is not under this volume: slicing it would throw, and
    /// the file is left out rather than failing the whole scan.
    /// </summary>
    private static FolderNode? FolderFor(FolderNode root, string rootPath, string directory)
    {
        if (directory.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            return root;
        if (!directory.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            return null;

        string relative = directory[rootPath.Length..];
        FolderNode node = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            node = node.GetOrAddChild(segment);
        return node;
    }

    private static bool InScope(string fullPath, IReadOnlyList<string> scopes)
    {
        if (scopes.Count == 0)
            return true;

        foreach (string scope in scopes)
        {
            if (fullPath.StartsWith(scope.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeOne(string path) => ScanTargets.NormalizeOne(path);

    private static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..];
    }

    private static string NormalizeRoot(string path)
    {
        string full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    private static string TrimSeparator(string path)
        => path.Length > 3 && path.EndsWith(Path.DirectorySeparatorChar) ? path.TrimEnd(Path.DirectorySeparatorChar) : path;

    private static string RootName(string rootPath)
        => rootPath.TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } trimmed ? trimmed : rootPath;
}
