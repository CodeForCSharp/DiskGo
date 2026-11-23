using System.Runtime.InteropServices;
using DiskGo.Core;
using DiskGo.Core.Ntfs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DiskGo;

public sealed partial class MainPage
{
    private void LoadTargets()
    {
        TargetMenu.Items.Clear();
        IReadOnlyList<ScanTarget> all;
        try
        {
            all = ScanTargetList.Enumerate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            all = [];
            StatusText.Text = "无法读取磁盘列表，仍可直接选择文件夹。";
        }

        if (all.Count == 0)
        {
            TargetMenu.Items.Add(new MenuFlyoutItem { Text = "没有找到磁盘", IsEnabled = false });
        }
        else
        {
            void AddGroup(string title, ScanTargetKind kind, string glyph)
            {
                var group = all.Where(t => t.Kind == kind).ToList();
                if (group.Count == 0)
                    return;

                TargetMenu.Items.Add(new MenuFlyoutItem
                {
                    Text = title,
                    IsEnabled = false,
                });

                foreach (ScanTarget target in group)
                {
                    string cap = "";
                    string trimmed = target.Path.TrimEnd(Path.DirectorySeparatorChar);
                    if (kind == ScanTargetKind.Volume && trimmed.Length == 2 && trimmed[1] == ':')
                    {
                        try
                        {
                            var info = new DriveInfo(target.Path);
                            if (info.IsReady)
                                cap = $"  ({Format(info.TotalSize - info.AvailableFreeSpace)} / {Format(info.TotalSize)})";
                        }
                        catch
                        {
                        }
                    }

                    var item = new MenuFlyoutItem
                    {
                        Text = target.Label + cap,
                        Tag = target.Path,
                        Icon = new FontIcon { Glyph = glyph },
                    };
                    item.Click += (_, _) => Choose(new[] { target.Path }, target.Label, glyph);
                    TargetMenu.Items.Add(item);
                }
            }

            AddGroup("本地与可移动磁盘", ScanTargetKind.Volume, "\uEDA2");
            AddGroup("网络位置", ScanTargetKind.Network, "\uE8CE");
            AddGroup("便携设备", ScanTargetKind.PortableDevice, "\uE8EA");
            AddGroup("映射文件夹", ScanTargetKind.Folder, "\uE8B7");
        }

        TargetMenu.Items.Add(new MenuFlyoutSeparator());

        var pickFolderItem = new MenuFlyoutItem
        {
            Text = "选择文件夹...",
            Icon = new FontIcon { Glyph = "\uE8B7" },
        };
        pickFolderItem.Click += async (_, _) => await PickFolder();
        TargetMenu.Items.Add(pickFolderItem);

        var pickMultiItem = new MenuFlyoutItem
        {
            Text = "选择多个位置...",
            Icon = new FontIcon { Glyph = "\uE762" },
        };
        pickMultiItem.Click += async (_, _) => await PickMultipleTargets();
        TargetMenu.Items.Add(pickMultiItem);

        var pickImportItem = new MenuFlyoutItem
        {
            Text = "打开 CSV / TSV / MFT 文件...",
            Icon = new FontIcon { Glyph = "\uE8E5" },
        };
        pickImportItem.Click += async (_, _) => await PickImport();
        TargetMenu.Items.Add(pickImportItem);
    }

    private void Choose(IReadOnlyList<string> paths, string label, string glyph = "\uEDA2")
    {
        _targets.Clear();
        _targets.AddRange(paths);
        TargetLabel.Text = label;
        TargetIcon.Glyph = glyph;
        ScanButton.IsEnabled = _targets.Count > 0;
        if (_settings.ScanOnSelect)
            _ = RunScan();
    }

    private async Task PickFolder()
    {
        try
        {
            string? path = await ShellHelpers.PickFolderAsync();
            if (path is not null)
                Choose(new[] { path }, path, "\uE8B7");
        }
        catch (COMException ex)
        {
            ShowError("无法选择文件夹", ex.Message);
        }
    }

    private async Task PickMultipleTargets()
    {
        List<string>? selected = await Dialogs.ShowMultiTargetAsync(XamlRoot, _targets);
        if (selected is null || selected.Count == 0)
            return;

        string label = selected.Count == 1 ? selected[0] : $"{selected.Count} 个扫描位置";
        Choose(selected, label, "\uE762");
    }

    private async Task PickImport()
    {
        string? path;
        try
        {
            path = await ShellHelpers.PickOpenFileAsync(".csv", ".tsv", ".mft");
        }
        catch (COMException ex)
        {
            ShowError("无法选择文件", ex.Message);
            return;
        }

        if (path is null)
            return;

        await OpenImportedFile(path, Path.GetFileName(path));
    }

