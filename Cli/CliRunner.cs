using System.Globalization;
using DiskGo.Core;
using DiskGo.Core.Ntfs;

namespace DiskGo;

/// <summary>Executes headless CLI scans, watches, snapshots, exports, and file actions.</summary>
internal static class CliRunner
{
    public static int Run(string[] args)
    {
        try
        {
            return Execute(args);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Scan canceled.");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Execute(string[] args)
    {
        if (Array.Exists(args, static arg => arg is "--list"))
            return ListTargets();
        if (CliOptions.Take(args, "--dump-mft") is string dump)
            return DumpMft(args, dump);
        if (CliOptions.Take(args, "--import") is string import)
            return Import(import, args);

        ScanOptions options = CliOptions.Parse(args, new Progress<ScanProgress>(Report), out CliOptions switches);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancel.Cancel();
        };

        if (switches.Monitor)
            return Watch(options, switches, cancel);

        ScanResult result = DiskScanner.Scan(options, cancel.Token);
        ApplyActions(result, switches);
        Publish(result, switches);
        return 0;
    }

    private static int Watch(ScanOptions options, CliOptions switches, CancellationTokenSource cancel)
    {
        using var live = new ScanMonitor(options);
        live.Updated += result => Publish(result, switches);
        live.Failed += ex => Console.Error.WriteLine(ex.Message);

        Publish(live.Start(cancel.Token), switches);
        if (switches.RescanPath is not null)
            Publish(live.RescanFolder(switches.RescanPath, cancel.Token), switches);
        Console.Error.WriteLine("Watching for changes. Press Ctrl+C to stop.");
        cancel.Token.WaitHandle.WaitOne();
        live.Stop();
        return 0;
    }

    private static void ApplyActions(ScanResult result, CliOptions switches)
    {
        if (switches.DeletePath is not null)
        {
            IReadOnlyList<FileEntry> gone = FileActions.Delete(result, switches.DeletePath, switches.DeleteMode);
            Console.WriteLine($"Deleted {gone.Count:N0} file(s) from {switches.DeletePath}");
        }

        if (switches.RenamePath is not null && switches.RenameTo is not null)
        {
            IReadOnlyList<FileEntry> moved = FileActions.Rename(result, switches.RenamePath, switches.RenameTo);
            Console.WriteLine($"Renamed {switches.RenamePath} to {switches.RenameTo} ({moved.Count:N0} file(s))");
        }

        if (switches.TransferPath is not null && switches.TransferTo is not null)
        {
            if (switches.TransferMoves)
            {
                IReadOnlyList<FileEntry> moved = FileActions.MoveTo(result, switches.TransferPath, switches.TransferTo);
                Console.WriteLine($"Moved {switches.TransferPath} to {switches.TransferTo} ({moved.Count:N0} file(s))");
            }
            else
            {
                string copy = FileActions.CopyTo(result, switches.TransferPath, switches.TransferTo);
                Console.WriteLine($"Copied {switches.TransferPath} to {copy}");
            }
        }

        if (switches.CopyPath is not null)
        {
            FileEntry? file = result.FindFile(switches.CopyPath);
            FolderNode? folder = file is null ? result.FindFolder(switches.CopyPath) : null;
            if (file is null && folder is null)
                throw new ArgumentException($"{switches.CopyPath} is not part of this scan.");

            if (switches.CopySize)
                FileActions.CopyPathAndSize(switches.CopyPath, file?.Size ?? folder!.Size, file?.AllocatedSize ?? folder!.AllocatedSize);
            else
                FileActions.CopyPath(switches.CopyPath);
            Console.WriteLine($"Copied {switches.CopyPath}");
        }
    }

    private static void Publish(ScanResult result, CliOptions switches)
    {
        WriteSummary(result);
        if (switches.ExportPath is not null)
        {
            CsvExporter.Export(result, switches.ExportPath, new CsvExportOptions
            {
                Format = switches.Tsv ? ExportFormat.Tsv : ExportFormat.Csv,
                IncludeFiles = !switches.FoldersOnly,
                IncludeFolders = !switches.FilesOnly,
                DuplicatesOnly = switches.DuplicatesOnly,
                IncludeAllDates = true,
                IncludeMftRecord = result.Method == ScanMethod.MasterFileTable,
                IncludeParentRecord = switches.ParentRecord,
                IncludeShortName = switches.ShortName,
                IncludeOwnSize = true,
                IncludePercentOfParent = true,
                IncludePercentOfDrive = switches.PercentOfDrive,
                IncludeDriveCapacity = switches.DriveCapacity,
                SplitPath = switches.SplitPath,
                MaxDepth = switches.MaxDepth,
                RowLimit = switches.RowLimit,
                UseUtc = switches.Utc,
                OnlyPaths = switches.Selected,
            });
            Console.WriteLine($"Exported {switches.ExportPath}");
        }

        if (switches.TypesPath is not null)
        {
            ExtensionExporter.Export(result, switches.TypesPath);
            Console.WriteLine($"Exported file types to {switches.TypesPath}");
        }
    }

    private static int ListTargets()
    {
        foreach (ScanTarget target in ScanTargetList.Enumerate())
            Console.WriteLine($"{target.Kind,-16} {target.Path}    {target.Label}");

        return 0;
    }

    private static void Report(ScanProgress progress)
    {
        Console.Error.Write(progress.Stage switch
        {
            ScanStage.ReadingMft => $"\rReading MFT {progress.Detail}   ",
            ScanStage.WalkingDirectories => $"\rScanning {progress.Files:N0} files, {progress.Folders:N0} folders   ",
            ScanStage.BuildingTree => $"\rBuilding tree, {progress.Files:N0} files   ",
            ScanStage.FindingDuplicates => $"\rFinding duplicates in {progress.Files:N0} files   ",
            _ => "",
        });
    }

    private static void WriteSummary(ScanResult result)
    {
        Console.Error.WriteLine();
        Console.WriteLine($"Scanned {result.RootPath} in {result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s " +
                          $"via {(result.Method == ScanMethod.MasterFileTable ? "the MFT" : "a directory walk")}" +
                          (result.FellBackFromMft ? " (the MFT could not be read)" : ""));
        Console.WriteLine($"Files: {result.Files.Count:N0}    Folders: {result.Root.FolderCount:N0}");
        Console.WriteLine($"Size: {Format(result.Root.Size)}    Allocated: {Format(result.Root.AllocatedSize)}");
        if (result.Drive.IsKnown)
        {
            Console.WriteLine($"Drive: {Format(result.Drive.Used)} used of {Format(result.Drive.Total)} ({result.Drive.UsedPercent.ToString("0.0", CultureInfo.InvariantCulture)}%)");
            if (result.Drive.Serial is uint serial)
                Console.WriteLine($"Serial: {serial >> 16:X4}-{serial & 0xFFFF:X4}");
            if (result.Drive.Reserved > 0)
                Console.WriteLine($"Reserved: {Format(result.Drive.Reserved)} in use but not part of any file");
        }

        Console.WriteLine();
        Console.WriteLine("Largest folders");
        foreach (FolderNode folder in result.Root.ChildrenBySize().Take(15))
            Console.WriteLine($"  {Format(folder.Size),10}  {folder.PercentOfParent.ToString("0.0", CultureInfo.InvariantCulture),5}%  {folder.Name}");

        Console.WriteLine();
        Console.WriteLine("Largest file types");
        foreach (ExtensionTotal extension in result.Extensions.Take(10))
            Console.WriteLine($"  {Format(extension.Size),10}  {extension.PercentOf(result.Root.Size).ToString("0.0", CultureInfo.InvariantCulture),5}%  {extension.Extension} ({extension.FileCount:N0})");

        int duplicates = result.Files.Count(static file => file.DuplicateCount > 1);
        if (duplicates > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Duplicates: {duplicates:N0} files");
            foreach (FileEntry file in result.DuplicateFiles().Take(15))
                Console.WriteLine($"  {file.DuplicateCount,4} x {Format(file.Size),10}  {file.FullPath}");
        }

        if (result.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{result.Warnings.Count:N0} directories could not be read. The first few:");
            foreach (string warning in result.Warnings.Take(5))
                Console.WriteLine("  " + warning);
        }
    }

    private static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(unit == 0 ? "0" : "0.00", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static int DumpMft(string[] args, string filePath)
    {
        string? drive = args.FirstOrDefault(static arg => !arg.StartsWith('-'));
        if (drive is null || drive.Length < 1 || !char.IsAsciiLetter(drive[0]))
            throw new ArgumentException("Name the drive to dump, for example: DiskGo C: --dump-mft C.mft");

        long records = MftDump.Write(char.ToUpperInvariant(drive[0]), filePath, recordsWritten: new Progress<long>(count =>
            Console.Error.Write($"\rDumping MFT, {count:N0} records   ")));
        Console.Error.WriteLine();
        Console.WriteLine($"Wrote {records:N0} records to {filePath}");
        return 0;
    }

    private static int Import(string filePath, string[] args)
    {
        ScanResult result = CsvImporter.Import(filePath);
        WriteSummary(result);

        string? typesPath = CliOptions.Take(args, "--export-types");
        if (typesPath is not null)
        {
            ExtensionExporter.Export(result, typesPath);
            Console.WriteLine($"Exported file types to {typesPath}");
        }

        return 0;
    }
}
