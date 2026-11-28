using System.Diagnostics.CodeAnalysis;

namespace DiskGo.Core;

/// <summary>
/// One directory in the result tree. Sizes roll up from descendants, so a node
/// always describes the whole subtree under it and not only its own files.
/// </summary>
public sealed class FolderNode
{
    private readonly IEqualityComparer<string> _names;
    private Dictionary<string, FolderNode>? _children;

    public FolderNode(string name, string fullPath, IEqualityComparer<string>? names = null)
    {
        _names = names ?? StringComparer.OrdinalIgnoreCase;
        Name = name;
        FullPath = fullPath;
    }

    public string Name { get; private set; }

    /// <summary>Full path. The volume root keeps its trailing separator; every other node does not.</summary>
    public string FullPath { get; private set; }

    public FolderNode? Parent { get; private set; }

    /// <summary>Logical size of every file at any depth below this folder.</summary>
    public long Size { get; private set; }

    /// <summary>Allocated size of every file at any depth below this folder.</summary>
    public long AllocatedSize { get; private set; }

    /// <summary>Logical size of the files directly inside this folder, excluding subfolders.</summary>
    public long OwnSize { get; private set; }

    /// <summary>Allocated size of the files directly inside this folder, excluding subfolders.</summary>
    public long OwnAllocatedSize { get; private set; }

    /// <summary>
    /// The directory's own last-write time, as the file system recorded it.
    /// Null until a scan supplies one. Distinct from <see cref="Modified"/>, which
    /// may instead be the newest time anywhere inside the folder.
    /// </summary>
    public DateTime? OwnModified { get; private set; }

    /// <summary>Files at any depth below this folder.</summary>
    public long FileCount { get; private set; }

    /// <summary>Directories at any depth below this folder, not counting itself.</summary>
    public long FolderCount { get; private set; }

    /// <summary>Newest last-write time found anywhere in the subtree, or null when it is empty.</summary>
    public DateTime? Modified { get; private set; }

    /// <summary>Files that sit directly in this folder, in the order they were discovered.</summary>
    public List<FileEntry> Files { get; } = [];

    /// <summary>
    /// Set when this node stands for every file in its parent that was smaller
    /// than the grouping threshold. It is not a directory on disk: its files are
    /// the small ones, and it has no children of its own.
    /// </summary>
    public bool IsFileGroup { get; private set; }

    public IReadOnlyCollection<FolderNode> Children => (IReadOnlyCollection<FolderNode>?)_children?.Values ?? [];

    /// <summary>Share of the parent folder's logical size, from 0 to 100. The root reports 100.</summary>
    public double PercentOfParent => Parent is null || Parent.Size == 0 ? 100d : 100d * Size / Parent.Size;

    /// <summary>True when this folder is a junction or a symbolic link. Its children are the link itself, not the directory it points at.</summary>
    public bool IsReparsePoint { get; private set; }

    /// <summary>The directory a junction or symbolic link points at, when it could be read.</summary>
    public string? ReparseTarget { get; private set; }

    internal void MarkReparse(ReparseInfo info)
    {
        IsReparsePoint = true;
        ReparseTarget = info.Target;
    }

