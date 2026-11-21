using DiskGo.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DiskGo;

public sealed partial class MainPage
{
    private void ShowResult(ScanResult result, string status, string? preserveSelectionPath = null)
    {
        _result = result;
        _typeFilter = null;
        UpdateTypeFilterPill();
        _fileColumns.TotalRootSize = result.Root.Size;

        if (result.Files.Count == 0 && result.Root.Children.Count == 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            ScanningState.Visibility = Visibility.Collapsed;
            Results.Visibility = Visibility.Collapsed;
            NoResultsState.Visibility = Visibility.Visible;

            bool hasFilter = !string.IsNullOrWhiteSpace(IncludeBox.Text) || !string.IsNullOrWhiteSpace(ExcludeBox.Text);
            NoResultsHint.Text = hasFilter ? "当前设置的筛选规则可能将所有文件排除了。" : "该位置下没有任何文件。";
            ClearFilterAndRescanBtn.Visibility = hasFilter ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            ScanningState.Visibility = Visibility.Collapsed;
            NoResultsState.Visibility = Visibility.Collapsed;
            Results.Visibility = Visibility.Visible;

            RebuildTree();
            FolderNode targetNode = (preserveSelectionPath is not null ? result.FindFolder(preserveSelectionPath) : null) ?? result.Root;
            SelectFolder(targetNode);
        }

        ShowCapacity();
        ShowWarningsBadge();
        StatusText.Text = status;
    }

    private void ShowWarningsBadge()
    {
        if (_result is not null && _result.Warnings.Count > 0)
        {
            WarningsButton.Content = $"有 {_result.Warnings.Count:N0} 个目录未读取";
            WarningsButton.Visibility = Visibility.Visible;
        }
        else
        {
            WarningsButton.Visibility = Visibility.Collapsed;
        }
    }

    private void RebuildTree()
    {
        if (_result is null)
        {
            _tree.Clear();
            return;
        }

        TreeSummaryText.Text = $"{_result.Root.FolderCount:N0} 个文件夹 · {_result.Files.Count:N0} 个文件";
        var initial = new List<TreeRow>();
        initial.Add(CreateTreeFolderRow(_result.Root, depth: 0, expanded: true));
        initial.AddRange(SortedChildrenAndFiles(_result.Root, childDepth: 1));
        _tree.ReplaceAll(initial);
    }

    private int InsertChildrenAndFiles(FolderNode folder, int insertIndex, int childDepth)
    {
        List<TreeRow> children = SortedChildrenAndFiles(folder, childDepth);
        _tree.InsertRange(insertIndex, children);
        return insertIndex + children.Count;
    }

    private List<TreeRow> SortedChildrenAndFiles(FolderNode folder, int childDepth)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        long parentSize = folder.Size;
        var rows = new List<TreeRow>(folder.Children.Count + folder.Files.Count);
        foreach (FolderNode child in folder.Children)
            rows.Add(new TreeRow(child, childDepth, expanded: false, _settings, dark));
        foreach (FileEntry file in folder.Files)
            rows.Add(new TreeRow(file, parentSize, childDepth, _settings, dark));

