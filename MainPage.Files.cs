using DiskGo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskGo;

public sealed partial class MainPage
{
    private void ShowFiles()
    {
        CancellationToken token = ResetCts(ref _filesCts);
        if (_result is null)
        {
            _files.SetEntries([]);
            EmptyFilesHint.Visibility = Visibility.Collapsed;
            return;
        }

        ScanResult result = _result;
        FolderNode? selected = _selected;
        string query = SearchBox.Text.Trim();
        string? typeFilter = _typeFilter;
        bool duplicatesOnly = DuplicatesOnlyBox.IsChecked == true;
        FileSortColumn fileSort = _fileSort;
        bool fileSortDesc = _fileSortDesc;

        if (query.Length == 0 && (selected?.Files.Count ?? 0) <= 5_000)
        {
            List<FileEntry> list = FilterAndSortFiles(result, selected, query, typeFilter, duplicatesOnly, fileSort, fileSortDesc, token);
            ApplyFileListResult(list, query, typeFilter);
            return;
        }

        _ = ShowFilesAsync(result, selected, query, typeFilter, duplicatesOnly, fileSort, fileSortDesc, token);
    }

    private async Task ShowFilesAsync(
        ScanResult result,
        FolderNode? selected,
        string query,
        string? typeFilter,
        bool duplicatesOnly,
        FileSortColumn fileSort,
        bool fileSortDesc,
        CancellationToken token)
    {
        try
        {
            List<FileEntry> list = await Task.Run(
                () => FilterAndSortFiles(result, selected, query, typeFilter, duplicatesOnly, fileSort, fileSortDesc, token),
                token);

            if (token.IsCancellationRequested || !ReferenceEquals(_result, result))
                return;

            ApplyFileListResult(list, query, typeFilter);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyFileListResult(List<FileEntry> list, string query, string? typeFilter)
    {
        _files.SetEntries(list);

        if (query.Length > 0)
            FileSummaryText.Text = $"全盘搜索 · {list.Count:N0} 个匹配";
        else if (typeFilter is not null)
            FileSummaryText.Text = $"已筛选 · {list.Count:N0} 个文件";
        else
            FileSummaryText.Text = $"当前文件夹 · {list.Count:N0} 个文件";

        if (list.Count == 0)
        {
            EmptyFilesHint.Text = query.Length > 0 ? "没有匹配的文件" : "当前文件夹没有直接文件";
            EmptyFilesHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyFilesHint.Visibility = Visibility.Collapsed;
        }

        UpdateSelectionSummary();
    }

    private static List<FileEntry> FilterAndSortFiles(
        ScanResult result,
        FolderNode? selected,
        string query,
        string? typeFilter,
        bool duplicatesOnly,
        FileSortColumn fileSort,
        bool fileSortDesc,
        CancellationToken token)
    {
        IReadOnlyList<FileEntry> raw = query.Length == 0
            ? (IReadOnlyList<FileEntry>?)selected?.Files ?? Array.Empty<FileEntry>()
            : result.Files;

        var filtered = new List<FileEntry>(query.Length == 0 && typeFilter is null && !duplicatesOnly ? raw.Count : Math.Min(raw.Count, 1024));
        bool hasQuery = query.Length > 0;
        bool hasType = typeFilter is not null;
        for (int i = 0; i < raw.Count; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();

            FileEntry file = raw[i];
            if (hasQuery && !file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            if (hasType && !string.Equals(ExtensionOf(file), typeFilter, StringComparison.Ordinal))
                continue;
            if (duplicatesOnly && file.DuplicateCount <= 1)
                continue;

            filtered.Add(file);
        }

        token.ThrowIfCancellationRequested();
        SortFileList(filtered, duplicatesOnly, fileSort, fileSortDesc);
        return filtered;
    }

    private static void SortFileList(List<FileEntry> list, bool duplicatesOnly, FileSortColumn fileSort, bool fileSortDesc)
    {
        if (list.Count <= 1)
            return;

        if (duplicatesOnly && fileSort == FileSortColumn.Size && fileSortDesc)
        {
            list.Sort(static (a, b) =>
            {
                int c = b.DuplicateSize.CompareTo(a.DuplicateSize);
                if (c != 0)
                    return c;
                c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
            });
            return;
        }

        Comparison<FileEntry> cmp = (fileSort, fileSortDesc) switch
        {
            (FileSortColumn.Name, false) => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            (FileSortColumn.Name, true) => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(b.Name, a.Name),
            (FileSortColumn.Allocated, false) => static (a, b) => (a.CountsTowardTotal ? a.AllocatedSize : 0).CompareTo(b.CountsTowardTotal ? b.AllocatedSize : 0),
            (FileSortColumn.Allocated, true) => static (a, b) => (b.CountsTowardTotal ? b.AllocatedSize : 0).CompareTo(a.CountsTowardTotal ? a.AllocatedSize : 0),
            (FileSortColumn.Percent, false) => static (a, b) => a.Size.CompareTo(b.Size),
            (FileSortColumn.Percent, true) => static (a, b) => b.Size.CompareTo(a.Size),
            (FileSortColumn.Directory, false) => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DirectoryPath, b.DirectoryPath),
            (FileSortColumn.Directory, true) => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(b.DirectoryPath, a.DirectoryPath),
            (FileSortColumn.Modified, false) => static (a, b) => a.Modified.CompareTo(b.Modified),
            (FileSortColumn.Modified, true) => static (a, b) => b.Modified.CompareTo(a.Modified),
            (FileSortColumn.Attributes, false) => static (a, b) => StringComparer.Ordinal.Compare(SizeText.Attributes(a.Attributes), SizeText.Attributes(b.Attributes)),
            (FileSortColumn.Attributes, true) => static (a, b) => StringComparer.Ordinal.Compare(SizeText.Attributes(b.Attributes), SizeText.Attributes(a.Attributes)),
            (FileSortColumn.DuplicateCount, false) => static (a, b) => a.DuplicateCount.CompareTo(b.DuplicateCount),
            (FileSortColumn.DuplicateCount, true) => static (a, b) => b.DuplicateCount.CompareTo(a.DuplicateCount),
            (FileSortColumn.DuplicateSize, false) => static (a, b) => a.DuplicateSize.CompareTo(b.DuplicateSize),
            (FileSortColumn.DuplicateSize, true) => static (a, b) => b.DuplicateSize.CompareTo(a.DuplicateSize),
            (FileSortColumn.Size, false) => static (a, b) => a.Size.CompareTo(b.Size),
            _ => static (a, b) => b.Size.CompareTo(a.Size),
        };
        list.Sort(cmp);
    }

    private static string ExtensionOf(FileEntry file)
        => file.Extension.Length == 0 ? "(none)" : file.Extension.ToLowerInvariant();

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void DuplicatesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_result is null)
            return;

        ApplyDuplicateMode();
    }

