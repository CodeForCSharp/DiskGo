using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DiskGo.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace DiskGo;

public sealed partial class MainPage
{
    private void MapSized(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 8 || e.NewSize.Height < 8)
            return;
        double scale = MapImage.XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Round(e.NewSize.Width * (scale < 1 ? 1 : scale));
        int height = (int)Math.Round(e.NewSize.Height * (scale < 1 ? 1 : scale));
        if (_bitmap is not null && _bitmap.PixelWidth == width && _bitmap.PixelHeight == height)
            return;
        DrawMap();
    }

    private void DrawMap()
    {
        if (_selected is null || MapBorder.ActualWidth < 8 || MapBorder.ActualHeight < 8)
            return;

        double scale = MapImage.XamlRoot?.RasterizationScale ?? 1;
        if (scale < 1)
            scale = 1;
        int width = Math.Max(8, (int)Math.Round(MapBorder.ActualWidth * scale));
        int height = Math.Max(8, (int)Math.Round(MapBorder.ActualHeight * scale));
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height);
            _pixels = new byte[width * height * 4];
            MapImage.Source = _bitmap;
        }

        _treemap.ShowNames = _settings.TreemapNames;
        _treemap.ShowFreeSpace = _settings.TreemapFreeSpace;
        _treemap.ByAllocated = _settings.TreemapByAllocated;
        _treemap.Dark = ActualTheme == ElementTheme.Dark;
        _treemap.HighContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        _treemap.FreeBytes = _result is not null && _selected == _result.Root && _result.Drive.IsKnown
            ? _result.Drive.Free
            : 0;
        _treemap.PixelsPerDip = (float)scale;
        _treemap.Draw(_pixels, width, height, _selected);

        using Stream stream = _bitmap.PixelBuffer.AsStream();
        stream.Position = 0;
        stream.Write(_pixels, 0, _pixels.Length);
        _bitmap.Invalidate();
    }

    private TreemapHit? HitAt(Point point)
    {
        double scale = MapImage.XamlRoot?.RasterizationScale ?? 1;
        if (scale < 1)
            scale = 1;
        return _treemap.Hit(new Point(point.X * scale, point.Y * scale));
    }

    private void MapMoved(object sender, PointerRoutedEventArgs e)
    {
        TreemapHit? hit = HitAt(e.GetCurrentPoint(MapBorder).Position);
        if (hit is null)
        {
            ToolTipService.SetToolTip(MapBorder, null);
            return;
        }

        string tip = hit.Value.Free
            ? $"空闲空间\n{Format(hit.Value.Size)}"
            : $"{hit.Value.Name}\n{hit.Value.Path}\n大小: {Format(hit.Value.Size)} · 占用: {Format(hit.Value.AllocatedSize)}";
        ToolTipService.SetToolTip(MapBorder, tip);
    }

    private void MapPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint pt = e.GetCurrentPoint(MapBorder);
        if (!pt.Properties.IsLeftButtonPressed)
            return;

        TreemapHit? hit = HitAt(pt.Position);
        if (hit?.Folder is FolderNode folder)
        {
            SelectFolder(folder);
        }
        else if (hit?.File is FileEntry file && _result is not null)
        {
            // Highlight corresponding file in FileList if visible
            int idx = _files.IndexOf(new FileRow(file, -1, _fileColumns));
            if (idx >= 0)
            {
                FileList.SelectedIndex = idx;
                FileList.ScrollIntoView(_files[idx]);
            }
        }
    }

    private void MapDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        TreemapHit? hit = HitAt(e.GetPosition(MapBorder));
        if (hit?.File is FileEntry file)
            ShellHelpers.OpenItem(file.FullPath);
    }

    private void MapRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        TreemapHit? hit = HitAt(e.GetPosition(MapBorder));
        if (hit is null || hit.Value.Free)
            return;

        string? path = hit.Value.Folder?.FullPath ?? hit.Value.File?.FullPath;
        if (string.IsNullOrEmpty(path))
            return;

        bool isFolder = hit.Value.Folder is not null;
        MenuFlyout flyout = BuildItemContextMenu(new[] { path }, isFolder, fromTree: false);
        flyout.ShowAt(MapBorder, e.GetPosition(MapBorder));
    }

    private void BackClicked(object sender, RoutedEventArgs e)
    {
        if (_selected?.Parent is FolderNode parent)
            SelectFolder(parent);
    }

    private void MapOptionMenuClicked(object sender, RoutedEventArgs e)
    {
        _settings.TreemapNames = MapNamesMenuItem.IsChecked;
        _settings.TreemapFreeSpace = MapFreeMenuItem.IsChecked;
        _settings.TreemapByAllocated = MapAllocMenuItem.IsChecked;
        _settings.Save();
        DrawMap();
    }

    private void FilePaneToggled(object sender, RoutedEventArgs e)
        => ApplyFilePaneVisibility(FilePaneToggle.IsChecked == true, save: true, focusSearch: FilePaneToggle.IsChecked == true);

    private void CloseFilePaneClicked(object sender, RoutedEventArgs e)
        => ApplyFilePaneVisibility(false, save: true);

    private void ApplyFilePaneVisibility(bool visible, bool save, bool focusSearch = false)
    {
        FilePaneToggle.IsChecked = visible;
        FilePaneCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MapFileSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (visible)
        {
            TreeBand.Height = new GridLength(1.15, GridUnitType.Star);
            MapRow.Height = new GridLength(0.9, GridUnitType.Star);
            MapFileSplitterRow.Height = new GridLength(8);
            FileBand.MinHeight = 120;
            FileBand.Height = new GridLength(1.0, GridUnitType.Star);
            if (focusSearch)
            {
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
            }
        }
        else
        {
            MapFileSplitterRow.Height = new GridLength(0);
            FileBand.MinHeight = 0;
            FileBand.Height = new GridLength(0);
            TreeBand.Height = new GridLength(1.15, GridUnitType.Star);
            MapRow.Height = new GridLength(1.0, GridUnitType.Star);
        }

        if (save && _settings.ShowFilePane != visible)
        {
            _settings.ShowFilePane = visible;
            _settings.Save();
        }
    }

    private async void ExportPngClicked(object sender, RoutedEventArgs e)
    {
        if (_result is null || _selected is null)
        {
            StatusText.Text = "请先扫描后再导出矩形图。";
            return;
        }

        TreemapPngOptions? pngOptions = await Dialogs.ShowTreemapExportAsync(XamlRoot, _settings);
        if (pngOptions is null)
            return;

        string? path;
        try
        {
            path = await ShellHelpers.PickSaveFileAsync($"DiskGo_Treemap_{DateTime.Now:yyyyMMdd_HHmmss}", "PNG 图像", ".png");
        }
        catch (COMException ex)
        {
            ShowError("无法选择保存位置", ex.Message);
            return;
        }

        if (path is null)
            return;

        long free = pngOptions.ShowFreeSpace && _selected == _result.Root && _result.Drive.IsKnown
            ? _result.Drive.Free
            : 0;

        await Treemap.ExportPngAsync(
            path,
            pngOptions.Width,
            pngOptions.Height,
            _selected,
            free,
            pngOptions.ShowNames,
            pngOptions.ShowFreeSpace,
            pngOptions.ByAllocated,
            pngOptions.Monochrome,
            ActualTheme == ElementTheme.Dark);

        _lastExportFolder = Path.GetDirectoryName(path);
        OpenExportFolderButton.Visibility = Visibility.Visible;
        StatusText.Text = "已保存矩形图: " + path;
    }
}
