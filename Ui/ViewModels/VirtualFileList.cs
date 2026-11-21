using System.Collections;
using System.Collections.Specialized;
using DiskGo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DiskGo;

public enum FileSortColumn
{
    Name,
    Size,
    Allocated,
    Percent,
    Directory,
    Modified,
    Attributes,
    DuplicateCount,
    DuplicateSize,
}

public sealed class FileColumnState
{
    public bool ShowAllocated { get; set; } = true;
    public bool ShowPercent { get; set; } = true;
    public bool ShowPath { get; set; } = true;
    public bool ShowModified { get; set; } = true;
    public bool ShowAttributes { get; set; } = true;
    public bool ShowDuplicates { get; set; }

    public SizeUnit SizeUnit { get; set; } = SizeUnit.Automatic;
    public bool BinaryUnits { get; set; }
    public int DecimalPlaces { get; set; } = 1;
    public long TotalRootSize { get; set; }
    public bool Dark { get; set; }
}

public sealed class FileRow : IEquatable<FileRow>
{
    private readonly FileColumnState _state;

    public FileRow(FileEntry file, int index, FileColumnState state)
    {
        File = file;
        Index = index;
        _state = state;
    }

    public FileEntry File { get; }
    public int Index { get; }

    public string Name => File.Name;
    public string Glyph => File.IsReparsePoint ? "\uE71B" : "\uE8A5";
    public string Tooltip => File.IsReparsePoint && !string.IsNullOrEmpty(File.ReparseTarget)
        ? $"{File.FullPath}\n链接目标: {File.ReparseTarget}"
        : File.FullPath;

    public Brush TypeSwatch => TypeColors.BrushFor(File.Extension, _state.Dark);

    public string Size => SizeText.Format(File.Size, _state.SizeUnit, _state.BinaryUnits, _state.DecimalPlaces);

    public string Allocated
    {
        get
        {
            string formatted = SizeText.Format(File.AllocatedSize, _state.SizeUnit, _state.BinaryUnits, _state.DecimalPlaces);
            return File.CountsTowardTotal ? formatted : "0 " + formatted;
        }
    }

    public string Percent
    {
        get
        {
            double share = _state.TotalRootSize == 0 ? 0 : 100d * File.Size / _state.TotalRootSize;
            return SizeText.Percent(share);
        }
    }

    public string Directory => File.DirectoryPath;
    public string Modified => SizeText.When(File.Modified);
    public string Attributes => SizeText.Attributes(File.Attributes);
    public string DuplicateCount => File.DuplicateCount > 1 ? File.DuplicateCount.ToString("N0") : "";
    public string DuplicateSize => File.DuplicateCount > 1 ? SizeText.Format(File.DuplicateSize, _state.SizeUnit, _state.BinaryUnits, _state.DecimalPlaces) : "";

    public Visibility AllocatedVis => _state.ShowAllocated ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PercentVis => _state.ShowPercent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PathVis => _state.ShowPath ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ModifiedVis => _state.ShowModified ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AttributesVis => _state.ShowAttributes ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DuplicatesVis => _state.ShowDuplicates ? Visibility.Visible : Visibility.Collapsed;

    public GridLength AllocatedColWidth => _state.ShowAllocated ? new GridLength(90) : new GridLength(0);
    public GridLength PercentColWidth => _state.ShowPercent ? new GridLength(64) : new GridLength(0);
    public GridLength PathColWidth => _state.ShowPath ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
    public GridLength ModifiedColWidth => _state.ShowModified ? new GridLength(130) : new GridLength(0);
    public GridLength AttributesColWidth => _state.ShowAttributes ? new GridLength(54) : new GridLength(0);
    public GridLength DupCntColWidth => _state.ShowDuplicates ? new GridLength(64) : new GridLength(0);
    public GridLength DupSizeColWidth => _state.ShowDuplicates ? new GridLength(88) : new GridLength(0);

    public bool Equals(FileRow? other) => other is not null && ReferenceEquals(File, other.File);
    public override bool Equals(object? obj) => obj is FileRow other && Equals(other);
    public override int GetHashCode() => File.GetHashCode();
}

/// <summary>
/// Virtualizing IList source for WinUI 3 ListView so folders with hundreds of
/// thousands of files never allocate row view models up front.
/// </summary>
public sealed class VirtualFileList : IList, INotifyCollectionChanged
{
    private List<FileEntry> _entries = [];
    private readonly FileColumnState _state;

    public VirtualFileList(FileColumnState state)
    {
        _state = state;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public FileEntry GetEntry(int index) => _entries[index];

    public int Count => _entries.Count;
    public bool IsFixedSize => false;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public object? this[int index]
    {
        get => new FileRow(_entries[index], index, _state);
        set => throw new NotSupportedException();
    }

    public void SetEntries(List<FileEntry> entries)
    {
        _entries = entries;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Refresh()
    {
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public int FindNextPrefix(string prefix, int startIndex)
    {
        if (_entries.Count == 0 || string.IsNullOrEmpty(prefix))
            return -1;

        for (int offset = 1; offset <= _entries.Count; offset++)
        {
            int idx = (startIndex + offset) % _entries.Count;
            if (_entries[idx].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return idx;
        }

        return -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value)
    {
        if (value is not FileRow row)
            return -1;
        if (row.Index >= 0 && row.Index < _entries.Count && ReferenceEquals(_entries[row.Index], row.File))
            return row.Index;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (ReferenceEquals(_entries[i], row.File))
                return i;
        }
        return -1;
    }

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < _entries.Count; i++)
            yield return new FileRow(_entries[i], i, _state);
    }

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < _entries.Count; i++)
            array.SetValue(new FileRow(_entries[i], i, _state), index + i);
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