    private async Task OpenImportedFile(string filePath, string displayName)
    {
        CancelBackgroundQueries();
        StopMonitor();
        InlineBanner.IsOpen = false;
        SetScanningUi(true, $"正在导入 {displayName}...");
        try
        {
            ScanResult result = await Task.Run(() => filePath.EndsWith(".mft", StringComparison.OrdinalIgnoreCase)
                ? MftDump.Read(filePath, @"C:\")
                : CsvImporter.Import(filePath));
            TargetLabel.Text = displayName;
            TargetIcon.Glyph = "\uE8E5";
            ShowResult(result, $"已导入 {displayName}，共 {result.Files.Count:N0} 个文件。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError("导入失败", ex.Message);
        }
        finally
        {
            SetScanningUi(false);
        }
    }

    private void PageDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "使用 DiskGo 扫描";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void PageDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0)
            return;

        if (items.Count == 1 && items[0] is StorageFile file &&
            (file.FileType.Equals(".csv", StringComparison.OrdinalIgnoreCase)
             || file.FileType.Equals(".tsv", StringComparison.OrdinalIgnoreCase)
             || file.FileType.Equals(".mft", StringComparison.OrdinalIgnoreCase)))
        {
            await OpenImportedFile(file.Path, file.Name);
            return;
        }

        var folders = items.OfType<StorageFolder>().Select(f => f.Path).ToList();
        if (folders.Count == 0)
            return;

        string label = folders.Count == 1 ? folders[0] : $"{folders.Count} 个拖入的文件夹";
        _targets.Clear();
        _targets.AddRange(folders);
        TargetLabel.Text = label;
        TargetIcon.Glyph = "\uE8B7";
        await RunScan();
    }

    private async void ScanClicked(object sender, RoutedEventArgs e)
    {
        if (_scan is not null)
        {
            await _scan.CancelAsync();
            return;
        }

        await RunScan();
    }

    private async void ApplyFilterScanClicked(object sender, RoutedEventArgs e) => await RunScan();

    private void ClearFilterClicked(object sender, RoutedEventArgs e)
    {
        IncludeBox.Text = "";
        ExcludeBox.Text = "";
        FullPathBox.IsChecked = false;
        RegexBox.IsChecked = false;
    }

    private async void ClearFilterAndRescanClicked(object sender, RoutedEventArgs e)
    {
        ClearFilterClicked(sender, e);
        await RunScan();
    }

    private async Task RunScan()
    {
        if (_targets.Count == 0)
        {
            ShowError("尚未选择扫描目标", "请先从左上角下拉列表中选择一个磁盘或文件夹。");
            return;
        }

        CancelBackgroundQueries();
        StopMonitor();
        InlineBanner.IsOpen = false;
        OpenExportFolderButton.Visibility = Visibility.Collapsed;

        _scan = new CancellationTokenSource();
        SetScanningUi(true, "正在准备扫描...");

        var options = BuildScanOptions(new Progress<ScanProgress>(UpdateScanProgress));

        try
        {
            ScanResult result = await Task.Run(() => DiskScanner.Scan(options, _scan.Token), _scan.Token);
            ShowResult(result, Summary(result));
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);

            if (_settings.MonitorChanges && CanMonitorCurrentTargets())
                StartMonitor(options, result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "扫描已取消。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("扫描失败", ex.Message, allowRetry: true);
        }
        finally
        {
            _scan.Dispose();
            _scan = null;
            SetScanningUi(false);
        }
    }

    private ScanOptions BuildScanOptions(IProgress<ScanProgress>? progress)
        => new()
        {
            Paths = _targets.ToArray(),
            IncludeFilter = IncludeBox.Text,
            ExcludeFilter = ExcludeBox.Text,
            FilterFullPath = FullPathBox.IsChecked == true,
            UseRegex = RegexBox.IsChecked == true,
            PreferMftForFolders = _settings.PreferMftForFolders,
            AllowSlowFallback = _settings.AllowSlowFallback,
            WalkThreads = _settings.WalkThreads,
            MftRead = _settings.MftRead,
            IncludeNtfsMetadata = _settings.IncludeNtfsMetadata,
            IncludeHidden = !_settings.IgnoreHidden,
            ExcludedAttributes = _settings.ExcludedAttributes,
            ShowNewestModified = _settings.ShowNewestModified,
            FileGroupMinimum = _settings.FileGroupMinimum,
            Duplicates = SelectedDuplicates(),
            Progress = progress,
        };

    private bool CanMonitorCurrentTargets()
    {
        if (_targets.Count == 0)
            return false;
        foreach (string target in _targets)
        {
            if (PortableDevices.IsDevicePath(target))
                return false;
            string trimmed = target.TrimEnd(Path.DirectorySeparatorChar);
            if (trimmed.Length == 2 && trimmed[1] == ':')
                return false;
        }
        return true;
    }