    /// <summary>The folder at <paramref name="path"/>, when it sits inside this one. The comparison ignores case.</summary>
    public FolderNode? Find(string path)
    {
        string here = FullPath.TrimEnd(Path.DirectorySeparatorChar);
        string wanted = path.TrimEnd(Path.DirectorySeparatorChar);
        if (wanted.Equals(here, StringComparison.OrdinalIgnoreCase))
            return this;

        string prefix = here + Path.DirectorySeparatorChar;
        if (!wanted.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        FolderNode node = this;
        ReadOnlySpan<char> rest = wanted.AsSpan(prefix.Length);
        while (!rest.IsEmpty)
        {
            int sep = rest.IndexOf(Path.DirectorySeparatorChar);
            ReadOnlySpan<char> segment = sep < 0 ? rest : rest[..sep];
            if (!node.TryGetChild(segment, out FolderNode? child))
                return null;
            node = child;
            if (sep < 0)
                break;
            rest = rest[(sep + 1)..];
        }

        return node;
    }

    internal FolderNode? FindFromRelative(ReadOnlySpan<char> relative)
    {
        FolderNode node = this;
        while (!relative.IsEmpty)
        {
            int sep = relative.IndexOf(Path.DirectorySeparatorChar);
            ReadOnlySpan<char> segment = sep < 0 ? relative : relative[..sep];
            if (!segment.IsEmpty)
            {
                if (!node.TryGetChild(segment, out FolderNode? child))
                    return null;
                node = child;
            }
            if (sep < 0)
                break;
            relative = relative[(sep + 1)..];
        }

        return node;
    }

    /// <summary>Returns the child directory with this name, creating it on first use.</summary>
    public FolderNode GetOrAddChild(string name)
    {
        _children ??= new Dictionary<string, FolderNode>(_names);
        if (_children.TryGetValue(name, out FolderNode? existing))
            return existing;

        string path = FullPath.EndsWith(Path.DirectorySeparatorChar)
            ? FullPath + name
            : FullPath + Path.DirectorySeparatorChar + name;
        var created = new FolderNode(name, path, _names) { Parent = this };
        _children.Add(name, created);
        return created;
    }

    internal FolderNode GetOrAddChild(ReadOnlySpan<char> name)
    {
        _children ??= new Dictionary<string, FolderNode>(_names);
        if (TryGetChild(name, out FolderNode? existing))
            return existing;

        string nameStr = name.ToString();
        string path = FullPath.EndsWith(Path.DirectorySeparatorChar)
            ? FullPath + nameStr
            : FullPath + Path.DirectorySeparatorChar + nameStr;
        var created = new FolderNode(nameStr, path, _names) { Parent = this };
        _children.Add(nameStr, created);
        return created;
    }

    internal void AddFile(FileEntry file)
    {
        Files.Add(file);
        // A later hard link is listed, but its bytes already belong to the first
        // name of the same record. Adding them again would make the folder larger
        // than the space it occupies.
        if (!file.CountsTowardTotal)
            return;

        OwnSize += file.Size;
        OwnAllocatedSize += file.AllocatedSize;
    }

    /// <summary>
    /// Drops a file that was deleted or renamed away. Only the folder's own
    /// totals change here; call <see cref="Seal"/> on the root afterwards so
    /// the ancestors pick the change up.
    /// </summary>
    internal bool RemoveFile(FileEntry file)
    {
        if (!Files.Remove(file))
            return false;

        if (file.CountsTowardTotal)
        {
            OwnSize -= file.Size;
            OwnAllocatedSize -= file.AllocatedSize;
        }

        return true;
    }

    /// <summary>Records the last-write time the file system keeps for this directory itself.</summary>
    internal void SetOwnModified(DateTime modified) => OwnModified = modified;

    /// <summary>Drops a child folder, for example after it was deleted. Returns false when it was not a child.</summary>
    internal bool RemoveChild(FolderNode child) => _children is not null && _children.Remove(child.Name);

    /// <summary>Puts a folder back that was removed, without creating a second one.</summary>
    internal void Reattach(FolderNode child)
    {
        child.Parent = this;
        (_children ??= new Dictionary<string, FolderNode>(_names))[child.Name] = child;
    }

    /// <summary>
    /// Swaps the child called <paramref name="name"/> for <paramref name="replacement"/>,
    /// keeping the new one's own contents. The replacement keeps the name it is
    /// known by here: a scan of one folder names its root after the whole path,
    /// which is not the name the parent stored it under.
    /// </summary>
    internal void ReplaceChild(string name, FolderNode replacement)
    {
        if (_children is null || !_children.ContainsKey(name))
            throw new ArgumentException($"{replacement.FullPath} is not inside {FullPath}.");

        replacement.Parent = this;
        replacement.Name = name;
        _children[name] = replacement;
    }

    /// <summary>
    /// Points this folder, and everything inside it, at <paramref name="path"/>.
    /// Used after a rename, where the disk has already moved and the tree has
    /// to follow. The folder keeps its place under its parent.
    /// </summary>
    internal void Retarget(string path)
    {
        string previous = FullPath;
        Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        FullPath = path;

        if (_children is not null)
        {
            foreach ((string childName, FolderNode child) in _children)
                child.Retarget(path + Path.DirectorySeparatorChar + childName);
        }

        _ = previous;
    }

    /// <summary>
    /// Fills the rolled-up sizes and <see cref="FolderCount"/> from the files
    /// and child folders already in place. Done once at the end, so a scan can
    /// fill sibling folders from several threads and only then combine them.
    /// </summary>
    internal void Seal()
    {
        long size = OwnSize;
        long allocated = OwnAllocatedSize;
        long count = Files.Count;
        long below = 0;
        DateTime? newest = null;
        foreach (FileEntry file in Files)
        {
            if (newest is null || file.Modified > newest)
                newest = file.Modified;
        }

        foreach (FileEntry file in Files)
            file.DirectoryNode = this;

        Files.TrimExcess();

        if (_children is not null)
        {
            foreach (FolderNode child in _children.Values)
            {
                child.Seal();
                size += child.Size;
                allocated += child.AllocatedSize;
                count += child.FileCount;
                below += 1 + child.FolderCount;
                if (child.Modified is DateTime modified && (newest is null || modified > newest))
                    newest = modified;
            }

            _children.TrimExcess();
        }

        Size = size;
        AllocatedSize = allocated;
        FileCount = count;
        FolderCount = below;
        NewestModified = newest;
        Modified = _showNewestModified ? newest : OwnModified;
    }

    /// <summary>The newest last-write time anywhere inside this folder, kept even when <see cref="Modified"/> shows the folder's own time.</summary>
    private DateTime? NewestModified { get; set; }

    private bool _showNewestModified;

    /// <summary>
    /// Chooses what <see cref="Modified"/> reports, for this folder and every
    /// folder below it. On uses the newest time of anything inside; off uses
    /// the time the file system recorded for the directory itself.
    /// </summary>
    public void UseNewestModified(bool enabled)
    {
        _showNewestModified = enabled;
        Modified = enabled ? NewestModified : OwnModified;
        if (_children is not null)
        {
            foreach (FolderNode child in _children.Values)
                child.UseNewestModified(enabled);
        }
    }

    /// <summary>
    /// Folds the files directly in this folder that are smaller than
    /// <paramref name="minimumBytes"/> into one child, and does the same in every
    /// folder below. A threshold of zero leaves the tree as it was. The files
    /// stay in the scan; only the shape of the tree changes.
    /// </summary>
    public void GroupSmallFiles(long minimumBytes)
    {
        if (minimumBytes <= 0 || IsFileGroup)
            return;

        // Group the folders below first, while this folder's own child list is
        // still exactly the directories on disk. The group node is added after.
        if (_children is not null)
        {
            foreach (FolderNode child in _children.Values.ToArray())
                child.GroupSmallFiles(minimumBytes);
        }

        var small = new List<FileEntry>();
        foreach (FileEntry file in Files)
        {
            if (file.Size < minimumBytes)
                small.Add(file);
        }

        if (small.Count > 0)
        {
            var group = new FolderNode($"<{minimumBytes:N0} bytes ({small.Count:N0})", FullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "<small-files>")
            {
                Parent = this,
                IsFileGroup = true,
            };
            foreach (FileEntry file in small)
            {
                Files.Remove(file);
                if (file.CountsTowardTotal)
                {
                    OwnSize -= file.Size;
                    OwnAllocatedSize -= file.AllocatedSize;
                }

                group.AddFile(file);
            }

            (_children ??= new Dictionary<string, FolderNode>(_names))[group.Name] = group;
        }
    }

    /// <summary>Sorts files and child folders so the largest consumers come first.</summary>
    public void SortBySizeDescending()
    {
        Files.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        if (_children is not null)
        {
            foreach (FolderNode child in _children.Values)
                child.SortBySizeDescending();
        }
    }

    /// <summary>
    /// Looks a child up by a path segment without copying it. The volume's
    /// upcase table supports that directly; a comparer that does not falls
    /// back to allocating the segment, which is what used to throw.
    /// </summary>
    private bool TryGetChild(ReadOnlySpan<char> name, [NotNullWhen(true)] out FolderNode? child)
    {
        child = null;
        if (_children is null)
            return false;

        if (_children.TryGetAlternateLookup(out Dictionary<string, FolderNode>.AlternateLookup<ReadOnlySpan<char>> lookup))
            return lookup.TryGetValue(name, out child);

        return _children.TryGetValue(name.ToString(), out child);
    }

    /// <summary>Child folders ordered by logical size, largest first.</summary>
    public IEnumerable<FolderNode> ChildrenBySize()
        => _children is null ? [] : _children.Values.OrderByDescending(static child => child.Size);
}