        Comparison<TreeRow> cmp = (_treeSort, _treeSortDesc) switch
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
        rows.Sort(cmp);
        return rows;
    }

    private static int? CompareFolderFirst(TreeRow a, TreeRow b)
        => a.IsFolder == b.IsFolder ? null : (a.IsFolder ? -1 : 1);

    private TreeRow CreateTreeFolderRow(FolderNode folder, int depth, bool expanded)
        => new(folder, depth, expanded, _settings, ActualTheme == ElementTheme.Dark);

    private void CollapseAllClicked(object sender, RoutedEventArgs e)
    {
        if (_result is null)
            return;
        RebuildTree();
        SelectFolder(_result.Root);
    }

    private void TreeClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TreeRow row || _result is null)
            return;

        if (row.Folder is FolderNode folder)
            SelectFolder(folder);
    }

    private void TreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TreeList.SelectedItem is not TreeRow row)
            return;

        if (row.Folder is not null)
            ToggleTreeRow(row);
        else if (row.File is FileEntry file && _settings.OpenOnDoubleClick)
            ShellHelpers.OpenItem(file.FullPath);
    }

    private void TreeExpanderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TreeRow row })
            ToggleTreeRow(row);
    }

    private void ToggleTreeRow(TreeRow row)
    {
        if (row.Folder is not FolderNode folder || (folder.Children.Count == 0 && folder.Files.Count == 0))
            return;

        int index = _tree.IndexOf(row);
        if (index < 0)
            return;

        if (row.Expanded)
        {
            int removeCount = 0;
            while (index + 1 + removeCount < _tree.Count && _tree[index + 1 + removeCount].Depth > row.Depth)
                removeCount++;
            _tree.RemoveRange(index + 1, removeCount);
            _tree[index] = CreateTreeFolderRow(folder, row.Depth, expanded: false);
        }
        else
        {
            _tree[index] = CreateTreeFolderRow(folder, row.Depth, expanded: true);
            InsertChildrenAndFiles(folder, index + 1, row.Depth + 1);
        }

        if (_selected == folder)
            TreeList.SelectedIndex = index;
    }

    private void EnsureTreeExpandedTo(FolderNode target)
    {
        var chain = new Stack<FolderNode>();
        FolderNode? cur = target;
        while (cur is not null)
        {
            chain.Push(cur);
            cur = cur.Parent;
        }

        while (chain.Count > 0)
        {
            FolderNode node = chain.Pop();
            int idx = -1;
            for (int i = 0; i < _tree.Count; i++)
            {
                if (ReferenceEquals(_tree[i].Folder, node))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
                continue;

            if (chain.Count > 0 && !_tree[idx].Expanded && (node.Children.Count > 0 || node.Files.Count > 0))
            {
                _tree[idx] = CreateTreeFolderRow(node, _tree[idx].Depth, expanded: true);
                InsertChildrenAndFiles(node, idx + 1, _tree[idx].Depth + 1);
            }
            else if (chain.Count == 0)
            {
                TreeList.SelectedIndex = idx;
                TreeList.ScrollIntoView(_tree[idx]);
            }
        }
    }

    private void SelectFolder(FolderNode folder, bool syncTree = true)
    {
        _selected = folder;
        _typeFilter = null;
        UpdateTypeFilterPill();

        BackButton.IsEnabled = folder.Parent is not null;
        UpdateBreadcrumb(folder);
        if (syncTree)
            EnsureTreeExpandedTo(folder);

        ShowTypes();
        ShowFiles();
        DrawMap();
    }

    private void UpdateBreadcrumb(FolderNode folder)
    {
        BreadcrumbPanel.Children.Clear();
        var chain = new List<FolderNode>();
        for (FolderNode? c = folder; c is not null; c = c.Parent)
            chain.Add(c);
        chain.Reverse();

        for (int i = 0; i < chain.Count; i++)
        {
            FolderNode node = chain[i];
            if (i > 0)
            {
                BreadcrumbPanel.Children.Add(new FontIcon
                {
                    Glyph = "\uE76C",
                    FontSize = 10,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(2, 0, 2, 0),
                });
            }

            string text = node.Parent is null ? TreeRow.VolumeLabel(node) : node.Name;
            bool isLast = i == chain.Count - 1;
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(6, 2, 6, 2),
                Height = 24,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                FontWeight = isLast ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)Application.Current.Resources[isLast ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
            };
            btn.Click += (_, _) => SelectFolder(node);
            BreadcrumbPanel.Children.Add(btn);
        }
    }

    private void ShowTypes()
    {
        CancellationToken token = ResetCts(ref _typesCts);
        if (_selected is null)
        {
            _types.Clear();
            return;
        }

        FolderNode target = _selected;
        if (_result is not null && ReferenceEquals(target, _result.Root))
        {
            ApplyTypeTotals(_result.Extensions, target.Size);
            return;
        }

        if (target.FileCount <= 5_000)
        {
            ApplyTypeTotals(ExtensionSummary.From(EnumerateFilesUnder(target, token), token), target.Size);
            return;
        }

        _ = ShowTypesAsync(target, token);
    }

    private async Task ShowTypesAsync(FolderNode target, CancellationToken token)
    {
        try
        {
            List<ExtensionTotal> totals = await Task.Run(
                () => ExtensionSummary.From(EnumerateFilesUnder(target, token), token),
                token);

            if (token.IsCancellationRequested || !ReferenceEquals(_selected, target))
                return;

            ApplyTypeTotals(totals, target.Size);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyTypeTotals(IReadOnlyList<ExtensionTotal> totals, long scopeTotal)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        var rows = new List<TypeRow>(totals.Count);
        foreach (ExtensionTotal total in totals)
        {
            string display = total.Extension == "(none)" ? "(无)" : total.Extension;
            rows.Add(new TypeRow(
                total.Extension,
                display,
                Format(total.Size),
                SizeText.Percent(total.PercentOf(scopeTotal)),
                total.FileCount.ToString("N0"),
                TypeColors.BrushFor(total.Extension, dark)));
        }

        _types.ReplaceAll(rows);
    }

    private void TypeClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TypeRow row)
            return;

        _typeFilter = _typeFilter == row.Key ? null : row.Key;
        if (_typeFilter is null)
            TypeList.SelectedItem = null;
        else if (!_settings.ShowFilePane)
            ApplyFilePaneVisibility(true, save: true);
        UpdateTypeFilterPill();
        ShowFiles();
    }

    private void ClearTypeFilterClicked(object sender, RoutedEventArgs e)
    {
        _typeFilter = null;
        TypeList.SelectedItem = null;
        UpdateTypeFilterPill();
        ShowFiles();
    }

    private void UpdateTypeFilterPill()
    {
        if (_typeFilter is null)
        {
            TypeFilterPill.Visibility = Visibility.Collapsed;
            ClearTypeFilterTopBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            string shown = _typeFilter == "(none)" ? "(无)" : _typeFilter;
            TypeFilterPillText.Text = $"类型: {shown}";
            TypeFilterPill.Visibility = Visibility.Visible;
            ClearTypeFilterTopBtn.Visibility = Visibility.Visible;
        }
    }

    private static IEnumerable<FileEntry> EnumerateFilesUnder(FolderNode folder, CancellationToken cancellationToken = default)
    {
        var stack = new Stack<FolderNode>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FolderNode current = stack.Pop();
            foreach (FileEntry file in current.Files)
                yield return file;
            foreach (FolderNode child in current.Children)
                stack.Push(child);
        }
    }

    private void ShowCapacity()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        if (_result is null)
        {
            CapacityText.Text = "尚未扫描";
            ToolTipService.SetToolTip(CapacityText, null);
            CapacitySegBar.Update(null, _settings, dark);
            return;
        }

        if (!_result.Drive.IsKnown)
        {
            CapacityText.Text = $"已扫描 {Format(_result.Root.Size)} · {_result.Files.Count:N0} 个文件";
            ToolTipService.SetToolTip(CapacityText, null);
            CapacitySegBar.Update(_result, _settings, dark);
            return;
        }

        DriveSpace drive = _result.Drive;
        string reserved = drive.Reserved > 0 ? $" · 预留 {Format(drive.Reserved)}" : "";
        CapacityText.Text = $"已用 {Format(drive.Used)} / 总计 {Format(drive.Total)} · 空闲 {Format(drive.Free)}{reserved}";

        string serialTip = drive.Serial is uint ser ? $"卷序列号: {SizeText.Serial(ser)}" : "卷序列号未知";
        ToolTipService.SetToolTip(CapacityText, serialTip);
        CapacitySegBar.Update(_result, _settings, dark);
    }
}
