using DiskGo.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace DiskGo;

public sealed partial class MainPage
{
    private void FilterToggled(object sender, RoutedEventArgs e)
        => FilterRow.Visibility = FilterButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void TypePaneToggled(object sender, RoutedEventArgs e)
    {
        _userToggledTypePane = true;
        bool visible = TypePaneToggle.IsChecked == true;
        _settings.ShowTypePane = visible;
        _settings.Save();
        ApplyTypePaneVisibility(visible);
    }

    private void ApplyTypePaneVisibility(bool visible)
    {
        TypePaneCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TypeSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TypeSplitterCol.Width = visible ? new GridLength(8) : new GridLength(0);
        TypeColumn.MinWidth = visible ? 200 : 0;
        TypeColumn.Width = visible ? new GridLength(Math.Clamp(_settings.TypePaneWidth, 200, 480)) : new GridLength(0);
        TypePaneToggle.IsChecked = visible;
    }

    private void TypeSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingType = true;
        _dragStart = e.GetCurrentPoint(this).Position.X;
        _dragSize = TypeColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void TypeSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingType)
            return;
        double next = _dragSize - (e.GetCurrentPoint(this).Position.X - _dragStart);
        TypeColumn.Width = new GridLength(Math.Clamp(next, 200, 480));
    }

    private void TypeSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingType)
            return;
        _draggingType = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _settings.TypePaneWidth = TypeColumn.ActualWidth;
        _settings.Save();
    }

    private void TreeSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingTreeSplit = true;
        _dragStart = e.GetCurrentPoint(Results).Position.Y;
        _dragSize = TreeBand.ActualHeight;
        _dragSizeAlt = MapRow.ActualHeight;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void TreeSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingTreeSplit)
            return;
        double delta = e.GetCurrentPoint(Results).Position.Y - _dragStart;
        double combined = _dragSize + _dragSizeAlt;
        double nextTree = Math.Clamp(_dragSize + delta, 120, Math.Max(120, combined - 96));
        double nextMap = Math.Max(96, combined - nextTree);
        TreeBand.Height = new GridLength(nextTree);
        MapRow.Height = new GridLength(nextMap);
    }

    private void TreeSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingTreeSplit)
            return;
        _draggingTreeSplit = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _settings.TreemapHeight = MapRow.ActualHeight;
        _settings.Save();
        DrawMap();
    }

    private void MapSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_settings.ShowFilePane)
            return;
        _draggingMapSplit = true;
        _dragStart = e.GetCurrentPoint(Results).Position.Y;
        _dragSize = MapRow.ActualHeight;
        _dragSizeAlt = FileBand.ActualHeight;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void MapSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingMapSplit || !_settings.ShowFilePane)
            return;
        double delta = e.GetCurrentPoint(Results).Position.Y - _dragStart;
        double combined = _dragSize + _dragSizeAlt;
        double nextMap = Math.Clamp(_dragSize + delta, 96, Math.Max(96, combined - 110));
        double nextFile = Math.Max(110, combined - nextMap);
        MapRow.Height = new GridLength(nextMap);
        FileBand.Height = new GridLength(nextFile);
    }

    private void MapSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingMapSplit)
            return;
        _draggingMapSplit = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _settings.TreemapHeight = MapRow.ActualHeight;
        _settings.Save();
        DrawMap();
    }

    private static bool IsCtrlDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsShiftDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsAltDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);

    private bool IsTextBoxFocused()
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (focused is TextBox)
                return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private void PageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        bool shift = IsShiftDown();

        if (e.Key == VirtualKey.F5)
        {
            _ = RunScan();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (_scan is not null)
            {
                _ = _scan.CancelAsync();
                e.Handled = true;
            }
            else if (SearchBox.Text.Length > 0)
            {
                SearchBox.Text = "";
                ShowFiles();
                e.Handled = true;
            }
            else if (_settings.ShowFilePane)
            {
                ApplyFilePaneVisibility(false, save: true);
                e.Handled = true;
            }
            return;
        }

        if (ctrl && !shift && e.Key == VirtualKey.F)
        {
            ApplyFilePaneVisibility(true, save: true, focusSearch: true);
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == VirtualKey.F)
        {
            FilterButton.IsChecked = !(FilterButton.IsChecked == true);
            FilterToggled(FilterButton, e);
            if (FilterRow.Visibility == Visibility.Visible)
                IncludeBox.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == VirtualKey.E)
        {
            OpenExportDialog();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == VirtualKey.N)
        {
            ShellHelpers.OpenNewWindow();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number6)
        {
            SizeUnit unit = (SizeUnit)(e.Key - VirtualKey.Number1);
            SetSizeUnit(unit);
            e.Handled = true;
            return;
        }

        if (IsTextBoxFocused())
            return;

        if (e.Key == VirtualKey.Back && _selected?.Parent is FolderNode parent)
        {
            SelectFolder(parent);
            e.Handled = true;
        }
    }

    private void TreeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TreeList.SelectedItem is TreeRow row)
        {
            string? path = row.Folder?.FullPath ?? row.File?.FullPath;
            bool isGroup = row.Folder?.IsFileGroup == true;
            if (path is not null && HandleCommonItemKey(e, new[] { path }, isFolder: row.IsFolder, isGroup))
                return;

            if (row.Folder is FolderNode folder)
            {
                if (e.Key == VirtualKey.Right && !row.Expanded && (folder.Children.Count > 0 || folder.Files.Count > 0))
                {
                    ToggleTreeRow(row);
                    e.Handled = true;
                    return;
                }
                if (e.Key == VirtualKey.Left && row.Expanded)
                {
                    ToggleTreeRow(row);
                    e.Handled = true;
                    return;
                }
            }
        }

        if (!IsCtrlDown() && !IsAltDown() && TryLetterJump(e.Key, out string prefix))
        {
            int start = Math.Max(-1, TreeList.SelectedIndex);
            for (int offset = 1; offset <= _tree.Count; offset++)
            {
                int idx = (start + offset) % _tree.Count;
                if (_tree[idx].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    TreeList.SelectedIndex = idx;
                    TreeList.ScrollIntoView(_tree[idx]);
                    if (_tree[idx].Folder is FolderNode folder)
                        SelectFolder(folder, syncTree: false);
                    e.Handled = true;
                    break;
                }
            }
        }
    }

    private void FileKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsCtrlDown() && e.Key == VirtualKey.A)
        {
            FileList.SelectAll();
            e.Handled = true;
            return;
        }

        var selectedPaths = FileList.SelectedItems.OfType<FileRow>().Select(r => r.File.FullPath).ToList();
        if (selectedPaths.Count > 0 && HandleCommonItemKey(e, selectedPaths, isFolder: false, isFileGroup: false))
            return;

        if (!IsCtrlDown() && !IsAltDown() && TryLetterJump(e.Key, out string prefix))
        {
            int idx = _files.FindNextPrefix(prefix, Math.Max(-1, FileList.SelectedIndex));
            if (idx >= 0)
            {
                FileList.SelectedIndex = idx;
                FileList.ScrollIntoView(_files[idx]);
                e.Handled = true;
            }
        }
    }

    private bool HandleCommonItemKey(KeyRoutedEventArgs e, IReadOnlyList<string> paths, bool isFolder, bool isFileGroup)
    {
        if (paths.Count == 0 || isFileGroup)
            return false;

        bool ctrl = IsCtrlDown();
        bool shift = IsShiftDown();
        bool alt = IsAltDown();
        bool single = paths.Count == 1;

        if (ctrl && shift && e.Key == VirtualKey.C)
        {
            CopyPathsAndSizes(paths);
            e.Handled = true;
            return true;
        }
        if (ctrl && e.Key == VirtualKey.C)
        {
            ShellHelpers.CopyText(string.Join(Environment.NewLine, paths));
            StatusText.Text = $"已复制 {paths.Count:N0} 个路径。";
            e.Handled = true;
            return true;
        }
        if (ctrl && e.Key == VirtualKey.Enter && single)
        {
            ShellHelpers.RevealInExplorer(paths[0]);
            e.Handled = true;
            return true;
        }
        if (alt && e.Key == VirtualKey.Enter && single)
        {
            ShellHelpers.ShowFileProperties(paths[0]);
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Enter && single)
        {
            ShellHelpers.OpenItem(paths[0]);
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.F2 && single && _settings.AllowRename)
        {
            _ = RenamePathAsync(paths[0]);
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Delete)
        {
            if (shift)
                _ = ConfirmPermanentDeleteAsync(paths);
            else
                _ = DeletePathsAsync(paths, DeleteMode.RecycleBin);
            e.Handled = true;
            return true;
        }

        _ = isFolder;
        return false;
    }

    private static bool TryLetterJump(VirtualKey key, out string ch)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            ch = ((char)('a' + (key - VirtualKey.A))).ToString();
            return true;
        }
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            ch = ((char)('0' + (key - VirtualKey.Number0))).ToString();
            return true;
        }
        ch = "";
        return false;
    }
}
