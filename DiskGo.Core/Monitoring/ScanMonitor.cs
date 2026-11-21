namespace DiskGo.Core;

/// <summary>
/// Keeps a scan current. It scans once, then watches those folders and scans
/// them again, with the same options, after the disk settles.
///
/// A burst of changes (a copy of a thousand files, a build) becomes one rescan
/// rather than one per file. The watch has to be stopped before it is pointed
/// at a different folder: the options are fixed for the life of a run, and
/// <see cref="Start"/> fails while a run is still active.
///
/// Portable devices have no directory Windows can watch, so a selection that is
/// only devices cannot be monitored. Devices mixed in with folders are scanned
/// each time, but only the folders are watched.
/// </summary>
public sealed class ScanMonitor : IDisposable
{
    private readonly ScanOptions _options;
    private readonly bool _includeSubfolders;
    private readonly TimeSpan _settle;
    private readonly object _gate = new();
    private readonly object _scanGate = new();
    private readonly List<DirectoryWatcher> _watchers = [];

    private Timer? _debounce;
    private CancellationTokenSource? _stop;
    private bool _running;
    private bool _dirty;
    private bool _rescanAll;
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="includeSubfolders">Watch changes nested inside the scanned folders, not only their top level.</param>
    /// <param name="settle">How long the disk must stay quiet before a rescan starts. A new change postpones it.</param>
    public ScanMonitor(ScanOptions options, bool includeSubfolders = true, TimeSpan? settle = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (settle is { Ticks: < 0 })
            throw new ArgumentOutOfRangeException(nameof(settle), "The settle time cannot be negative.");

        _options = options;
        _includeSubfolders = includeSubfolders;
        _settle = settle ?? TimeSpan.FromMilliseconds(400);
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsMonitoring => _running;

    /// <summary>The most recent scan, or null before the first one finishes.</summary>
    public ScanResult? Latest { get; private set; }

    /// <summary>
    /// Raised on the thread that ran the rescan, after every automatic scan.
    /// The scan <see cref="Start"/> returns is not raised here. Do not call
    /// <see cref="Rescan"/> from this event; that scan is already the update.
    /// </summary>
    public event Action<ScanResult>? Updated;

    /// <summary>Raised when a rescan fails. The watch stays active.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Raised on the watch thread as changes arrive, before they are folded into a rescan.</summary>
    public event Action<IReadOnlyList<FileChange>>? Changed;

    /// <summary>
    /// Scans once and starts watching. Throws when a watch is already running:
    /// stop it before starting again, including when the folder should change.
    /// </summary>
    public ScanResult Start(CancellationToken cancellationToken = default)
        => StartCore(initialResult: null, cancellationToken);

    /// <summary>
    /// Starts watching using an already completed <paramref name="initialResult"/> so
    /// callers do not trigger a redundant second full scan at startup.
    /// </summary>
    public ScanResult Start(ScanResult initialResult, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialResult);
        return StartCore(initialResult, cancellationToken);
    }

    private ScanResult StartCore(ScanResult? initialResult, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_running)
                throw new InvalidOperationException("The monitor is still active. Stop it before changing what it watches.");