    private void DuplicatesOnlyClicked(object sender, RoutedEventArgs e)
    {
        if (DuplicatesOnlyBox.IsChecked == true && DuplicateBox.SelectedIndex == 0)
            DuplicateBox.SelectedIndex = 1;
        else
            ShowFiles();
    }

    private async void ApplyDuplicateMode()
    {
        CancellationToken token = ResetCts(ref _dupCts);
        if (_result is null)
            return;

        ScanResult result = _result;
        DuplicateMode mode = SelectedDuplicates();
        if (mode != DuplicateMode.None)
            StatusText.Text = "正在检测重复文件...";

        try
        {
            await Task.Run(() =>
            {
                if (mode != DuplicateMode.None)
                {
                    DuplicateFinder.Apply(result.Files, mode, token);
                }
                else
                {
                    foreach (FileEntry f in result.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        f.DuplicateCount = 0;
                        f.DuplicateSize = 0;
                    }
                }
            }, token);

            if (token.IsCancellationRequested || !ReferenceEquals(_result, result))
                return;

            _fileColumns.ShowDuplicates = mode != DuplicateMode.None;
            ApplyFileColumnWidths();
            ShowFiles();
            StatusText.Text = Summary(result);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private DuplicateMode SelectedDuplicates()
        => ((DuplicateBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "name" => DuplicateMode.NameAndSize,
            "date" => DuplicateMode.NameSizeAndDate,
            _ => DuplicateMode.None,
        };

    private void BuildColumnMenus()
    {
        void AddTreeToggle(string title, Func<bool> get, Action<bool> set)
        {
            void AddTo(MenuFlyout menu)
            {
                var item = new ToggleMenuFlyoutItem { Text = title, IsChecked = get() };
                item.Click += (_, _) =>
                {
                    set(item.IsChecked);
                    _settings.Save();
                    ApplyTreeColumnWidths();
                    RefreshTreeRows();
                    SyncColumnChecks();
                };
                menu.Items.Add(item);
            }
            AddTo(TreeColumnsMenu);
            AddTo(TreeHeaderMenu);
        }

        AddTreeToggle("占用大小 (Allocated)", () => _settings.TreeShowAllocated, v => _settings.TreeShowAllocated = v);
        AddTreeToggle("文件数", () => _settings.TreeShowFiles, v => _settings.TreeShowFiles = v);
        AddTreeToggle("文件夹数", () => _settings.TreeShowFolders, v => _settings.TreeShowFolders = v);
        AddTreeToggle("修改时间", () => _settings.TreeShowModified, v => _settings.TreeShowModified = v);

        void AddFileToggle(string title, Func<bool> get, Action<bool> set)
        {
            void AddTo(MenuFlyout menu)
            {
                var item = new ToggleMenuFlyoutItem { Text = title, IsChecked = get() };
                item.Click += (_, _) =>
                {
                    set(item.IsChecked);
                    _settings.Save();
                    _fileColumns.ShowAllocated = _settings.FileShowAllocated;
                    _fileColumns.ShowPercent = _settings.FileShowPercent;
                    _fileColumns.ShowPath = _settings.FileShowPath;
                    _fileColumns.ShowModified = _settings.FileShowModified;
                    _fileColumns.ShowAttributes = _settings.FileShowAttributes;
                    ApplyFileColumnWidths();
                    _files.Refresh();
                    SyncColumnChecks();
                };
                menu.Items.Add(item);
            }
            AddTo(FileColumnsMenu);
            AddTo(FileHeaderMenu);
        }

        AddFileToggle("占用大小 (Allocated)", () => _settings.FileShowAllocated, v => _settings.FileShowAllocated = v);
        AddFileToggle("占全盘 (%)", () => _settings.FileShowPercent, v => _settings.FileShowPercent = v);
        AddFileToggle("路径", () => _settings.FileShowPath, v => _settings.FileShowPath = v);
        AddFileToggle("修改时间", () => _settings.FileShowModified, v => _settings.FileShowModified = v);
        AddFileToggle("属性", () => _settings.FileShowAttributes, v => _settings.FileShowAttributes = v);
    }

    private void SyncColumnChecks()
    {
        bool[] treeVals = [_settings.TreeShowAllocated, _settings.TreeShowFiles, _settings.TreeShowFolders, _settings.TreeShowModified];
        foreach (MenuFlyout menu in new[] { TreeColumnsMenu, TreeHeaderMenu })
        {
            for (int i = 0; i < treeVals.Length && i < menu.Items.Count; i++)
                if (menu.Items[i] is ToggleMenuFlyoutItem t) t.IsChecked = treeVals[i];
        }

        bool[] fileVals = [_settings.FileShowAllocated, _settings.FileShowPercent, _settings.FileShowPath, _settings.FileShowModified, _settings.FileShowAttributes];
        foreach (MenuFlyout menu in new[] { FileColumnsMenu, FileHeaderMenu })
        {
            for (int i = 0; i < fileVals.Length && i < menu.Items.Count; i++)
                if (menu.Items[i] is ToggleMenuFlyoutItem t) t.IsChecked = fileVals[i];
        }
    }

    private void ApplyTreeColumnWidths()
    {
        TreeHdrAllocCol.Width = _settings.TreeShowAllocated ? new GridLength(90) : new GridLength(0);
        TreeHdrFilesCol.Width = _settings.TreeShowFiles ? new GridLength(74) : new GridLength(0);
        TreeHdrFoldersCol.Width = _settings.TreeShowFolders ? new GridLength(68) : new GridLength(0);
        TreeHdrModCol.Width = _settings.TreeShowModified ? new GridLength(130) : new GridLength(0);

        TreeColAlloc.Visibility = _settings.TreeShowAllocated ? Visibility.Visible : Visibility.Collapsed;
        TreeColFiles.Visibility = _settings.TreeShowFiles ? Visibility.Visible : Visibility.Collapsed;
        TreeColFolders.Visibility = _settings.TreeShowFolders ? Visibility.Visible : Visibility.Collapsed;
        TreeColMod.Visibility = _settings.TreeShowModified ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFileColumnWidths()
    {
        FileHdrAllocCol.Width = _fileColumns.ShowAllocated ? new GridLength(90) : new GridLength(0);
        FileHdrPctCol.Width = _fileColumns.ShowPercent ? new GridLength(64) : new GridLength(0);
        FileHdrPathCol.Width = _fileColumns.ShowPath ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
        FileHdrModCol.Width = _fileColumns.ShowModified ? new GridLength(130) : new GridLength(0);
        FileHdrAttrCol.Width = _fileColumns.ShowAttributes ? new GridLength(54) : new GridLength(0);
        FileHdrDupCntCol.Width = _fileColumns.ShowDuplicates ? new GridLength(64) : new GridLength(0);
        FileHdrDupSizeCol.Width = _fileColumns.ShowDuplicates ? new GridLength(88) : new GridLength(0);

        FileColAlloc.Visibility = _fileColumns.ShowAllocated ? Visibility.Visible : Visibility.Collapsed;
        FileColPct.Visibility = _fileColumns.ShowPercent ? Visibility.Visible : Visibility.Collapsed;
        FileColPath.Visibility = _fileColumns.ShowPath ? Visibility.Visible : Visibility.Collapsed;
        FileColMod.Visibility = _fileColumns.ShowModified ? Visibility.Visible : Visibility.Collapsed;
        FileColAttr.Visibility = _fileColumns.ShowAttributes ? Visibility.Visible : Visibility.Collapsed;
        FileColDupCnt.Visibility = _fileColumns.ShowDuplicates ? Visibility.Visible : Visibility.Collapsed;
        FileColDupSize.Visibility = _fileColumns.ShowDuplicates ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TreeSortClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !Enum.TryParse<TreeSortColumn>(btn.Tag as string, out var col))
            return;

        if (_treeSort == col)
            _treeSortDesc = !_treeSortDesc;
        else
        {
            _treeSort = col;
            _treeSortDesc = col != TreeSortColumn.Name;
        }

        UpdateTreeHeaderLabels();
        if (_result is null)
            return;

        string? selectedPath = _selected?.FullPath;
        RebuildTree();
        if (selectedPath is not null && _result.FindFolder(selectedPath) is FolderNode node)
            EnsureTreeExpandedTo(node);
    }

    private void UpdateTreeHeaderLabels()
    {
        string arrow = _treeSortDesc ? " ▼" : " ▲";
        TreeColName.Content = "名称" + (_treeSort == TreeSortColumn.Name ? arrow : "");
        TreeColPct.Content = "占上级" + (_treeSort == TreeSortColumn.Percent ? arrow : "");
        TreeColSize.Content = "大小" + (_treeSort == TreeSortColumn.Size ? arrow : "");
        TreeColAlloc.Content = "占用" + (_treeSort == TreeSortColumn.Allocated ? arrow : "");
        TreeColFiles.Content = "文件" + (_treeSort == TreeSortColumn.Files ? arrow : "");
        TreeColFolders.Content = "文件夹" + (_treeSort == TreeSortColumn.Folders ? arrow : "");
        TreeColMod.Content = "修改时间" + (_treeSort == TreeSortColumn.Modified ? arrow : "");
    }

    private void FileSortClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !Enum.TryParse<FileSortColumn>(btn.Tag as string, out var col))
            return;

        if (_fileSort == col)
            _fileSortDesc = !_fileSortDesc;
        else
        {
            _fileSort = col;
            _fileSortDesc = col is not (FileSortColumn.Name or FileSortColumn.Directory or FileSortColumn.Attributes);
        }

        UpdateFileHeaderLabels();
        ShowFiles();
    }

    private void UpdateFileHeaderLabels()
    {
        string arrow = _fileSortDesc ? " ▼" : " ▲";
        FileColName.Content = "名称" + (_fileSort == FileSortColumn.Name ? arrow : "");
        FileColSize.Content = "大小" + (_fileSort == FileSortColumn.Size ? arrow : "");
        FileColAlloc.Content = "占用" + (_fileSort == FileSortColumn.Allocated ? arrow : "");
        FileColPct.Content = "占全盘" + (_fileSort == FileSortColumn.Percent ? arrow : "");
        FileColPath.Content = "路径" + (_fileSort == FileSortColumn.Directory ? arrow : "");
        FileColMod.Content = "修改时间" + (_fileSort == FileSortColumn.Modified ? arrow : "");
        FileColAttr.Content = "属性" + (_fileSort == FileSortColumn.Attributes ? arrow : "");
        FileColDupCnt.Content = "重复数" + (_fileSort == FileSortColumn.DuplicateCount ? arrow : "");
        FileColDupSize.Content = "重复大小" + (_fileSort == FileSortColumn.DuplicateSize ? arrow : "");
    }

    private void RefreshTreeRows()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _tree.RefreshAll(items =>
        {
            for (int i = 0; i < items.Count; i++)
            {
                TreeRow cur = items[i];
                if (cur.Folder is FolderNode folder)
                    items[i] = new TreeRow(folder, cur.Depth, cur.Expanded, _settings, dark);
                else if (cur.File is FileEntry file)
                    items[i] = new TreeRow(file, cur.ParentSizeBytes, cur.Depth, _settings, dark);
            }
        });
    }

