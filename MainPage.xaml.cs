using System.Collections.ObjectModel;
using DiskGo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DiskGo;

public sealed partial class MainPage : Page
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Treemap _treemap = new();
    private readonly BatchObservableCollection<TreeRow> _tree = [];
    private readonly BatchObservableCollection<TypeRow> _types = [];
    private readonly FileColumnState _fileColumns = new();
    private readonly VirtualFileList _files;
    private readonly List<string> _targets = [];
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private ScanResult? _result;
    private FolderNode? _selected;
    private string? _typeFilter;
    private CancellationTokenSource? _scan;
    private CancellationTokenSource? _typesCts;
    private CancellationTokenSource? _filesCts;
    private CancellationTokenSource? _dupCts;
    private ScanMonitor? _monitor;
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = [];

    private TreeSortColumn _treeSort = TreeSortColumn.Size;
    private bool _treeSortDesc = true;
    private FileSortColumn _fileSort = FileSortColumn.Size;
    private bool _fileSortDesc = true;

    private string? _lastExportFolder;
    private bool _userToggledTypePane;

    // Splitters state
    private bool _draggingType;
    private bool _draggingTreeSplit;
    private bool _draggingMapSplit;
    private double _dragStart;
    private double _dragSize;
    private double _dragSizeAlt;

    public MainPage()
    {
        InitializeComponent();
        _files = new VirtualFileList(_fileColumns);
        TreeList.ItemsSource = _tree;
        TypeList.ItemsSource = _types;
        FileList.ItemsSource = _files;
        DuplicateBox.SelectedIndex = 0;

        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ShowFiles();
        };

        Loaded += OnLoaded;
        SizeChanged += OnPageSizeChanged;
        Unloaded += (_, _) =>
        {
            CancelBackgroundQueries();
            StopMonitor();
        };
    }

    private void CancelBackgroundQueries()
    {
        CancelCts(ref _typesCts);
        CancelCts(ref _filesCts);
        CancelCts(ref _dupCts);
    }

    private static CancellationToken ResetCts(ref CancellationTokenSource? slot)
    {
        CancelCts(ref slot);
        slot = new CancellationTokenSource();
        return slot.Token;
    }

    private static void CancelCts(ref CancellationTokenSource? slot)
    {
        CancellationTokenSource? old = slot;
        slot = null;
        if (old is null)
            return;

        try
        {
            old.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        old.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncSettingsToUi();
        BuildColumnMenus();
        BuildUnitMenu();
        LoadTargets();

        if (_targets.Count == 0 && TargetMenu.Items.Count > 0)
        {
            // Pre-select the system drive if present without auto-scanning until user clicks or chooses
            foreach (MenuFlyoutItemBase item in TargetMenu.Items)
            {
                if (item is MenuFlyoutItem mfi && mfi.Tag is string path && path.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
                {
                    _targets.Add(path);
                    TargetLabel.Text = mfi.Text;
                    break;
                }
            }
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_userToggledTypePane && e.NewSize.Width > 0)
        {
            bool wideEnough = e.NewSize.Width >= 1100;
            ApplyTypePaneVisibility(wideEnough && _settings.ShowTypePane);
        }
    }

    private void SyncSettingsToUi()
    {
        TypeColumn.Width = new GridLength(Math.Clamp(_settings.TypePaneWidth, 200, 480));
        TypePaneToggle.IsChecked = _settings.ShowTypePane;
        ApplyTypePaneVisibility(_settings.ShowTypePane);
        ApplyFilePaneVisibility(_settings.ShowFilePane, save: false);

        MapNamesMenuItem.IsChecked = _settings.TreemapNames;
        MapFreeMenuItem.IsChecked = _settings.TreemapFreeSpace;
        MapAllocMenuItem.IsChecked = _settings.TreemapByAllocated;

        _fileColumns.ShowAllocated = _settings.FileShowAllocated;
        _fileColumns.ShowPercent = _settings.FileShowPercent;
        _fileColumns.ShowPath = _settings.FileShowPath;
        _fileColumns.ShowModified = _settings.FileShowModified;
        _fileColumns.ShowAttributes = _settings.FileShowAttributes;
        _fileColumns.SizeUnit = _settings.SizeUnit;
        _fileColumns.BinaryUnits = _settings.BinaryUnits;
        _fileColumns.DecimalPlaces = _settings.DecimalPlaces;
        _fileColumns.Dark = ActualTheme == ElementTheme.Dark;

        ApplyTreeColumnWidths();
        ApplyFileColumnWidths();
        UpdateUnitBadge();
    }

    private string Format(long bytes) => SizeText.Format(bytes, _settings.SizeUnit, _settings.BinaryUnits, _settings.DecimalPlaces);

    private static string Summary(ScanResult result)
    {
        string method = result.Method == ScanMethod.MasterFileTable ? "MFT" : "目录遍历";
        string fallback = result.FellBackFromMft ? " (MFT 读取失败，已退回目录遍历)" : "";
        return $"完成 · 耗时 {result.Elapsed.TotalSeconds:0.00}s · {method}{fallback} · {result.Files.Count:N0} 个文件 · {result.Root.FolderCount:N0} 个文件夹";
    }
}