            _running = true;
            _dirty = false;
        }

        try
        {
            string[] watchable = Watchable(ScanTargets.Normalize(_options.Paths));

            lock (_scanGate)
            {
                _stop?.Dispose();
                _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Latest = initialResult ?? DiskScanner.Scan(_options, cancellationToken);
            }

            foreach (string path in watchable)
            {
                var watcher = new DirectoryWatcher(path, _includeSubfolders);
                watcher.Start(OnChanges);
                _watchers.Add(watcher);
            }

            _debounce = new Timer(_ => RescanIfDirty(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return Latest;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Scans the same folders again now, without waiting for the disk to settle.</summary>
    public ScanResult Rescan(CancellationToken cancellationToken = default)
    {
        if (!_running)
            throw new InvalidOperationException("The monitor is not active. Start it before rescanning.");

        return ScanNow(cancellationToken, publish: true);
    }

    /// <summary>
    /// Reads one folder again and swaps that branch of the current result.
    /// The folder has to be inside the scan and not the scan root, which is
    /// replaced by <see cref="Rescan"/> instead. Returns the result afterwards.
    /// </summary>
    public ScanResult RescanFolder(string folder, CancellationToken cancellationToken = default)
    {
        if (!_running)
            throw new InvalidOperationException("The monitor is not active. Start it before rescanning.");
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("There is no folder to rescan.", nameof(folder));

        RescanFolders([folder], cancellationToken);
        return Latest ?? throw new InvalidOperationException("The first scan has not finished.");
    }

    /// <summary>Stops watching. Safe to call more than once, and required before <see cref="Start"/> can run again.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _dirty = false;
            _rescanAll = false;
            _changed.Clear();
            try
            {
                _stop?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Timer? debounce = _debounce;
        _debounce = null;
        debounce?.Dispose();

        foreach (DirectoryWatcher watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    public void Dispose() => Stop();

    private static string[] Watchable(IReadOnlyList<string> targets)
    {
        var folders = new List<string>();
        foreach (string target in targets)
        {
            if (PortableDevices.IsDevicePath(target))
                continue;
            if (!Directory.Exists(target))
                throw new DirectoryNotFoundException($"Cannot monitor {target}, because it is not a directory.");

            folders.Add(target);
        }

        if (folders.Count == 0)
            throw new InvalidOperationException("A portable device cannot be monitored. Scan a drive or a folder instead.");

        return [.. folders];
    }

    private void OnChanges(IReadOnlyList<FileChange> changes)
    {
        if (!_running)
            return;

        try
        {
            Changed?.Invoke(changes);
        }
        catch (Exception)
        {
            // Reporting the change is optional. The rescan is not.
        }

        lock (_gate)
        {
            if (!_running)
                return;
            _dirty = true;
            Note(changes);
        }

        // Each new change pushes the rescan back, so a burst settles into one scan.
        TrySchedule(_settle);
    }

    /// <summary>
    /// Records which folders a burst touched. A rename or an overflow names no
    /// single folder, so the whole scan has to be repeated.
    /// </summary>
    private void Note(IReadOnlyList<FileChange> changes)
    {
        foreach (FileChange change in changes)
        {
            if (change.Kind is FileChangeKind.RenamedFrom or FileChangeKind.RenamedTo or FileChangeKind.Overflow)
            {
                _rescanAll = true;
                return;
            }

            string? folder = Path.GetDirectoryName(change.Path);
            if (string.IsNullOrEmpty(folder))
                continue;

            // A change directly inside a watched root is the root itself. Asking
            // to replace the root is refused, so that one rescans everything.
            if (IsWatchedRoot(folder))
            {
                _rescanAll = true;
                return;
            }

            _changed.Add(folder);
        }
    }

    private bool IsWatchedRoot(string folder)
    {
        string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar);
        foreach (string path in _options.Paths)
        {
            if (path.TrimEnd(Path.DirectorySeparatorChar).Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void RescanIfDirty()
    {
        CancellationToken token;
        string[] changed;
        bool everything;
        lock (_gate)
        {
            if (!_running || !_dirty)
                return;

            _dirty = false;
            everything = _rescanAll;
            changed = [.. _changed];
            _rescanAll = false;
            _changed.Clear();
            token = _stop?.Token ?? CancellationToken.None;
        }

        try
        {
            if (everything || Latest is null)
                ScanNow(token, publish: true);
            else
                RescanFolders(changed, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException)
        {
            Failed?.Invoke(ex);
        }

        bool again;
        lock (_gate)
            again = _running && _dirty;
        if (again)
            TrySchedule(_settle);
    }

    /// <summary>
    /// Reads only the folders that changed and swaps each one into the existing
    /// result. The rest of the tree is left as it was.
    /// </summary>
    private void RescanFolders(IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        lock (_scanGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanResult? current = Latest;
            if (current is null)
                return;

            foreach (string folder in folders)
            {
                if (current.FindFolder(folder) is null || current.Root.FullPath.TrimEnd(Path.DirectorySeparatorChar).Equals(folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    continue;

                ScanResult fresh = DiskScanner.Scan(new ScanOptions
                {
                    Paths = [folder],
                    Duplicates = _options.Duplicates,
                    IncludeFilter = _options.IncludeFilter,
                    ExcludeFilter = _options.ExcludeFilter,
                    FilterFullPath = _options.FilterFullPath,
                    UseRegex = _options.UseRegex,
                    IncludeHidden = _options.IncludeHidden,
                    ExcludedAttributes = _options.ExcludedAttributes,
                    WalkThreads = _options.WalkThreads,
                    FileGroupMinimum = _options.FileGroupMinimum,
                    ShowNewestModified = _options.ShowNewestModified,
                }, cancellationToken);
                current.ReplaceFolder(folder, fresh);
            }

            Latest = current;
            try
            {
                Updated?.Invoke(current);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
            }
        }
    }

    private ScanResult ScanNow(CancellationToken cancellationToken, bool publish)
    {
        // One scan at a time. A change that arrives during it sets _dirty, and
        // the watch schedules another pass once this one returns.
        lock (_scanGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanResult result = DiskScanner.Scan(_options, cancellationToken);
            Latest = result;
            if (publish)
            {
                try
                {
                    Updated?.Invoke(result);
                }
                catch (Exception ex)
                {
                    Failed?.Invoke(ex);
                }
            }

            return result;
        }
    }

    private void TrySchedule(TimeSpan due)
    {
        try
        {
            _debounce?.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