    private void StartMonitor(ScanOptions options, ScanResult initialResult)
    {
        try
        {
            var monitorOptions = new ScanOptions
            {
                Paths = options.Paths,
                IncludeFilter = options.IncludeFilter,
                ExcludeFilter = options.ExcludeFilter,
                FilterFullPath = options.FilterFullPath,
                UseRegex = options.UseRegex,
                PreferMftForFolders = options.PreferMftForFolders,
                AllowSlowFallback = options.AllowSlowFallback,
                WalkThreads = options.WalkThreads,
                MftRead = options.MftRead,
                IncludeNtfsMetadata = options.IncludeNtfsMetadata,
                IncludeHidden = options.IncludeHidden,
                ExcludedAttributes = options.ExcludedAttributes,
                ShowNewestModified = options.ShowNewestModified,
                FileGroupMinimum = options.FileGroupMinimum,
                Duplicates = options.Duplicates,
            };

            _monitor = new ScanMonitor(monitorOptions);
            _monitor.Updated += updated => DispatcherQueue.TryEnqueue(() =>
            {
                string currentPath = _selected?.FullPath ?? updated.Root.FullPath;
                ShowResult(updated, $"检测到变化，已自动更新 · {Summary(updated)}", currentPath);
            });
            _monitor.Failed += ex => DispatcherQueue.TryEnqueue(() =>
            {
                StatusText.Text = "自动刷新失败: " + ex.Message;
            });

            _ = Task.Run(() =>
            {
                try
                {
                    _monitor?.Start(initialResult);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_monitor is { IsMonitoring: true })
                            MonitorBadge.Visibility = Visibility.Visible;
                    });
                }
                catch
                {
                    DispatcherQueue.TryEnqueue(StopMonitor);
                }
            });
        }
        catch
        {
            StopMonitor();
        }
    }

    private void StopMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
        MonitorBadge.Visibility = Visibility.Collapsed;
    }

    private void UpdateScanProgress(ScanProgress progress)
    {
        string stage = progress.Stage switch
        {
            ScanStage.Preparing => "正在准备...",
            ScanStage.ReadingMft => "正在高速读取 NTFS 主文件表 (MFT)",
            ScanStage.WalkingDirectories => "正在遍历目录",
            ScanStage.BuildingTree => "正在构建目录树与统计信息",
            ScanStage.FindingDuplicates => "正在检测重复文件",
            ScanStage.Completed => "完成",
            _ => "正在扫描...",
        };

        string counts = progress.Stage switch
        {
            ScanStage.ReadingMft => progress.Detail,
            ScanStage.WalkingDirectories or ScanStage.BuildingTree or ScanStage.FindingDuplicates =>
                $"{progress.Files:N0} 个文件 · {progress.Folders:N0} 个文件夹",
            _ => progress.Detail,
        };

        ScanStageText.Text = stage;
        ScanCountsText.Text = counts;
        StatusText.Text = string.IsNullOrEmpty(counts) ? stage : $"{stage} ({counts})";
    }

    private void SetScanningUi(bool scanning, string? initialStage = null)
    {
        if (scanning)
        {
            ScanButtonText.Text = "取消";
            ScanIcon.Glyph = "\uE711";
            if (!string.IsNullOrEmpty(initialStage))
                ScanStageText.Text = initialStage;
            ScanCountsText.Text = "";

            if (_result is null)
            {
                EmptyState.Visibility = Visibility.Collapsed;
                NoResultsState.Visibility = Visibility.Collapsed;
                Results.Visibility = Visibility.Collapsed;
                ScanningState.Visibility = Visibility.Visible;
                ScanRing.IsActive = true;
            }
            else
            {
                RescanBar.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ScanButtonText.Text = "扫描";
            ScanIcon.Glyph = "\uE768";
            ScanRing.IsActive = false;
            ScanningState.Visibility = Visibility.Collapsed;
            RescanBar.Visibility = Visibility.Collapsed;

            if (_result is null)
            {
                EmptyState.Visibility = Visibility.Visible;
                Results.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowError(string title, string message, bool allowRetry = false)
    {
        InlineBanner.Title = title;
        InlineBanner.Message = message;
        InlineBanner.Severity = InfoBarSeverity.Error;
        if (allowRetry)
        {
            var retry = new Button { Content = "再试一次" };
            retry.Click += async (_, _) =>
            {
                InlineBanner.IsOpen = false;
                await RunScan();
            };
            InlineBanner.ActionButton = retry;
        }
        else
        {
            InlineBanner.ActionButton = null;
        }
        InlineBanner.IsOpen = true;
        StatusText.Text = message;
    }
}