    private void BuildUnitMenu()
    {
        UnitMenu.Items.Clear();
        (SizeUnit Unit, string Label)[] units =
        [
            (SizeUnit.Automatic, "自动 (Ctrl+1)"),
            (SizeUnit.Bytes, "字节 B (Ctrl+2)"),
            (SizeUnit.Kilobytes, "千字节 KB (Ctrl+3)"),
            (SizeUnit.Megabytes, "兆字节 MB (Ctrl+4)"),
            (SizeUnit.Gigabytes, "吉字节 GB (Ctrl+5)"),
            (SizeUnit.Terabytes, "太字节 TB (Ctrl+6)"),
        ];

        foreach ((SizeUnit unit, string label) in units)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => SetSizeUnit(unit);
            UnitMenu.Items.Add(item);
        }
    }

    private void SetSizeUnit(SizeUnit unit)
    {
        _settings.SizeUnit = unit;
        _settings.Save();
        _fileColumns.SizeUnit = unit;
        _fileColumns.BinaryUnits = _settings.BinaryUnits;
        _fileColumns.DecimalPlaces = _settings.DecimalPlaces;
        UpdateUnitBadge();

        RefreshTreeRows();
        ShowTypes();
        _files.Refresh();
        ShowCapacity();
        UpdateSelectionSummary();
    }

    private void UpdateUnitBadge()
    {
        string text = _settings.SizeUnit switch
        {
            SizeUnit.Bytes => "单位: B",
            SizeUnit.Kilobytes => _settings.BinaryUnits ? "单位: KiB" : "单位: KB",
            SizeUnit.Megabytes => _settings.BinaryUnits ? "单位: MiB" : "单位: MB",
            SizeUnit.Gigabytes => _settings.BinaryUnits ? "单位: GiB" : "单位: GB",
            SizeUnit.Terabytes => _settings.BinaryUnits ? "单位: TiB" : "单位: TB",
            _ => _settings.BinaryUnits ? "单位: 自动 (1024)" : "单位: 自动",
        };
        UnitBadgeButton.Content = text;
    }
}
