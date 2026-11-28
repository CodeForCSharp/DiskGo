using System.Runtime.InteropServices;
using DiskGo.Core;
using DiskGo.Core.Ntfs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DiskGo;

public sealed partial class MainPage
{
    private void FilesSelected(object sender, SelectionChangedEventArgs e) => UpdateSelectionSummary();

    private void UpdateSelectionSummary()
    {
        long size = 0;
        int count = 0;
        foreach (FileRow row in FileList.SelectedItems.OfType<FileRow>())
        {
            size += row.File.Size;
            count++;
        }

        SelectionText.Text = count == 0 ? "" : $"已选 {count:N0} 项 · {Format(size)}";
    }

    private void FileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_settings.OpenOnDoubleClick && FileList.SelectedItem is FileRow row)
            ShellHelpers.OpenItem(row.File.FullPath);
    }

    private async void FileListDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileRow>().Select(r => r.File.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        var items = new List<IStorageItem>();
        foreach (string path in paths.Take(64))
        {
            try
            {
                items.Add(await StorageFile.GetFileFromPathAsync(path));
            }
            catch
            {
            }
        }
        if (items.Count > 0)
            e.Data.SetStorageItems(items);
    }

    private void TreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TreeRow row)
        {
            if (row.Folder is FolderNode folder)
            {
                if (TreeList.SelectedItem is not TreeRow cur || !ReferenceEquals(cur.Folder, folder))
                {
                    TreeList.SelectedItem = row;
                    SelectFolder(folder, syncTree: false);
                }

                if (folder.IsFileGroup)
                    return;

                MenuFlyout flyout = BuildItemContextMenu(new[] { folder.FullPath }, isFolder: true, fromTree: true);
                flyout.ShowAt(TreeList, e.GetPosition(TreeList));
            }
            else if (row.File is FileEntry file)
            {
                TreeList.SelectedItem = row;
                MenuFlyout flyout = BuildItemContextMenu(new[] { file.FullPath }, isFolder: false, fromTree: true);
                flyout.ShowAt(TreeList, e.GetPosition(TreeList));
            }
        }
    }

    private void FileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileRow row)
        {
            if (!FileList.SelectedItems.Contains(row))
                FileList.SelectedItem = row;
        }

        List<string> paths = FileList.SelectedItems.OfType<FileRow>().Select(r => r.File.FullPath).ToList();
        if (paths.Count == 0)
            return;

        MenuFlyout flyout = BuildItemContextMenu(paths, isFolder: false, fromTree: false);
        flyout.ShowAt(FileList, e.GetPosition(FileList));
    }

    private MenuFlyout BuildItemContextMenu(IReadOnlyList<string> paths, bool isFolder, bool fromTree)
    {
        var menu = new MenuFlyout();
        bool single = paths.Count == 1;
        bool isRoot = isFolder && _result is not null && single &&
                      paths[0].TrimEnd(Path.DirectorySeparatorChar).Equals(_result.Root.FullPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        if (single)
        {
            var openItem = new MenuFlyoutItem { Text = isFolder ? "在资源管理器中打开" : "打开", Icon = new FontIcon { Glyph = "\uE8E5" } };
            openItem.Click += (_, _) => ShellHelpers.OpenItem(paths[0]);
            menu.Items.Add(openItem);

            var revealItem = new MenuFlyoutItem { Text = "在资源管理器中定位", Icon = new FontIcon { Glyph = "\uEC50" } };
            revealItem.Click += (_, _) => ShellHelpers.RevealInExplorer(paths[0]);
            menu.Items.Add(revealItem);

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var copyPathItem = new MenuFlyoutItem { Text = single ? "复制路径" : $"复制 {paths.Count:N0} 个路径", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyPathItem.Click += (_, _) =>
        {
            ShellHelpers.CopyText(string.Join(Environment.NewLine, paths));
            StatusText.Text = $"已复制 {paths.Count:N0} 个路径。";
        };
        menu.Items.Add(copyPathItem);

        var copyPathSizeItem = new MenuFlyoutItem { Text = "复制为路径和大小 (TSV)", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyPathSizeItem.Click += (_, _) => CopyPathsAndSizes(paths);
        menu.Items.Add(copyPathSizeItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        if (!isRoot)
        {
            var cutItem = new MenuFlyoutItem { Text = "剪切", Icon = new FontIcon { Glyph = "\uE8C6" } };
            cutItem.Click += (_, _) => SetShellClipboard(paths, cut: true);
            menu.Items.Add(cutItem);
        }

        var copyFilesItem = new MenuFlyoutItem { Text = "复制文件", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyFilesItem.Click += (_, _) => SetShellClipboard(paths, cut: false);
        menu.Items.Add(copyFilesItem);

        if (single && !isRoot && _settings.AllowRename)
        {
            var renameItem = new MenuFlyoutItem { Text = "重命名 (F2)", Icon = new FontIcon { Glyph = "\uE8AC" } };
            renameItem.Click += async (_, _) => await RenamePathAsync(paths[0]);
            menu.Items.Add(renameItem);
        }

        if (!isRoot)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var recycleItem = new MenuFlyoutItem { Text = "移到回收站 (Del)", Icon = new FontIcon { Glyph = "\uE74D" } };
            recycleItem.Click += async (_, _) => await DeletePathsAsync(paths, DeleteMode.RecycleBin);
            menu.Items.Add(recycleItem);

            var permItem = new MenuFlyoutItem { Text = "永久删除 (Shift+Del)", Icon = new FontIcon { Glyph = "\uE74D" } };
            permItem.Click += async (_, _) => await ConfirmPermanentDeleteAsync(paths);
            menu.Items.Add(permItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var exportSelItem = new MenuFlyoutItem { Text = "导出选中项...", Icon = new FontIcon { Glyph = "\uEDE1" } };
        exportSelItem.Click += async (_, _) => await OpenExportDialogAsync(paths, defaultSelectionOnly: true);
        menu.Items.Add(exportSelItem);

        if (single)
        {
            var propItem = new MenuFlyoutItem { Text = "属性 (Alt+Enter)", Icon = new FontIcon { Glyph = "\uE946" } };
            propItem.Click += (_, _) => ShellHelpers.ShowFileProperties(paths[0]);
            menu.Items.Add(propItem);
        }

        _ = fromTree;
        return menu;
    }

    private void SetShellClipboard(IReadOnlyList<string> paths, bool cut)
    {
        try
        {
            ShellHelpers.SetClipboardFiles(paths, cut);
            StatusText.Text = cut ? $"已剪切 {paths.Count:N0} 项到剪贴板。" : $"已复制 {paths.Count:N0} 项到剪贴板。";
        }
        catch (IOException ex)
        {
            ShowError("剪贴板操作失败", ex.Message);
        }
    }

    private void CopyPathsAndSizes(IReadOnlyList<string> paths)
    {
        if (_result is null)
            return;

        var lines = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (_result.FindFile(path) is FileEntry f)
                lines.Add($"{f.FullPath}\t{f.Size}\t{f.AllocatedSize}");
            else if (_result.FindFolder(path) is FolderNode d)
                lines.Add($"{d.FullPath}\t{d.Size}\t{d.AllocatedSize}");
        }

        if (lines.Count > 0)
        {
            ShellHelpers.CopyText(string.Join(Environment.NewLine, lines));
            StatusText.Text = $"已复制 {lines.Count:N0} 项的路径和大小。";
        }
    }

    private async Task RenamePathAsync(string path)
    {
        if (_result is null)
            return;

        string currentName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var box = new TextBox
        {
            Text = currentName,
            SelectionStart = 0,
            SelectionLength = Path.GetFileNameWithoutExtension(currentName).Length,
        };

        var dialog = new ContentDialog
        {
            Title = "重命名",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = box,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        string nextName = box.Text.Trim();
        if (string.IsNullOrEmpty(nextName) || string.Equals(nextName, currentName, StringComparison.Ordinal))
            return;

        try
        {
            await Task.Run(() => FileActions.Rename(_result, path, nextName));
            string? selectedFolderPath = _selected?.FullPath;
            if (selectedFolderPath is not null && selectedFolderPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                selectedFolderPath = Path.Combine(Path.GetDirectoryName(path)!, nextName);

            ShowResult(_result, $"已重命名为 {nextName}。", selectedFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("重命名失败", ex.Message);
        }
    }

    private async Task ConfirmPermanentDeleteAsync(IReadOnlyList<string> paths)
    {
        if (_result is null)
            return;

        long totalBytes = 0;
        foreach (string p in paths)
        {
            if (_result.FindFile(p) is FileEntry f)
                totalBytes += f.Size;
            else if (_result.FindFolder(p) is FolderNode d)
                totalBytes += d.Size;
        }

        var dialog = new ContentDialog
        {
            Title = "确认永久删除",
            Content = $"确定要永久删除选中的 {paths.Count:N0} 项（共 {Format(totalBytes)}）吗？\n此操作不会经过回收站，且无法撤销。",
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await DeletePathsAsync(paths, DeleteMode.Permanent);
    }

    private async Task DeletePathsAsync(IReadOnlyList<string> paths, DeleteMode mode)
    {
        if (_result is null)
            return;

        var targets = new List<(string Path, FileEntry? File, FolderNode? Folder)>();
        foreach (string path in paths)
        {
            FileEntry? file = _result.FindFile(path);
            FolderNode? folder = file is null ? _result.FindFolder(path) : null;
            if (file is null && (folder is null || folder == _result.Root))
                continue;
            targets.Add((path, file, folder));
        }

        if (targets.Count == 0)
            return;

        string? keepFolder = _selected?.FullPath;
        try
        {
            await Task.Run(() => FileActions.DeleteMany(_result, targets.Select(static target => target.Path), mode));
            ApplyDeletion(targets, keepFolder, $"已删除 {targets.Count:N0} 项。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("删除失败", ex.Message);
        }
    }

    /// <summary>
    /// Drops the rows that were just deleted and refreshes the numbers of the
    /// folders that still contain them. The tree stays expanded exactly as it
    /// was: rebuilding it would fold every folder back up.
    /// </summary>
    private void ApplyDeletion(IReadOnlyList<(string Path, FileEntry? File, FolderNode? Folder)> targets, string? keepFolder, string status)
    {
        if (_result is null)
            return;

        var removedFiles = new HashSet<FileEntry>(ReferenceEqualityComparer.Instance);
        var removedFolders = new HashSet<FolderNode>(ReferenceEqualityComparer.Instance);
        foreach ((_, FileEntry? file, FolderNode? folder) in targets)
        {
            if (file is not null)
                removedFiles.Add(file);
            else if (folder is not null)
                removedFolders.Add(folder);
        }

        RefreshAncestorRows(removedFolders, removedFiles);

        var restated = new HashSet<FolderNode>(ReferenceEqualityComparer.Instance);
        foreach (FolderNode folder in removedFolders)
        {
            if (folder.Parent is not null)
                restated.Add(folder.Parent);
        }

        foreach (FileEntry file in removedFiles)
        {
            if (file.Folder is not null)
                restated.Add(file.Folder);
        }

        _tree.RefreshAll(rows =>
        {
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                TreeRow row = rows[i];
                if (row.File is FileEntry file && removedFiles.Contains(file)
                    || row.Folder is FolderNode folder && (removedFolders.Contains(folder) || DescendsFrom(folder, removedFolders)))
                    rows.RemoveAt(i);
            }

            // Only an expanded folder shows the rows that have to move. A
            // collapsed one keeps its place; its numbers were rewritten above.
            foreach (FolderNode parent in restated)
                RestateChildren(rows, parent);
        });

        FolderNode? stillThere = keepFolder is null ? null : _result.FindFolder(keepFolder);
        if (stillThere is null)
        {
            stillThere = _result.Root;
            _selected = stillThere;
            EnsureTreeExpandedTo(stillThere);
        }

        if (_result.Files.Count == 0 && _result.Root.Children.Count == 0)
        {
            ShowResult(_result, status, stillThere.FullPath);
            return;
        }

        _fileColumns.TotalRootSize = _result.Root.Size;
        TreeSummaryText.Text = $"{_result.Root.FolderCount:N0} 个文件夹 · {_result.Files.Count:N0} 个文件";
        BackButton.IsEnabled = _selected?.Parent is not null;
        if (_selected is not null)
            UpdateBreadcrumb(_selected);
        ShowCapacity();
        ShowTypes();
        ShowFiles();
        DrawMap();
        StatusText.Text = status;
    }

    /// <summary>
    /// Rewrites the visible rows of every folder whose totals changed: the
    /// parents of a deleted folder, and the folder a deleted file lived in.
    /// Expansion and scroll position are left alone.
    /// </summary>
    private void RefreshAncestorRows(HashSet<FolderNode> removedFolders, HashSet<FileEntry> removedFiles)
    {
        var touched = new HashSet<FolderNode>(ReferenceEqualityComparer.Instance);
        foreach (FolderNode folder in removedFolders)
        {
            for (FolderNode? parent = folder.Parent; parent is not null; parent = parent.Parent)
                touched.Add(parent);
        }

        foreach (FileEntry file in removedFiles)
        {
            for (FolderNode? parent = file.Folder; parent is not null; parent = parent.Parent)
                touched.Add(parent);
        }

        if (touched.Count == 0)
            return;

        bool dark = ActualTheme == ElementTheme.Dark;
        for (int i = 0; i < _tree.Count; i++)
        {
            TreeRow row = _tree[i];
            if (row.Folder is FolderNode folder && touched.Contains(folder))
                _tree[i] = new TreeRow(folder, row.Depth, row.Expanded, _settings, dark);
        }
    }

    /// <summary>
    /// Rebuilds the direct children of one expanded folder in the current sort
    /// order. Deeper rows stay attached to the child they were expanded under,
    /// so folding the tree back open is not part of a delete.
    /// </summary>
    private void RestateChildren(IList<TreeRow> rows, FolderNode parent)
    {
        int parentIndex = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i].Folder, parent))
            {
                parentIndex = i;
                break;
            }
        }

        if (parentIndex < 0 || !rows[parentIndex].Expanded)
            return;

        int depth = rows[parentIndex].Depth;
        int end = parentIndex + 1;
        while (end < rows.Count && rows[end].Depth > depth)
            end++;

        var blocks = new List<List<TreeRow>>();
        for (int i = parentIndex + 1; i < end;)
        {
            var block = new List<TreeRow> { rows[i] };
            int childDepth = rows[i].Depth;
            i++;
            while (i < end && rows[i].Depth > childDepth)
                block.Add(rows[i++]);
            blocks.Add(block);
        }

        Comparison<TreeRow> order = TreeRowOrder();
        blocks.Sort((a, b) => order(a[0], b[0]));

        int write = parentIndex + 1;
        foreach (List<TreeRow> block in blocks)
        {
            foreach (TreeRow row in block)
                rows[write++] = row;
        }
    }

    private Comparison<TreeRow> TreeRowOrder()
        => (_treeSort, _treeSortDesc) switch
        {
            (TreeSortColumn.Name, false) => static (a, b) => CompareFolderFirst(a, b) ?? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            (TreeSortColumn.Name, true) => static (a, b) => CompareFolderFirst(a, b) ?? string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase),
            (TreeSortColumn.Percent, false) => static (a, b) => a.PercentValue.CompareTo(b.PercentValue),
            (TreeSortColumn.Percent, true) => static (a, b) => b.PercentValue.CompareTo(a.PercentValue),
            (TreeSortColumn.Allocated, false) => static (a, b) => a.AllocatedBytes.CompareTo(b.AllocatedBytes),
            (TreeSortColumn.Allocated, true) => static (a, b) => b.AllocatedBytes.CompareTo(a.AllocatedBytes),
            (TreeSortColumn.Files, false) => static (a, b) => a.FileCountValue.CompareTo(b.FileCountValue),
            (TreeSortColumn.Files, true) => static (a, b) => b.FileCountValue.CompareTo(a.FileCountValue),
            (TreeSortColumn.Folders, false) => static (a, b) => a.FolderCountValue.CompareTo(b.FolderCountValue),
            (TreeSortColumn.Folders, true) => static (a, b) => b.FolderCountValue.CompareTo(a.FolderCountValue),
            (TreeSortColumn.Modified, false) => static (a, b) => a.ModifiedValue.CompareTo(b.ModifiedValue),
            (TreeSortColumn.Modified, true) => static (a, b) => b.ModifiedValue.CompareTo(a.ModifiedValue),
            (TreeSortColumn.Size, false) => static (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
            _ => static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes),
        };

    private static bool DescendsFrom(FolderNode folder, HashSet<FolderNode> ancestors)
    {
        for (FolderNode? parent = folder.Parent; parent is not null; parent = parent.Parent)
        {
            if (ancestors.Contains(parent))
                return true;
        }

        return false;
    }

    public void OpenExportDialog()
    {
        var selected = GetActiveSelectedPaths();
        _ = OpenExportDialogAsync(selected, defaultSelectionOnly: false);
    }

    private IReadOnlyList<string> GetActiveSelectedPaths()
    {
        var filePaths = FileList.SelectedItems.OfType<FileRow>().Select(r => r.File.FullPath).ToList();
        if (filePaths.Count > 0)
            return filePaths;
        if (_selected is not null && _selected != _result?.Root)
            return new[] { _selected.FullPath };
        return Array.Empty<string>();
    }

    private async Task OpenExportDialogAsync(IReadOnlyList<string> selectedPaths, bool defaultSelectionOnly)
    {
        if (_result is null)
        {
            StatusText.Text = "还没有可以导出的扫描结果。";
            return;
        }

        ExportDialogResult? req = await Dialogs.ShowExportAsync(XamlRoot, _result, selectedPaths, defaultSelectionOnly);
        if (req is null)
            return;

        bool csv = req.IsTypesExport || req.Options.Format == ExportFormat.Csv;
        string? path;
        try
        {
            path = await ShellHelpers.PickSaveFileAsync(req.FileName, csv ? "CSV 文件" : "TSV 文件", csv ? ".csv" : ".tsv");
        }
        catch (COMException ex)
        {
            ShowError("无法选择保存位置", ex.Message);
            return;
        }

        if (path is null)
            return;

        try
        {
            if (req.IsTypesExport)
                await Task.Run(() => ExtensionExporter.Export(_result, path));
            else
                await Task.Run(() => CsvExporter.Export(_result, path, req.Options));

            _lastExportFolder = Path.GetDirectoryName(path);
            OpenExportFolderButton.Visibility = Visibility.Visible;
            StatusText.Text = "已导出: " + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("导出失败", ex.Message);
        }
    }

    public async void OpenSettingsDialog()
    {
        bool isFolderTarget = CanMonitorCurrentTargets();
        bool saved = await Dialogs.ShowSettingsAsync(XamlRoot, _settings, isFolderTarget);
        if (!saved)
            return;

        _fileColumns.SizeUnit = _settings.SizeUnit;
        _fileColumns.BinaryUnits = _settings.BinaryUnits;
        _fileColumns.DecimalPlaces = _settings.DecimalPlaces;
        MapNamesMenuItem.IsChecked = _settings.TreemapNames;
        MapFreeMenuItem.IsChecked = _settings.TreemapFreeSpace;
        MapAllocMenuItem.IsChecked = _settings.TreemapByAllocated;
        UpdateUnitBadge();

        if (_result is not null)
        {
            _result.Root.UseNewestModified(_settings.ShowNewestModified);
            RefreshTreeRows();
            ShowTypes();
            _files.Refresh();
            ShowCapacity();
            DrawMap();
        }
    }

    public async void DumpMftFromMenu()
    {
        (char DriveLetter, string FilePath)? req = await Dialogs.ShowDumpMftAsync(XamlRoot);
        if (req is null)
            return;

        InlineBanner.IsOpen = false;
        SetScanningUi(true, $"正在导出 {req.Value.DriveLetter}: 的 MFT...");
        try
        {
            var progress = new Progress<long>(count =>
                StatusText.Text = $"正在写入 MFT 快照: {count:N0} 条记录...");
            long records = await Task.Run(() => MftDump.Write(req.Value.DriveLetter, req.Value.FilePath, CancellationToken.None, progress));
            _lastExportFolder = Path.GetDirectoryName(req.Value.FilePath);
            OpenExportFolderButton.Visibility = Visibility.Visible;
            StatusText.Text = $"已导出 {records:N0} 条 MFT 记录到 {req.Value.FilePath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError("导出 MFT 失败", ex.Message);
        }
        finally
        {
            SetScanningUi(false);
        }
    }

    private async void WarningsClicked(object sender, RoutedEventArgs e)
    {
        if (_result is not null && _result.Warnings.Count > 0)
            await Dialogs.ShowWarningsAsync(XamlRoot, _result.Warnings);
    }

    private void OpenExportFolderClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastExportFolder) && Directory.Exists(_lastExportFolder))
            ShellHelpers.OpenItem(_lastExportFolder);
    }

    private async void EmptyPickClicked(object sender, RoutedEventArgs e) => await PickFolder();
    private async void EmptyImportClicked(object sender, RoutedEventArgs e) => await PickImport();
}
