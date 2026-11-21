using System.Diagnostics;
using DiskGo.Core.Ntfs;
using DiskGo.Core.Portable;

namespace DiskGo.Core;

/// <summary>
/// Scans a drive or a folder and returns the tree, the flat file list and the
/// per-extension totals. This is the entry point the rest of the program uses;
/// everything under <c>Ntfs</c> is an implementation detail.
/// </summary>
public static class DiskScanner
{
    /// <summary>
    /// Scans <see cref="ScanOptions.Path"/>.
    ///
    /// A whole NTFS volume is read from its Master File Table. Anything else,
    /// including a single folder, is walked directory by directory unless
    /// <see cref="ScanOptions.PreferMftForFolders"/> says to read the MFT anyway
    /// and keep only that folder. When an MFT read fails and
    /// <see cref="ScanOptions.AllowSlowFallback"/> is set, the scan continues
    /// with the directory walk instead of giving up.
    /// </summary>
    public static ScanResult Scan(ScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Paths.Count == 1 && MftDump.IsDump(options.Path))
            return MftDump.Read(options.Path, options.DumpVolumeRoot, cancellationToken, options.DumpClusterSize, options.UpcaseFile);

        string[] targets = ScanTargets.Normalize(ApplyLists(options.Paths, options.SubstList, options.NetworkList));
        var filter = PathFilter.Parse(
            options.IncludeFilter, options.ExcludeFilter, options.FilterFullPath, options.IncludeHidden, options.ExcludedAttributes, options.UseRegex);
        var watch = Stopwatch.StartNew();
        Report(options, ScanStage.Preparing, 0, 0, string.Join(", ", targets));

        (FolderNode root, List<FileEntry> files, ScanMethod method, bool fellBack, List<string> warnings, IEqualityComparer<string> names) =
            targets.Length == 1
                ? ScanOne(targets[0], filter, options, cancellationToken)
                : ScanMany(targets, filter, options, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        Report(options, ScanStage.FindingDuplicates, files.Count, root.FolderCount + 1, root.FullPath);
        DuplicateFinder.Apply(files, options.Duplicates, cancellationToken, names);

        Report(options, ScanStage.BuildingTree, files.Count, root.FolderCount + 1, "Sorting");
        root.GroupSmallFiles(options.FileGroupMinimum);
        root.Seal();
        root.UseNewestModified(options.ShowNewestModified);
        root.SortBySizeDescending();
        List<ExtensionTotal> extensions = ExtensionSummary.From(files);

        watch.Stop();
        Report(options, ScanStage.Completed, files.Count, root.FolderCount + 1, $"{watch.Elapsed.TotalSeconds:0.0}s");

        return new ScanResult(
            root.FullPath,
            method,
            fellBack,
            root,
            files,
            extensions,
            targets.Length == 1 ? ReadDriveSpace(targets[0], method == ScanMethod.MasterFileTable ? root.AllocatedSize : -1) : DriveSpace.Unknown,
            watch.Elapsed,
            warnings);
    }

    /// <summary>
    /// Reads each NTFS volume once, however many selected folders it holds,
    /// and walks everything else one folder at a time. A selection that spans
    /// volumes is then hung under a single root.
    /// </summary>
    private static (FolderNode Root, List<FileEntry> Files, ScanMethod Method, bool FellBack, List<string> Warnings, IEqualityComparer<string> Names) ScanMany(
        string[] targets, PathFilter filter, ScanOptions options, CancellationToken cancellationToken)
    {
        var groups = targets.GroupBy(VolumeOf).ToArray();
        var parts = new List<(FolderNode, List<FileEntry>)>();
        var warnings = new List<string>();
        var method = ScanMethod.MasterFileTable;
        bool fellBack = false;
        IEqualityComparer<string> names = StringComparer.OrdinalIgnoreCase;

        foreach (IGrouping<string, string> group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] paths = group.ToArray();

            if (PortableDevices.IsDevicePath(group.Key))
            {
                method = ScanMethod.DirectoryWalk;
                foreach (string path in paths)
                {
                    (FolderNode deviceRoot, List<FileEntry> deviceFiles, _, _, List<string> deviceWarnings) =
                        ScanDevice(path, filter, options, cancellationToken);
                    parts.Add((deviceRoot, deviceFiles));
                    warnings.AddRange(deviceWarnings);
                }

                continue;
            }

            bool wholeVolume = paths.Any(path => ScanTargets.IsVolumeRoot(path) || VolumeMounts.IsVolumeName(path));
            bool wantMft = wholeVolume || options.PreferMftForFolders;

            if (wantMft && TryScanMft(paths[0], wholeVolume ? null : paths, filter, options, cancellationToken, out FolderNode? mftRoot, out List<FileEntry>? mftFiles, out IEqualityComparer<string>? mftNames))
            {
                parts.Add((mftRoot!, mftFiles!));
                names = mftNames!;
                if (wholeVolume)
                    AttachMountedVolumes(mftRoot!, mftFiles!, filter, options, warnings, cancellationToken);
                continue;
            }

            if (wantMft && !options.AllowSlowFallback)
                throw new IOException($"The MFT of {group.Key} could not be read, and the slow folder scan is disabled.");

            if (wantMft)
            {
                fellBack = true;
                warnings.Add($"The MFT of {group.Key} could not be read. Its folders were scanned directly.");
            }

            method = ScanMethod.DirectoryWalk;
            foreach (string path in paths)
            {
                (FolderNode root, List<FileEntry> files, List<string> walkWarnings) = Walk(path, filter, options, cancellationToken);
                parts.Add((root, files));
                warnings.AddRange(walkWarnings);
            }
        }

