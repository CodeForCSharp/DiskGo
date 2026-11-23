using System.Globalization;
using System.Runtime.InteropServices;
using DiskGo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IoFileAttributes = System.IO.FileAttributes;

namespace DiskGo;

public sealed record ExportDialogResult(
    bool IsTypesExport,
    string FileName,
    CsvExportOptions Options);

public sealed record TreemapPngOptions(
    int Width,
    int Height,
    bool Monochrome,
    bool ShowNames,
    bool ShowFreeSpace,
    bool ByAllocated);

internal static class Dialogs
{
    public static async Task<ExportDialogResult?> ShowExportAsync(
        XamlRoot xamlRoot,
        ScanResult result,
        IReadOnlyList<string> selectedPaths,
        bool defaultSelectionOnly = false)
    {
        bool hasSelection = selectedPaths.Count > 0;
        bool isMft = result.Method == ScanMethod.MasterFileTable;

        var modeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        modeBox.Items.Add(new ComboBoxItem { Content = "文件与文件夹明细 (CSV / TSV)", Tag = "tree" });
        modeBox.Items.Add(new ComboBoxItem { Content = "文件类型汇总 (CSV)", Tag = "types" });
        modeBox.SelectedIndex = 0;

        var scopeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = hasSelection };
        scopeBox.Items.Add(new ComboBoxItem { Content = "整个扫描结果", Tag = "all" });
        scopeBox.Items.Add(new ComboBoxItem { Content = $"仅当前选中项 ({selectedPaths.Count:N0} 项)", Tag = "selected" });
        scopeBox.SelectedIndex = hasSelection && defaultSelectionOnly ? 1 : 0;

        var formatBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        formatBox.Items.Add(new ComboBoxItem { Content = "CSV (逗号分隔)", Tag = "csv" });
        formatBox.Items.Add(new ComboBoxItem { Content = "TSV (制表符分隔)", Tag = "tsv" });
        formatBox.SelectedIndex = 0;

