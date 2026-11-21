using System.Collections.Concurrent;
using DiskGo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DiskGo;

public enum TreeSortColumn
{
    Name,
    Percent,
    Size,
    Allocated,
    Files,
    Folders,
    Modified,
}

public sealed class TreeRow
{
    public TreeRow(FolderNode folder, int depth, bool expanded, AppSettings settings, bool dark)
    {
        Folder = folder;
        ParentSizeBytes = folder.Parent?.Size ?? folder.Size;
        Depth = depth;
        Expanded = expanded;
        Name = folder.Parent is null ? VolumeLabel(folder) : folder.Name;
        SizeBytes = folder.Size;
        AllocatedBytes = folder.AllocatedSize;
        FileCountValue = folder.FileCount;
        FolderCountValue = folder.FolderCount;
        ModifiedValue = folder.Modified ?? DateTime.MinValue;

        PercentValue = folder.PercentOfParent;
        Percent = SizeText.Percent(folder.PercentOfParent);
        Size = SizeText.Format(folder.Size, settings.SizeUnit, settings.BinaryUnits, settings.DecimalPlaces);
        Allocated = SizeText.Format(folder.AllocatedSize, settings.SizeUnit, settings.BinaryUnits, settings.DecimalPlaces);
        Files = folder.FileCount.ToString("N0");
        Folders = folder.FolderCount.ToString("N0");
        Modified = SizeText.When(folder.Modified);

        AllocatedVis = settings.TreeShowAllocated ? Visibility.Visible : Visibility.Collapsed;
        FilesVis = settings.TreeShowFiles ? Visibility.Visible : Visibility.Collapsed;
        FoldersVis = settings.TreeShowFolders ? Visibility.Visible : Visibility.Collapsed;
        ModifiedVis = settings.TreeShowModified ? Visibility.Visible : Visibility.Collapsed;

        AllocatedColWidth = settings.TreeShowAllocated ? new GridLength(90) : new GridLength(0);
        FilesColWidth = settings.TreeShowFiles ? new GridLength(74) : new GridLength(0);
        FoldersColWidth = settings.TreeShowFolders ? new GridLength(68) : new GridLength(0);
        ModifiedColWidth = settings.TreeShowModified ? new GridLength(130) : new GridLength(0);

        ExpanderVis = (folder.Children.Count > 0 || folder.Files.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        FolderGlyph = folder.IsReparsePoint ? "\uE71B" : folder.Parent is null ? "\uEDA2" : "\uE8B7";
        IconBrush = (Brush)Application.Current.Resources[
            folder.IsReparsePoint ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush"];
        Tooltip = folder.IsReparsePoint && !string.IsNullOrEmpty(folder.ReparseTarget)
            ? $"{folder.FullPath}\n链接目标: {folder.ReparseTarget}"
            : folder.FullPath;

        _ = dark;
    }

    public TreeRow(FileEntry file, long parentSize, int depth, AppSettings settings, bool dark)
    {
        File = file;
        ParentSizeBytes = parentSize;
        Depth = depth;
        Name = file.Name;
        SizeBytes = file.Size;
        AllocatedBytes = file.CountsTowardTotal ? file.AllocatedSize : 0;
        ModifiedValue = file.Modified;

        PercentValue = parentSize == 0 ? 0d : 100d * file.Size / parentSize;
        Percent = SizeText.Percent(PercentValue);
        Size = SizeText.Format(file.Size, settings.SizeUnit, settings.BinaryUnits, settings.DecimalPlaces);
        string allocFmt = SizeText.Format(file.AllocatedSize, settings.SizeUnit, settings.BinaryUnits, settings.DecimalPlaces);
        Allocated = file.CountsTowardTotal ? allocFmt : "0 " + allocFmt;
        Files = "";
        Folders = "";
        Modified = SizeText.When(file.Modified);

        AllocatedVis = settings.TreeShowAllocated ? Visibility.Visible : Visibility.Collapsed;
        FilesVis = settings.TreeShowFiles ? Visibility.Visible : Visibility.Collapsed;
        FoldersVis = settings.TreeShowFolders ? Visibility.Visible : Visibility.Collapsed;
        ModifiedVis = settings.TreeShowModified ? Visibility.Visible : Visibility.Collapsed;

        AllocatedColWidth = settings.TreeShowAllocated ? new GridLength(90) : new GridLength(0);
        FilesColWidth = settings.TreeShowFiles ? new GridLength(74) : new GridLength(0);
        FoldersColWidth = settings.TreeShowFolders ? new GridLength(68) : new GridLength(0);
        ModifiedColWidth = settings.TreeShowModified ? new GridLength(130) : new GridLength(0);

        ExpanderVis = Visibility.Collapsed;
        FolderGlyph = file.IsReparsePoint ? "\uE71B" : "\uE8A5";
        IconBrush = TypeColors.BrushFor(file.Extension, dark);
        Tooltip = file.IsReparsePoint && !string.IsNullOrEmpty(file.ReparseTarget)
            ? $"{file.FullPath}\n链接目标: {file.ReparseTarget}"
            : file.FullPath;
    }

    public FolderNode? Folder { get; }
    public FileEntry? File { get; }
    public bool IsFolder => Folder is not null;
    public long ParentSizeBytes { get; }
    public long SizeBytes { get; }
    public long AllocatedBytes { get; }
    public long FileCountValue { get; }
    public long FolderCountValue { get; }
    public DateTime ModifiedValue { get; }

    public int Depth { get; }
    public bool Expanded { get; }
    public string Name { get; }
    public double PercentValue { get; }
    public string Percent { get; }
    public string Size { get; }
    public string Allocated { get; }
    public string Files { get; }
    public string Folders { get; }
    public string Modified { get; }

    public Visibility AllocatedVis { get; }
    public Visibility FilesVis { get; }
    public Visibility FoldersVis { get; }
    public Visibility ModifiedVis { get; }

    public GridLength AllocatedColWidth { get; }
    public GridLength FilesColWidth { get; }
    public GridLength FoldersColWidth { get; }
    public GridLength ModifiedColWidth { get; }

    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public Visibility ExpanderVis { get; }
    public string ChevronGlyph => Expanded ? "\uE70D" : "\uE76C";
    public string FolderGlyph { get; }
    public Brush IconBrush { get; }
    public string Tooltip { get; }

    private static readonly ConcurrentDictionary<string, string> VolumeLabelCache = new(StringComparer.OrdinalIgnoreCase);

    public static string VolumeLabel(FolderNode folder)
    {
        string path = folder.FullPath.TrimEnd(Path.DirectorySeparatorChar);
        if (path.Length == 2 && path[1] == ':')
        {
            return VolumeLabelCache.GetOrAdd(path, static drivePath =>
            {
                try
                {
                    string label = new DriveInfo(drivePath).VolumeLabel;
                    return string.IsNullOrWhiteSpace(label) ? $"本地磁盘 ({drivePath})" : $"{label} ({drivePath})";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return drivePath;
                }
            });
        }

        return path.Length == 0 ? folder.Name : path;
    }
}

public sealed record TypeRow(
    string Key,
    string DisplayName,
    string Size,
    string Percent,
    string Count,
    Brush Swatch);