        (FolderNode combined, List<FileEntry> combinedFiles) = ResultCombiner.Combine(parts);
        return (combined, combinedFiles, method, fellBack, warnings, names);
    }

    private static (FolderNode, List<FileEntry>, ScanMethod, bool, List<string>, IEqualityComparer<string>) ScanOne(
        string fullPath, PathFilter filter, ScanOptions options, CancellationToken cancellationToken)
    {
        if (PortableDevices.IsDevicePath(fullPath))
        {
            (FolderNode deviceRoot, List<FileEntry> deviceFiles, ScanMethod deviceMethod, bool deviceFellBack, List<string> deviceWarnings) =
                ScanDevice(fullPath, filter, options, cancellationToken);
            return (deviceRoot, deviceFiles, deviceMethod, deviceFellBack, deviceWarnings, StringComparer.OrdinalIgnoreCase);
        }

        var warnings = new List<string>();
        bool wantMft = ScanTargets.IsVolumeRoot(fullPath) || VolumeMounts.IsVolumeName(fullPath) || options.PreferMftForFolders;

        if (wantMft && TryScanMft(fullPath, ScanTargets.IsVolumeRoot(fullPath) || VolumeMounts.IsVolumeName(fullPath) ? null : [fullPath], filter, options, cancellationToken, out FolderNode? root, out List<FileEntry>? files, out IEqualityComparer<string>? names))
        {
            if (ScanTargets.IsVolumeRoot(fullPath) || VolumeMounts.IsVolumeName(fullPath))
                AttachMountedVolumes(root!, files!, filter, options, warnings, cancellationToken);
            return (root!, files!, ScanMethod.MasterFileTable, false, warnings, names!);
        }

        if (wantMft && !options.AllowSlowFallback)
            throw new IOException($"The MFT of {VolumeOf(fullPath)} could not be read, and the slow folder scan is disabled.");

        bool fellBack = wantMft;
        if (fellBack)
            warnings.Add($"The MFT of {VolumeOf(fullPath)} could not be read. The folder was scanned directly, so the result may be slower and less complete.");

        (FolderNode walked, List<FileEntry> walkedFiles, List<string> walkWarnings) = Walk(fullPath, filter, options, cancellationToken);
        warnings.AddRange(walkWarnings);
        return (walked, walkedFiles, ScanMethod.DirectoryWalk, fellBack, warnings, StringComparer.OrdinalIgnoreCase);
    }

    private static (FolderNode, List<FileEntry>, ScanMethod, bool, List<string>) ScanDevice(
        string devicePath, PathFilter filter, ScanOptions options, CancellationToken cancellationToken)
    {
        Report(options, ScanStage.WalkingDirectories, 0, 0, devicePath);
        (FolderNode root, List<FileEntry> files, List<string> warnings) = PortableDeviceScanner.Scan(
            devicePath,
            filter,
            cancellationToken,
            (seenFiles, seenFolders) => Report(options, ScanStage.WalkingDirectories, seenFiles, seenFolders, devicePath));
        return (root, files, ScanMethod.DirectoryWalk, false, warnings);
    }

    private static (FolderNode, List<FileEntry>, List<string>) Walk(
        string path, PathFilter filter, ScanOptions options, CancellationToken cancellationToken)
    {
        Report(options, ScanStage.WalkingDirectories, 0, 0, path);
        return DirectoryScanner.Scan(
            path,
            filter,
            cancellationToken,
            (seenFiles, seenFolders) => Report(options, ScanStage.WalkingDirectories, seenFiles, seenFolders, path),
            options.WalkThreads);
    }

    private static bool TryScanMft(
        string fullPath,
        IReadOnlyList<string>? restrictTo,
        PathFilter filter,
        ScanOptions options,
        CancellationToken cancellationToken,
        out FolderNode? root,
        out List<FileEntry>? files,
        out IEqualityComparer<string>? names)
    {
        root = null;
        files = null;
        names = null;

        char letter = ScanTargets.DriveLetter(fullPath);
        string? volumeName = letter == '\0' && VolumeMounts.IsVolumeName(fullPath) ? fullPath : null;
        if (letter == '\0' && volumeName is null || letter != '\0' && !IsNtfs(letter))
            return false;

        try
        {
            string where = letter == '\0' ? fullPath : $"{letter}:";
            Report(options, ScanStage.ReadingMft, 0, 0, where);
            MftScan scan = volumeName is null
                ? MftScanner.ReadVolume(letter, cancellationToken, count => Report(options, ScanStage.ReadingMft, 0, 0, $"{count:N0} records"), options.MftRead)
                : MftScanner.ReadVolume(volumeName, cancellationToken, count => Report(options, ScanStage.ReadingMft, 0, 0, $"{count:N0} records"), options.MftRead);

            Report(options, ScanStage.BuildingTree, 0, 0, where);
            names = scan.Upcase is null ? StringComparer.OrdinalIgnoreCase : scan.Upcase;
            (root, files) = TreeBuilder.FromMft(
                scan.Records,
                letter == '\0' ? fullPath : letter + @":\",
                restrictTo,
                filter,
                options.IncludeNtfsMetadata,
                cancellationToken,
                (seenFiles, seenFolders) => Report(options, ScanStage.BuildingTree, seenFiles, seenFolders, letter == '\0' ? fullPath : $"{letter}:"),
                scan.Upcase);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fills every folder that has another volume mounted on it. The parent
    /// scan only saw the mount point itself, so each mounted volume is read
    /// from its own MFT and hung where the mount point was.
    /// </summary>
    private static void AttachMountedVolumes(
        FolderNode root, List<FileEntry> files, PathFilter filter, ScanOptions options, List<string> warnings, CancellationToken cancellationToken)
    {
        foreach ((string folder, string volume) in VolumeMounts.MountedVolumes(root.FullPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FolderNode? point = root.Find(folder);
            if (point is null || point.Parent is null)
                continue;

            if (!TryScanMft(volume, null, filter, options, cancellationToken, out FolderNode? mounted, out List<FileEntry>? mountedFiles, out _))
            {
                warnings.Add($"{folder} has volume {volume} mounted on it, and that volume could not be read.");
                continue;
            }

            // The mount point itself was a directory entry in the parent scan.
            // Its files, if a reparse point ever contributed any, leave with it.
            foreach (FileEntry stale in FilesInside(point))
                files.Remove(stale);
            point.Parent.ReplaceChild(point.Name, mounted!);
            files.AddRange(mountedFiles!);
        }
    }

    /// <summary>
    /// Rewrites letters that only exist in the lists handed in from the session
    /// that launched this one. The live lookup still runs afterwards, so a letter
    /// this process can see for itself is resolved the ordinary way.
    /// </summary>
    private static IReadOnlyList<string> ApplyLists(IReadOnlyList<string> paths, string subst, string network)
    {
        if (subst.Length == 0 && network.Length == 0)
            return paths;

        List<ListedDrive> substDrives = SessionLists.Parse(subst);
        List<ListedDrive> networkDrives = SessionLists.Parse(network);
        var rewritten = new string[paths.Count];
        for (int i = 0; i < paths.Count; i++)
            rewritten[i] = SessionLists.Resolve(paths[i], substDrives, networkDrives);
        return rewritten;
    }

    private static List<FileEntry> FilesInside(FolderNode folder)
    {
        var inside = new List<FileEntry>(folder.Files);
        foreach (FolderNode child in folder.Children)
            inside.AddRange(FilesInside(child));
        return inside;
    }

    private static string VolumeOf(string path)
    {
        // A portable device is a volume of its own: it shares nothing with a
        // drive letter, so each one is read on its own.
        if (PortableDevices.IsDevicePath(path) || VolumeMounts.IsVolumeName(path))
            return path;

        char letter = ScanTargets.DriveLetter(path);
        return letter == '\0' ? path : letter + @":\";
    }

    private static DriveSpace ReadDriveSpace(string path, long accounted)
    {
        try
        {
            if (VolumeMounts.IsVolumeName(path))
            {
                string volumeRoot = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return VolumeMounts.TryGetSpace(volumeRoot, out long total, out long free)
                    ? new DriveSpace(total, free, accounted, VolumeSerial.TryRead(volumeRoot))
                    : DriveSpace.Unknown;
            }

            string root = Path.GetPathRoot(path) ?? "";
            if (root.Length == 0)
                return DriveSpace.Unknown;

            if (ScanTargets.IsVolumeRoot(root))
            {
                var drive = new DriveInfo(root);
                return drive.IsReady
                    ? new DriveSpace(drive.TotalSize, drive.AvailableFreeSpace, accounted, VolumeSerial.TryRead(root))
                    : DriveSpace.Unknown;
            }

            return VolumeMounts.TryGetSpace(root, out long uncTotal, out long uncFree)
                ? new DriveSpace(uncTotal, uncFree, accounted, VolumeSerial.TryRead(root))
                : DriveSpace.Unknown;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return DriveSpace.Unknown;
        }
    }

    private static void Report(ScanOptions options, ScanStage stage, long files, long folders, string detail)
        => options.Progress?.Report(new ScanProgress(stage, files, folders, detail));

    private static bool IsNtfs(char letter)
    {
        try
        {
            var drive = new DriveInfo(letter + @":\");
            return drive.IsReady && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

}