        var nameBox = new TextBox
        {
            Header = "默认文件名",
            Text = "DiskGo_%d_%t",
            PlaceholderText = "DiskGo_%d_%t",
        };
        var nameHint = new TextBlock
        {
            Text = "提示: %d 将替换为日期，%t 将替换为时间。",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var includeFiles = new CheckBox { Content = "包含文件", IsChecked = true };
        var includeFolders = new CheckBox { Content = "包含文件夹", IsChecked = true };
        var duplicatesOnly = new CheckBox { Content = "仅导出重复文件", IsChecked = false };

        var allDates = new CheckBox { Content = "创建与访问时间", IsChecked = true };
        var ownSize = new CheckBox { Content = "文件夹自身大小", IsChecked = true };
        var splitPath = new CheckBox { Content = "拆分路径 (盘符/目录/名称/扩展名)", IsChecked = false };
        var driveCap = new CheckBox { Content = "磁盘容量信息", IsChecked = result.Drive.IsKnown };
        var pctParent = new CheckBox { Content = "占上级百分比", IsChecked = true };
        var pctDrive = new CheckBox { Content = "占全盘百分比", IsChecked = result.Drive.IsKnown };
        var mftRec = new CheckBox { Content = "MFT 记录号", IsChecked = isMft, IsEnabled = isMft };
        var parentRec = new CheckBox { Content = "父记录号", IsChecked = false, IsEnabled = isMft };
        var shortName = new CheckBox { Content = "DOS 8.3 短名称", IsChecked = false };
        var useUtc = new CheckBox { Content = "时间使用 UTC", IsChecked = false };
        var maxDepthBox = new NumberBox
        {
            Header = "最大层级 (0 为不限)",
            Value = 0,
            Minimum = 0,
            Maximum = 256,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var columnsGrid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        CheckBox[] colChecks = [allDates, ownSize, splitPath, driveCap, pctParent, pctDrive, mftRec, parentRec, shortName, useUtc];
        for (int i = 0; i < (colChecks.Length + 1) / 2; i++)
            columnsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < colChecks.Length; i++)
        {
            Grid.SetRow(colChecks[i], i / 2);
            Grid.SetColumn(colChecks[i], i % 2);
            columnsGrid.Children.Add(colChecks[i]);
        }

        var treeOptionsPanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "范围与格式", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    },
                    Children =
                    {
                        WithCol(scopeBox, 0),
                        WithCol(formatBox, 1),
                    },
                },
                new TextBlock { Text = "包含内容", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children = { includeFiles, includeFolders, duplicatesOnly },
                },
                new TextBlock { Text = "导出列", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                columnsGrid,
                maxDepthBox,
            },
        };

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 460,
            Children =
            {
                new TextBlock { Text = "导出类型", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                modeBox,
                treeOptionsPanel,
                nameBox,
                nameHint,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "导出扫描结果",
            PrimaryButtonText = "选择保存位置...",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = content,
            },
        };

        void UpdateValidity()
        {
            bool isTypes = modeBox.SelectedIndex == 1;
            treeOptionsPanel.Visibility = isTypes ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = isTypes || includeFiles.IsChecked == true || includeFolders.IsChecked == true;
        }

        modeBox.SelectionChanged += (_, _) => UpdateValidity();
        includeFiles.Checked += (_, _) => UpdateValidity();
        includeFiles.Unchecked += (_, _) => UpdateValidity();
        includeFolders.Checked += (_, _) => UpdateValidity();
        includeFolders.Unchecked += (_, _) => UpdateValidity();
        UpdateValidity();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        DateTime now = DateTime.Now;
        string suggested = (string.IsNullOrWhiteSpace(nameBox.Text) ? "DiskGo_%d_%t" : nameBox.Text.Trim())
            .Replace("%d", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("%t", now.ToString("HHmmss", CultureInfo.InvariantCulture));

        bool exportTypes = modeBox.SelectedIndex == 1;
        bool tsv = !exportTypes && formatBox.SelectedIndex == 1;

        var options = new CsvExportOptions
        {
            Format = tsv ? ExportFormat.Tsv : ExportFormat.Csv,
            IncludeFiles = includeFiles.IsChecked == true,
            IncludeFolders = includeFolders.IsChecked == true,
            DuplicatesOnly = duplicatesOnly.IsChecked == true,
            IncludeAllDates = allDates.IsChecked == true,
            IncludeOwnSize = ownSize.IsChecked == true,
            SplitPath = splitPath.IsChecked == true,
            IncludeDriveCapacity = driveCap.IsChecked == true,
            IncludePercentOfParent = pctParent.IsChecked == true,
            IncludePercentOfDrive = pctDrive.IsChecked == true,
            IncludeMftRecord = mftRec.IsChecked == true && isMft,
            IncludeParentRecord = parentRec.IsChecked == true && isMft,
            IncludeShortName = shortName.IsChecked == true,
            UseUtc = useUtc.IsChecked == true,
            MaxDepth = double.IsNaN(maxDepthBox.Value) ? 0 : Math.Max(0, (int)maxDepthBox.Value),
            OnlyPaths = scopeBox.SelectedIndex == 1 && hasSelection ? selectedPaths.ToArray() : [],
        };

        return new ExportDialogResult(exportTypes, suggested, options);
    }

    public static async Task<TreemapPngOptions?> ShowTreemapExportAsync(XamlRoot xamlRoot, AppSettings settings)
    {
        var resBox = new ComboBox { Header = "图像分辨率", HorizontalAlignment = HorizontalAlignment.Stretch };
        resBox.Items.Add(new ComboBoxItem { Content = "1280 × 720 (HD)", Tag = "1280x720" });
        resBox.Items.Add(new ComboBoxItem { Content = "1920 × 1080 (Full HD)", Tag = "1920x1080" });
        resBox.Items.Add(new ComboBoxItem { Content = "2560 × 1440 (2K QHD)", Tag = "2560x1440" });
        resBox.Items.Add(new ComboBoxItem { Content = "3840 × 2160 (4K UHD)", Tag = "3840x2160" });
        resBox.SelectedIndex = settings.PngWidth switch
        {
            1280 => 0,
            2560 => 2,
            3840 => 3,
            _ => 1,
        };

        var monoCheck = new CheckBox { Content = "单色灰度模式", IsChecked = false };
        var namesCheck = new CheckBox { Content = "显示文件与文件夹名称", IsChecked = settings.TreemapNames };
        var freeCheck = new CheckBox { Content = "包含磁盘空闲空间", IsChecked = settings.TreemapFreeSpace };
        var allocCheck = new CheckBox { Content = "按磁盘占用大小计算面积", IsChecked = settings.TreemapByAllocated };

        var dialog = new ContentDialog
        {
            Title = "导出矩形图为 PNG",
            PrimaryButtonText = "保存...",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 340,
                Children = { resBox, namesCheck, freeCheck, allocCheck, monoCheck },
            },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        string tag = ((ComboBoxItem)resBox.SelectedItem).Tag as string ?? "1920x1080";
        string[] parts = tag.Split('x');
        int w = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int h = int.Parse(parts[1], CultureInfo.InvariantCulture);
        settings.PngWidth = w;
        settings.PngHeight = h;
        settings.Save();

        return new TreemapPngOptions(
            w,
            h,
            monoCheck.IsChecked == true,
            namesCheck.IsChecked == true,
            freeCheck.IsChecked == true,
            allocCheck.IsChecked == true);
    }

    public static async Task<List<string>?> ShowMultiTargetAsync(XamlRoot xamlRoot, IReadOnlyList<string> currentTargets)
    {
        var checks = new List<(CheckBox Box, string Path)>();
        var customFolders = new List<string>();
        var listPanel = new StackPanel { Spacing = 6 };
        var customList = new ListView { MaxHeight = 160, SelectionMode = ListViewSelectionMode.Single };

        var existingSet = new HashSet<string>(currentTargets, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ScanTarget> knownTargets;
        try
        {
            knownTargets = ScanTargetList.Enumerate();
        }
        catch
        {
            knownTargets = [];
        }

        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ScanTarget t in knownTargets)
        {
            knownPaths.Add(t.Path);
            var cb = new CheckBox
            {
                Content = $"{t.Label}  ({t.Path})",
                IsChecked = existingSet.Contains(t.Path),
            };
            checks.Add((cb, t.Path));
            listPanel.Children.Add(cb);
        }

        foreach (string p in currentTargets)
        {
            if (!knownPaths.Contains(p))
                customFolders.Add(p);
        }

        void RefreshCustomList()
        {
            customList.ItemsSource = null;
            customList.ItemsSource = customFolders.ToList();
        }
        RefreshCustomList();

        var addBtn = new Button { Content = "添加文件夹..." };
        var removeBtn = new Button { Content = "移除选中", IsEnabled = false };
        var errorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsOpen = false,
            IsClosable = false,
        };
        customList.SelectionChanged += (_, _) => removeBtn.IsEnabled = customList.SelectedItem is string;

        addBtn.Click += async (_, _) =>
        {
            try
            {
                string? path = await ShellHelpers.PickFolderAsync();
                if (path is not null && !customFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    customFolders.Add(path);
                    RefreshCustomList();
                    errorBar.IsOpen = false;
                }
            }
            catch (COMException ex)
            {
                errorBar.Message = ex.Message;
                errorBar.IsOpen = true;
            }
        };

        removeBtn.Click += (_, _) =>
        {
            if (customList.SelectedItem is string path)
            {
                customFolders.Remove(path);
                RefreshCustomList();
            }
        };

        var dialog = new ContentDialog
        {
            Title = "选择多个扫描位置",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 440,
                Children =
                {
                    new TextBlock { Text = "可用磁盘与设备", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new ScrollViewer { MaxHeight = 180, Content = listPanel },
                    new TextBlock { Text = "自定义文件夹", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    customList,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addBtn, removeBtn } },
                    errorBar,
                },
            },
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var selected = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Path).Concat(customFolders).ToList();
            if (selected.Count == 0)
            {
                errorBar.Message = "请至少选择一个驱动器或文件夹。";
                errorBar.IsOpen = true;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        return checks.Where(c => c.Box.IsChecked == true).Select(c => c.Path).Concat(customFolders).ToList();
    }

    public static async Task<bool> ShowSettingsAsync(XamlRoot xamlRoot, AppSettings settings, bool isFolderTarget)
    {
        var alwaysAdmin = new ToggleSwitch { Header = "总是以管理员身份运行", IsOn = settings.AlwaysAdministrator };
        var preferMft = new ToggleSwitch { Header = "扫描文件夹时也读取 MFT", IsOn = settings.PreferMftForFolders };
        var preferMftNote = SecondaryNote("大文件夹通常更快，小文件夹通常更慢。");
        var slowFallback = new ToggleSwitch { Header = "无法读取 MFT 时改用目录遍历", IsOn = settings.AllowSlowFallback };
        var threadsBox = new NumberBox
        {
            Header = "目录遍历线程数 (0 为自动)",
            Value = settings.WalkThreads,
            Minimum = 0,
            Maximum = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var mftReadBox = new ComboBox { Header = "MFT 读取策略", HorizontalAlignment = HorizontalAlignment.Stretch };
        mftReadBox.Items.Add(new ComboBoxItem { Content = "自动 (SSD 使用重叠 I/O)", Tag = MftReadMode.Auto });
        mftReadBox.Items.Add(new ComboBoxItem { Content = "始终重叠读取 (Overlapped)", Tag = MftReadMode.Overlapped });
        mftReadBox.Items.Add(new ComboBoxItem { Content = "同步顺序读取 (Synchronous)", Tag = MftReadMode.Synchronous });
        mftReadBox.SelectedIndex = (int)settings.MftRead;

        var ntfsMeta = new ToggleSwitch { Header = "计入 NTFS 元数据文件 ($MFT、$Bitmap 等)", IsOn = settings.IncludeNtfsMetadata };
        var ignoreHidden = new ToggleSwitch { Header = "忽略隐藏文件", IsOn = settings.IgnoreHidden };
        var attrSystem = new CheckBox { Content = "系统 (System)", IsChecked = settings.ExcludedAttributes.HasFlag(IoFileAttributes.System) };
        var attrTemp = new CheckBox { Content = "临时 (Temporary)", IsChecked = settings.ExcludedAttributes.HasFlag(IoFileAttributes.Temporary) };
        var attrReadOnly = new CheckBox { Content = "只读 (ReadOnly)", IsChecked = settings.ExcludedAttributes.HasFlag(IoFileAttributes.ReadOnly) };
        var attrOffline = new CheckBox { Content = "脱机 (Offline)", IsChecked = settings.ExcludedAttributes.HasFlag(IoFileAttributes.Offline) };

        var monitorSwitch = new ToggleSwitch
        {
            Header = "监视文件夹变化并自动重新扫描",
            IsOn = settings.MonitorChanges,
            IsEnabled = isFolderTarget,
        };
        var monitorNote = SecondaryNote(isFolderTarget ? "检测到文件夹内文件变动后自动增量更新。" : "仅对具体文件夹有效，全盘扫描不支持文件监视。");

        var scanTab = new ScrollViewer
        {
            MaxHeight = 420,
            Content = new StackPanel
            {
                Spacing = 10,
                Padding = new Thickness(0, 8, 12, 8),
                Children =
                {
                    alwaysAdmin,
                    preferMft,
                    preferMftNote,
                    slowFallback,
                    mftReadBox,
                    threadsBox,
                    ntfsMeta,
                    ignoreHidden,
                    new TextBlock { Text = "按属性排除", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { attrSystem, attrTemp, attrReadOnly, attrOffline } },
                    monitorSwitch,
                    monitorNote,
                },
            },
        };

        var unitBox = new ComboBox { Header = "大小单位", HorizontalAlignment = HorizontalAlignment.Stretch };
        unitBox.Items.Add(new ComboBoxItem { Content = "自动选择", Tag = SizeUnit.Automatic });
        unitBox.Items.Add(new ComboBoxItem { Content = "字节 (B)", Tag = SizeUnit.Bytes });
        unitBox.Items.Add(new ComboBoxItem { Content = "千字节 (KB / KiB)", Tag = SizeUnit.Kilobytes });
        unitBox.Items.Add(new ComboBoxItem { Content = "兆字节 (MB / MiB)", Tag = SizeUnit.Megabytes });
        unitBox.Items.Add(new ComboBoxItem { Content = "吉字节 (GB / GiB)", Tag = SizeUnit.Gigabytes });
        unitBox.Items.Add(new ComboBoxItem { Content = "太字节 (TB / TiB)", Tag = SizeUnit.Terabytes });
        unitBox.SelectedIndex = (int)settings.SizeUnit;

        var binarySwitch = new ToggleSwitch { Header = "使用二进制进制 (1024，如 KiB / GiB)", IsOn = settings.BinaryUnits };
        var decimalsBox = new NumberBox
        {
            Header = "小数位数 (0 - 2)",
            Value = settings.DecimalPlaces,
            Minimum = 0,
            Maximum = 2,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var newestModified = new ToggleSwitch { Header = "文件夹修改时间显示内部最新文件时间", IsOn = settings.ShowNewestModified };
        var groupMinBox = new NumberBox
        {
            Header = "小文件合并阈值 (字节，0 为不合并)",
            Value = settings.FileGroupMinimum,
            Minimum = 0,
            Maximum = 1_000_000_000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var treemapNames = new ToggleSwitch { Header = "矩形图显示文件与文件夹名称", IsOn = settings.TreemapNames };
        var treemapFree = new ToggleSwitch { Header = "矩形图显示磁盘空闲空间", IsOn = settings.TreemapFreeSpace };
        var treemapAlloc = new ToggleSwitch { Header = "矩形图按占用空间 (Allocated) 绘图", IsOn = settings.TreemapByAllocated };

        var displayTab = new ScrollViewer
        {
            MaxHeight = 420,
            Content = new StackPanel
            {
                Spacing = 10,
                Padding = new Thickness(0, 8, 12, 8),
                Children =
                {
                    unitBox,
                    binarySwitch,
                    decimalsBox,
                    newestModified,
                    groupMinBox,
                    treemapNames,
                    treemapFree,
                    treemapAlloc,
                },
            },
        };

        var openDbl = new ToggleSwitch { Header = "双击文件时用系统默认程序打开", IsOn = settings.OpenOnDoubleClick };
        var allowRename = new ToggleSwitch { Header = "允许在列表中按 F2 重命名", IsOn = settings.AllowRename };
        var scanOnSelect = new ToggleSwitch { Header = "选中目标后立刻开始扫描", IsOn = settings.ScanOnSelect };

        var ctxCard = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "资源管理器右键菜单", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    SecondaryNote("可在安装包中注册「用 DiskGo 分析」右键菜单（支持驱动器、文件夹与文件夹背景，参见 ExplorerContextMenu.md）。"),
                },
            },
        };

        var behaviorTab = new ScrollViewer
        {
            MaxHeight = 420,
            Content = new StackPanel
            {
                Spacing = 10,
                Padding = new Thickness(0, 8, 12, 8),
                Children = { openDbl, allowRename, scanOnSelect, ctxCard },
            },
        };

        var pivot = new Pivot
        {
            MinWidth = 480,
            Items =
            {
                new PivotItem { Header = "扫描", Content = scanTab },
                new PivotItem { Header = "显示", Content = displayTab },
                new PivotItem { Header = "行为", Content = behaviorTab },
            },
        };

        var dialog = new ContentDialog
        {
            Title = "设置",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            Content = pivot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;

        settings.AlwaysAdministrator = alwaysAdmin.IsOn;
        settings.PreferMftForFolders = preferMft.IsOn;
        settings.AllowSlowFallback = slowFallback.IsOn;
        settings.MftRead = (MftReadMode)((ComboBoxItem)mftReadBox.SelectedItem).Tag;
        settings.WalkThreads = double.IsNaN(threadsBox.Value) ? 0 : Math.Max(0, (int)threadsBox.Value);
        settings.IncludeNtfsMetadata = ntfsMeta.IsOn;
        settings.IgnoreHidden = ignoreHidden.IsOn;

        IoFileAttributes excluded = 0;
        if (attrSystem.IsChecked == true) excluded |= IoFileAttributes.System;
        if (attrTemp.IsChecked == true) excluded |= IoFileAttributes.Temporary;
        if (attrReadOnly.IsChecked == true) excluded |= IoFileAttributes.ReadOnly;
        if (attrOffline.IsChecked == true) excluded |= IoFileAttributes.Offline;
        settings.ExcludedAttributes = excluded;
        settings.MonitorChanges = monitorSwitch.IsOn;

        settings.SizeUnit = (SizeUnit)((ComboBoxItem)unitBox.SelectedItem).Tag;
        settings.BinaryUnits = binarySwitch.IsOn;
        settings.DecimalPlaces = double.IsNaN(decimalsBox.Value) ? 1 : Math.Clamp((int)decimalsBox.Value, 0, 2);
        settings.ShowNewestModified = newestModified.IsOn;
        settings.FileGroupMinimum = double.IsNaN(groupMinBox.Value) ? 0 : Math.Max(0, (long)groupMinBox.Value);
        settings.TreemapNames = treemapNames.IsOn;
        settings.TreemapFreeSpace = treemapFree.IsOn;
        settings.TreemapByAllocated = treemapAlloc.IsOn;

        settings.OpenOnDoubleClick = openDbl.IsOn;
        settings.AllowRename = allowRename.IsOn;
        settings.ScanOnSelect = scanOnSelect.IsOn;

        settings.Save();
        return true;
    }

    public static async Task<(char DriveLetter, string FilePath)?> ShowDumpMftAsync(XamlRoot xamlRoot)
    {
        var driveBox = new ComboBox { Header = "选择要导出的 NTFS 卷", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel;
                    driveBox.Items.Add(new ComboBoxItem { Content = $"{label} ({d.Name.TrimEnd('\\')})", Tag = char.ToUpperInvariant(d.Name[0]) });
                }
            }
            catch
            {
            }
        }

        if (driveBox.Items.Count == 0)
        {
            var warn = new ContentDialog
            {
                Title = "导出 MFT",
                Content = "当前没有检测到可读的 NTFS 磁盘。",
                CloseButtonText = "关闭",
                XamlRoot = xamlRoot,
            };
            await warn.ShowAsync();
            return null;
        }

        driveBox.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "导出 MFT 快照 (.mft)",
            PrimaryButtonText = "选择保存位置...",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            Content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 340,
                Children =
                {
                    driveBox,
                    SecondaryNote("将卷的 NTFS 主文件表保存为离线 .mft 文件，之后可随时直接打开分析（需要管理员权限）。"),
                },
            },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        char letter = (char)((ComboBoxItem)driveBox.SelectedItem).Tag;
        string? path = await ShellHelpers.PickSaveFileAsync($"Drive_{letter}_{DateTime.Now:yyyyMMdd}", "DiskGo MFT Dump", ".mft");
        return path is null ? null : (letter, path);
    }

    public static async Task ShowWarningsAsync(XamlRoot xamlRoot, IReadOnlyList<string> warnings)
    {
        string allText = string.Join(Environment.NewLine, warnings);
        var list = new TextBox
        {
            Text = allText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 360,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);

        var dialog = new ContentDialog
        {
            Title = $"有 {warnings.Count:N0} 个项目未能读取",
            PrimaryButtonText = "复制全部",
            CloseButtonText = "关闭",
            XamlRoot = xamlRoot,
            Content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 480,
                Children = { list },
            },
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ShellHelpers.CopyText(allText);
    }

    private static UIElement WithCol(FrameworkElement element, int col)
    {
        Grid.SetColumn(element, col);
        return element;
    }

    private static TextBlock SecondaryNote(string text)
        => new()
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
}
