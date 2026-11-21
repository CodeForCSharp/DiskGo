using DiskGo.Core;
using DiskGo.Core.Ntfs;
using System.Runtime.InteropServices;
using Xunit;

namespace DiskGo.Tests;

/// <summary>
/// Exercises the parts of a scan that do not need a raw volume handle:
/// the directory walk, duplicate grouping, extension totals and CSV export.
/// The MFT reader needs an elevated process and a real NTFS volume, so it is
/// checked by running the program rather than here.
/// </summary>
public partial class ScanTests
{
    [Fact]
    public void DirectoryWalk_SumsFoldersAndFindsDuplicates()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("docs", "notes.txt"), "hello");
        File.WriteAllText(sandbox.File("docs", "copy.txt"), "hello");
        File.WriteAllBytes(sandbox.File("photos", "a.jpg"), new byte[4096]);
        File.WriteAllText(sandbox.File("photos", "nested", "notes.txt"), "hello");

        ScanResult result = DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Root],
            Duplicates = DuplicateMode.NameAndSize,
        });

        Assert.Equal(ScanMethod.DirectoryWalk, result.Method);
        Assert.Equal(4, result.Files.Count);
        Assert.Equal(4096 + 15, result.Root.Size);

        FolderNode photos = Assert.Single(result.Root.Children, folder => folder.Name == "photos");
        Assert.Equal(4096 + 5, photos.Size);
        Assert.Equal(4096, photos.OwnSize);

        FileEntry[] duplicates = result.DuplicateFiles().ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, file => Assert.Equal("notes.txt", file.Name));
        Assert.All(duplicates, file => Assert.Equal(2, file.DuplicateCount));

        ExtensionTotal text = Assert.Single(result.Extensions, total => total.Extension == ".txt");
        Assert.Equal(3, text.FileCount);
        Assert.Equal(15, text.Size);
    }

    [Fact]
    public void IncludeFilter_KeepsOnlyMatchingNames()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("keep.log"), "x");
        File.WriteAllText(sandbox.File("drop.txt"), "yyyy");

        ScanResult result = DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Root],
            IncludeFilter = "*.log",
        });

        FileEntry file = Assert.Single(result.Files);
        Assert.Equal("keep.log", file.Name);
        Assert.Equal(1, result.Root.Size);
    }

    [Fact]
    public void HiddenFiles_AreDroppedOnlyWhenAsked()
    {
        using var sandbox = new Sandbox();
        string hidden = sandbox.File("secret.txt");
        File.WriteAllText(hidden, "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.WriteAllText(sandbox.File("visible.txt"), "ok");

        ScanResult all = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        Assert.Equal(2, all.Files.Count);

        ScanResult shown = DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Root],
            IncludeHidden = false,
        });

        FileEntry file = Assert.Single(shown.Files);
        Assert.Equal("visible.txt", file.Name);
        Assert.Equal(2, shown.Root.Size);
    }

    [Fact]
    public void ExcludedAttributes_DropFilesThatCarryAnyOfThem()
    {
        using var sandbox = new Sandbox();
        string system = sandbox.File("core.dat");
        File.WriteAllText(system, "system");
        File.SetAttributes(system, FileAttributes.System);
        File.WriteAllText(sandbox.File("notes.txt"), "plain");

        ScanResult result = DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Root],
            ExcludedAttributes = FileAttributes.System | FileAttributes.Temporary,
        });

        FileEntry file = Assert.Single(result.Files);
        Assert.Equal("notes.txt", file.Name);
    }

    [Fact]
    public async Task Monitor_RescansTheSameFolderAfterItChanges()
    {
        // The two monitor tests share the machine's change notification, so they
        // must not run at the same time.
        await MonitorGate.WaitAsync();
        try
        {
            await RunMonitorRescan();
        }
        finally
        {
            MonitorGate.Release();
        }
    }

    private static async Task RunMonitorRescan()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("a.txt"), "one");

        var options = new ScanOptions { Paths = [sandbox.Root] };
        var updated = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new ScanMonitor(options, settle: TimeSpan.FromMilliseconds(50));
        monitor.Failed += ex => updated.TrySetException(ex);
        monitor.Updated += result => updated.TrySetResult(result);

        ScanResult first = monitor.Start();
        Assert.Single(first.Files);
        Assert.True(monitor.IsMonitoring);

        File.WriteAllText(sandbox.File("b.txt"), "two");

        ScanResult second = await updated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, second.Files.Count);
        Assert.Equal(2, monitor.Latest!.Files.Count);

        monitor.Stop();
        Assert.False(monitor.IsMonitoring);
        Assert.Throws<InvalidOperationException>(() => monitor.Rescan());
    }

    private static readonly SemaphoreSlim MonitorGate = new(1, 1);

    [Fact]
    public void Monitor_RefusesASecondStartAndADeviceOnItsOwn()
    {
        MonitorGate.Wait();
        try
        {
            RunMonitorRefuses();
        }
        finally
        {
            MonitorGate.Release();
        }
    }

    private static void RunMonitorRefuses()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("a.txt"), "one");

        using var monitor = new ScanMonitor(new ScanOptions { Paths = [sandbox.Root] });
        monitor.Start();
        Assert.Throws<InvalidOperationException>(() => monitor.Start());

        using var device = new ScanMonitor(new ScanOptions { Paths = ["mtp:nothing-connected"] });
        Assert.Throws<InvalidOperationException>(() => device.Start());
    }

    [Fact]
    public void DateDuplicates_DoNotGroupFilesWrittenAtDifferentTimes()
    {
        using var sandbox = new Sandbox();
        string first = sandbox.File("same.txt");
        string second = sandbox.File("nested", "same.txt");
        File.WriteAllText(first, "same");
        File.WriteAllText(second, "same");
        File.SetLastWriteTime(first, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local));
        File.SetLastWriteTime(second, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Local));

        ScanResult result = DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Root],
            Duplicates = DuplicateMode.NameSizeAndDate,
        });

        Assert.Empty(result.DuplicateFiles());
        Assert.All(result.Files, file => Assert.Equal(1, file.DuplicateCount));
    }

    [Fact]
    public void RenamedCopies_AreTheSameDuplicateAsTheOriginal()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("notes.txt"), "same");
        File.WriteAllText(sandbox.File("notes - Copy.txt"), "same");
        File.WriteAllText(sandbox.File("notes - copy (2).txt"), "same");
        File.WriteAllText(sandbox.File("notes (draft).txt"), "same");
        File.WriteAllText(sandbox.File("notes - Copy - Copy.txt"), "same");

        ScanResult result = DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Root],
            Duplicates = DuplicateMode.NameAndSize,
        });

        FileEntry[] group = result.DuplicateFiles().ToArray();
        Assert.Equal(3, group.Length);
        Assert.All(group, file => Assert.Equal(3, file.DuplicateCount));
        Assert.Contains(group, file => file.Name == "notes.txt");
        Assert.Contains(group, file => file.Name == "notes - Copy.txt");
        Assert.Contains(group, file => file.Name == "notes - copy (2).txt");
        Assert.DoesNotContain(group, file => file.Name == "notes (draft).txt");
        Assert.DoesNotContain(group, file => file.Name == "notes - Copy - Copy.txt");
    }

    [Fact]
    public void VolumeSerial_IsTheNumberWindowsReports()
    {
        using var sandbox = new Sandbox();
        uint? serial = VolumeSerial.TryRead(sandbox.Root);
        Assert.NotNull(serial);

        string root = Path.GetPathRoot(sandbox.Root)!;
        Assert.Equal(0, GetVolumeInformation(root, 0, 0, out uint expected, out _, out _, 0, 0) ? 0 : 1);
        Assert.Equal(expected, serial);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(
        string rootPathName, nint volumeName, int volumeNameSize, out uint serialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, nint fileSystemName, int fileSystemNameSize);

    [Theory]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("notes - Copy.txt", "notes.txt")]
    [InlineData("notes - COPY.TXT", "notes.TXT")]
    [InlineData("notes (1).txt", "notes.txt")]
    [InlineData("notes - copy (2).txt", "notes.txt")]
    [InlineData("notes (draft).txt", "notes (draft).txt")]
    [InlineData("notes (0).txt", "notes (0).txt")]
    [InlineData("notes - Copy - Copy.txt", "notes - Copy.txt")]
    [InlineData("notes (1) (2).txt", "notes (1).txt")]
    [InlineData("notes (2) (draft).txt", "notes (2) (draft).txt")]
    [InlineData("archive.tar.gz", "archive.tar.gz")]
    [InlineData("archive.tar - copy.gz", "archive.tar.gz")]
    [InlineData("readme", "readme")]
    [InlineData("readme - copy", "readme")]
    public void DuplicateName_DropsOneCopyDecoration(string name, string expected)
        => Assert.Equal(expected, DuplicateName.Normalize(name));

    [Fact]
    public void CsvExport_RoundTripsNamesThatNeedQuoting()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("odd, name.txt"), "abc");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        string csv = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N") + ".csv");
        CsvExporter.Export(result, csv, new CsvExportOptions { IncludeOwnSize = true, IncludePercentOfParent = true });

        string text = File.ReadAllText(csv);
        File.Delete(csv);
        Assert.Contains("Type,Path,Size,Allocated,Own Size,Own Allocated,% of Parent,Files,Modified,Attributes,Duplicates", text);
        Assert.Contains("\"" + sandbox.Root + "\\odd, name.txt\"", text);
        Assert.Contains("Folder", text);
        Assert.Contains("File", text);
    }

    [Fact]
    public void DirectoryWalk_UsesSeveralThreadsAndKeepsTheTotals()
    {
        using var sandbox = new Sandbox();
        long expected = 0;
        for (int folder = 0; folder < 6; folder++)
        {
            for (int file = 0; file < 4; file++)
            {
                int bytes = folder * 10 + file + 1;
                File.WriteAllBytes(sandbox.File("branch" + folder, "file" + file + ".bin"), new byte[bytes]);
                expected += bytes;
            }
        }

        (FolderNode root, List<FileEntry> files, List<string> warnings) = DirectoryScanner.Scan(sandbox.Root, PathFilter.Parse("", "", false), CancellationToken.None, workers: 4);

        Assert.Empty(warnings);
        Assert.Equal(24, files.Count);
        Assert.Equal(expected, root.Size);
        Assert.Equal(24, root.FileCount);
        Assert.Equal(6, root.FolderCount);
        Assert.Equal(6, root.Children.Count);
        Assert.All(root.Children, folder => Assert.Equal(4, folder.Files.Count));
    }

    [Fact]
    public void Delete_RemovesTheNamedFileAndKeepsTheRest()
    {
        using var sandbox = new Sandbox();
        string keep = sandbox.File("keep.txt");
        string drop = sandbox.File("drop.txt");
        File.WriteAllText(keep, "stay");
        File.WriteAllText(drop, "go");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        IReadOnlyList<FileEntry> gone = FileActions.Delete(result, drop, DeleteMode.Permanent);

        Assert.Single(gone);
        Assert.False(File.Exists(drop));
        Assert.True(File.Exists(keep));
        FileEntry remaining = Assert.Single(result.Files);
        Assert.Equal("keep.txt", remaining.Name);
        Assert.Equal(4, result.Root.Size);
        Assert.Null(result.FindFile(drop));
    }

    [Fact]
    public void Delete_RefusesAPathTheScanDidNotSee()
    {
        using var sandbox = new Sandbox();
        string keep = sandbox.File("keep.txt");
        File.WriteAllText(keep, "stay");
        string outsider = Path.Combine(Path.GetTempPath(), "diskgo-outsider-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outsider, "not scanned");

        try
        {
            ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
            Assert.Throws<ArgumentException>(() => FileActions.Delete(result, outsider, DeleteMode.Permanent));
            Assert.True(File.Exists(outsider));
        }
        finally
        {
            File.Delete(outsider);
        }
    }

    [Fact]
    public void Rename_MovesTheFileAndTheTreeAgrees()
    {
        using var sandbox = new Sandbox();
        string original = sandbox.File("box", "old.txt");
        File.WriteAllText(original, "contents");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        FileActions.Rename(result, original, "new.txt");

        string renamed = Path.Combine(sandbox.Root, "box", "new.txt");
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(original));
        Assert.Equal("new.txt", Assert.Single(result.Files).Name);
        Assert.Equal(renamed, result.FindFile(renamed)!.FullPath);
        Assert.NotNull(result.FindFolder(Path.Combine(sandbox.Root, "box")));
    }

    [Fact]
    public void TsvExport_RoundTripsAndForeignColumnsImport()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("notes.txt"), "hello");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        string tsv = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N") + ".tsv");
        CsvExporter.Export(result, tsv, new CsvExportOptions { Format = ExportFormat.Tsv, IncludeAllDates = true });

        string text = File.ReadAllText(tsv);
        Assert.Contains("Type\tPath\tSize", text);

        ScanResult imported = CsvImporter.Import(tsv);
        File.Delete(tsv);
        Assert.Equal("notes.txt", Assert.Single(imported.Files).Name);
        Assert.Equal(5, imported.Root.Size);

        string foreign = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(foreign, "File Name,Logical Size,Size on Disk\n" + sandbox.File("notes.txt") + ",5,4096\n");
        ScanResult other = CsvImporter.Import(foreign);
        File.Delete(foreign);
        FileEntry file = Assert.Single(other.Files);
        Assert.Equal(5, file.Size);
        Assert.Equal(4096, file.AllocatedSize);

        string split = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N") + ".csv");
        CsvExporter.Export(result, split, new CsvExportOptions { SplitPath = true, IncludeAllDates = true, IncludeDriveCapacity = true });
        ScanResult rejoined = CsvImporter.Import(split);
        File.Delete(split);
        Assert.Equal(sandbox.File("notes.txt"), Assert.Single(rejoined.Files).FullPath);
    }

    [Fact]
    public void GroupSmallFiles_FoldsOnlyTheFilesBelowTheThreshold()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("tiny-a.txt"), "a");
        File.WriteAllText(sandbox.File("tiny-b.txt"), "b");
        File.WriteAllText(sandbox.File("big.txt"), new string('x', 100));

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root], FileGroupMinimum = 50 });

        Assert.Equal(3, result.Files.Count); // the group changes the tree, not how many files were found
        FolderNode group = Assert.Single(result.Root.Children, static folder => folder.IsFileGroup);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(result.Root.Files, static file => file.Name == "big.txt");
        Assert.Equal(102, result.Root.Size);
    }

    [Fact]
    public void CopyTo_LeavesTheScanAndWritesTheCopy()
    {
        using var sandbox = new Sandbox();
        string source = sandbox.File("box", "note.txt");
        File.WriteAllText(source, "contents");
        string destination = Path.Combine(sandbox.Root, "elsewhere");
        Directory.CreateDirectory(destination);

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        string copy = FileActions.CopyTo(result, source, destination);

        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(source));
        Assert.Equal(1, result.Files.Count);
        Assert.NotNull(result.FindFile(source));
    }

    [Fact]
    public void MoveTo_FollowsTheFileIntoTheNewFolder()
    {
        using var sandbox = new Sandbox();
        string source = sandbox.File("box", "note.txt");
        File.WriteAllText(source, "contents");
        string destination = Path.Combine(sandbox.Root, "elsewhere");
        Directory.CreateDirectory(destination);

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        FileActions.MoveTo(result, source, destination);

        string moved = Path.Combine(destination, "note.txt");
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(source));
        Assert.Equal(moved, Assert.Single(result.Files).FullPath);
        Assert.Null(result.FindFile(source));
    }

    [Fact]
    public void SessionList_RewritesALetterItWasGiven()
    {
        List<DiskGo.Core.ListedDrive> subst = SessionLists.Parse(@"S=\??\C:\Real\Folder");
        List<DiskGo.Core.ListedDrive> network = SessionLists.Parse(@"Z:=\\server\share");

        Assert.Equal(@"C:\Real\Folder\notes.txt", SessionLists.Resolve(@"S:\notes.txt", subst, network));
        Assert.Equal(@"\\server\share\notes.txt", SessionLists.Resolve(@"Z:\notes.txt", subst, network));
        Assert.Equal(@"C:\untouched.txt", SessionLists.Resolve(@"C:\untouched.txt", subst, network));
    }

    [Fact]
    public void ExportSelected_WritesOnlyTheNamedFolder()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("keep", "a.txt"), "aa");
        File.WriteAllText(sandbox.File("drop", "b.txt"), "bbb");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        string csv = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N") + ".csv");
        CsvExporter.Export(result, csv, new CsvExportOptions { OnlyPaths = [Path.Combine(sandbox.Root, "keep")] });

        string text = File.ReadAllText(csv);
        File.Delete(csv);
        Assert.Contains("a.txt", text);
        Assert.DoesNotContain("b.txt", text);
    }

    [Fact]
    public void ResultFilter_NarrowsAFinishedScanWithoutLosingTheTotals()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("keep.log"), "ab");
        File.WriteAllText(sandbox.File("notes", "drop.txt"), "xxxxx");

        ScanResult scanned = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        ScanResult filtered = ResultFilter.Apply(scanned, new ScanOptions { Paths = [sandbox.Root], IncludeFilter = @".*\.log", UseRegex = true });

        FileEntry file = Assert.Single(filtered.Files);
        Assert.Equal("keep.log", file.Name);
        Assert.Equal(2, filtered.Root.Size);
        Assert.Equal(2, scanned.Files.Count);
    }

    [Fact]
    public void SearchFilter_KeepsFilesBySizeDateAndLength()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("small.txt"), "ab");
        File.WriteAllText(sandbox.File("bigger.bin"), new string('x', 2000));
        string old = sandbox.File("old.txt");
        File.WriteAllText(old, "old");
        File.SetLastWriteTime(old, new DateTime(2019, 6, 1, 12, 0, 0));

        ScanResult bySize = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root], IncludeFilter = ">=1k" });
        Assert.Equal("bigger.bin", Assert.Single(bySize.Files).Name);

        ScanResult byDate = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root], IncludeFilter = "<2020/01/01" });
        Assert.Equal("old.txt", Assert.Single(byDate.Files).Name);

        ScanResult byLength = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root], IncludeFilter = "namelen=10" });
        Assert.Equal("bigger.bin", Assert.Single(byLength.Files).Name);

        ScanResult either = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root], IncludeFilter = "*.bin|*.txt >=1k" });
        Assert.Equal("bigger.bin", Assert.Single(either.Files).Name);
    }

    [Fact]
    public void ExcludeFilter_SkipsANamedFolderWithoutReadingIt()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("keep.txt"), "aa");
        File.WriteAllText(sandbox.File("skip", "gone.txt"), "bbbb");

        ScanResult result = DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Root],
            ExcludeFilter = Path.Combine(sandbox.Root, "skip") + Path.DirectorySeparatorChar,
            FilterFullPath = true,
        });

        Assert.Equal("keep.txt", Assert.Single(result.Files).Name);
        Assert.Equal(2, result.Root.Size);
    }

    [Fact]
    public void FolderModified_IsItsOwnTimeUnlessTheNewestInsideIsAskedFor()
    {
        using var sandbox = new Sandbox();
        string file = sandbox.File("box", "note.txt");
        File.WriteAllText(file, "abc");
        var folderTime = new DateTime(2020, 1, 2, 3, 4, 5);
        var fileTime = new DateTime(2024, 5, 6, 7, 8, 9);
        Directory.SetLastWriteTime(Path.GetDirectoryName(file)!, folderTime);
        File.SetLastWriteTime(file, fileTime);

        ScanResult own = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        FolderNode box = Assert.Single(own.Root.Children);
        Assert.Equal(folderTime, box.Modified);

        ScanResult newest = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root], ShowNewestModified = true });
        Assert.Equal(fileTime, Assert.Single(newest.Root.Children).Modified);
    }

    [Fact]
    public void HardLink_IsCountedOnceInTheFolderTotal()
    {
        using var sandbox = new Sandbox();
        string first = sandbox.File("first.txt");
        File.WriteAllText(first, "hello");
        string second = Path.Combine(sandbox.Root, "second.txt");
        HardLink.CreateHardLink(second, first);

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(5, result.Root.Size);
        Assert.Contains(result.Files, file => !file.CountsTowardTotal);
    }

    private static partial class HardLink
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Create(string link, string existing, nint security);

        public static void CreateHardLink(string link, string existing)
        {
            if (!Create(link, existing, 0))
                throw new IOException("The hard link could not be created.");
        }
    }

    [Fact]
    public void ReplaceFolder_SwapsOneBranchAndLeavesTheOther()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("keep", "a.txt"), "stay");
        File.WriteAllText(sandbox.File("drop", "b.txt"), "old");

        ScanResult scanned = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        string branch = Path.Combine(sandbox.Root, "drop");
        File.WriteAllText(Path.Combine(branch, "c.txt"), "newer!");
        File.Delete(Path.Combine(branch, "b.txt"));

        ScanResult fresh = DiskScanner.Scan(new ScanOptions { Paths = [branch] });
        scanned.ReplaceFolder(branch, fresh);

        Assert.Equal(2, scanned.Files.Count);
        Assert.Contains(scanned.Files, file => file.Name == "a.txt");
        Assert.Contains(scanned.Files, file => file.Name == "c.txt");
        Assert.DoesNotContain(scanned.Files, file => file.Name == "b.txt");
        Assert.Equal(4 + 6, scanned.Root.Size);
        Assert.NotNull(scanned.FindFolder(branch));
    }

    [Fact]
    public void Export_StopsAfterTheRowLimit()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("a.txt"), "one");
        File.WriteAllText(sandbox.File("b.txt"), "two");
        File.WriteAllText(sandbox.File("c.txt"), "three");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        string csv = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N") + ".csv");
        CsvExporter.Export(result, csv, new CsvExportOptions { IncludeFolders = false, RowLimit = 2 });

        string[] lines = File.ReadAllLines(csv);
        File.Delete(csv);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void PipelinedRead_ReturnsTheBytesInOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N") + ".bin");
        byte[] written = new byte[4096 * 6];
        for (int i = 0; i < written.Length; i++)
            written[i] = (byte)(i * 17);

        File.WriteAllBytes(path, written);
        try
        {
            using var reader = VolumeReader.OpenUnbuffered(path);
            var read = new List<byte>();
            reader.ReadPipelined(4096, 4096 * 4, 4096, chunk => read.AddRange(chunk));

            Assert.Equal(written.AsSpan(4096, 4096 * 4).ToArray(), read);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeleteMany_RemovesMultipleAndNestedTargetsInOneBatch()
    {
        using var sandbox = new Sandbox();
        string keep = sandbox.File("keep.txt");
        string dropA = sandbox.File("folder", "a.txt");
        string dropB = sandbox.File("folder", "nested", "b.txt");
        string dropDir = Path.Combine(sandbox.Root, "folder");
        string dropRootFile = sandbox.File("loose.txt");
        File.WriteAllText(keep, "1234");
        File.WriteAllText(dropA, "aaaaa");
        File.WriteAllText(dropB, "bbbbb");
        File.WriteAllText(dropRootFile, "cc");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        IReadOnlyList<FileEntry> gone = FileActions.DeleteMany(result, [dropB, dropDir, dropRootFile, dropDir], DeleteMode.Permanent);

        Assert.Equal(3, gone.Count);
        Assert.Single(result.Files);
        Assert.Equal(4, result.Root.Size);
        Assert.False(Directory.Exists(dropDir));
        Assert.False(File.Exists(dropRootFile));
        Assert.Null(result.FindFolder(dropDir));
        Assert.NotNull(result.FindFile(keep));
    }

    [Fact]
    public void ScanMonitor_StartWithInitialResult_WatchesWithoutRescanning()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("initial.txt"), "hello");
        var options = new ScanOptions { Paths = [sandbox.Root] };
        ScanResult initial = DiskScanner.Scan(options);

        using var monitor = new ScanMonitor(options, settle: TimeSpan.FromMilliseconds(50));
        using var updated = new ManualResetEventSlim();
        ScanResult? latest = null;
        monitor.Updated += result =>
        {
            latest = result;
            updated.Set();
        };

        ScanResult attached = monitor.Start(initial);
        Assert.Same(initial, attached);
        Assert.True(monitor.IsMonitoring);

        File.WriteAllText(sandbox.File("added.txt"), "world!");
        Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotNull(latest);
        Assert.Equal(2, latest.Files.Count);
        monitor.Stop();
    }

    [Fact]
    public void ResultFilter_KeepsDirectoryNodeBoundToFilteredTree()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("sub", "keep.log"), "ab");
        File.WriteAllText(sandbox.File("sub", "drop.txt"), "xxxxx");

        ScanResult scanned = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        ScanResult filtered = ResultFilter.Apply(scanned, new ScanOptions { Paths = [sandbox.Root], IncludeFilter = "*.log" });

        FileEntry filteredFile = Assert.Single(filtered.Files);
        FolderNode? filteredSub = filtered.FindFolder(Path.Combine(sandbox.Root, "sub"));
        FolderNode? originalSub = scanned.FindFolder(Path.Combine(sandbox.Root, "sub"));

        Assert.NotNull(filteredSub);
        Assert.NotNull(originalSub);
        Assert.NotSame(originalSub, filteredSub);
        Assert.Same(filteredSub, filteredFile.DirectoryNode);
        Assert.Equal(2, scanned.Files.Count);
        Assert.Same(originalSub, Assert.Single(scanned.Files, f => f.Name == "keep.log").DirectoryNode);
    }

    [Fact]
    public void ExtensionSummary_NormalizesMixedCaseExtensions()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("a.DLL"), "12");
        File.WriteAllText(sandbox.File("b.dll"), "345");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        ExtensionTotal dll = Assert.Single(result.Extensions);

        Assert.Equal(".dll", dll.Extension);
        Assert.Equal(2, dll.FileCount);
        Assert.Equal(5, dll.Size);
    }

    [Fact]
    public void ScanTargetList_SkipsUnmountedSystemVolumes()
    {
        IReadOnlyList<ScanTarget> targets = ScanTargetList.Enumerate();
        Assert.DoesNotContain(targets, static target => VolumeMounts.IsVolumeName(target.Path));
    }

    [Fact]
    public void DiskScanner_DoesNotThrowArgumentExceptionForVolumeGuidPath()
    {
        const string missingVolume = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\";
        ScanResult result = DiskScanner.Scan(new ScanOptions
        {
            Paths = [missingVolume],
            AllowSlowFallback = true,
        });

        Assert.Equal(missingVolume, result.RootPath);
        Assert.NotEmpty(result.Warnings);
    }

    /// <summary>A temporary directory that removes itself, including anything a test created inside it.</summary>
    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string File(params string[] segments)
        {
            string path = Path.Combine([Root, .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A file being scanned can briefly stay locked. The next run uses a fresh directory anyway.
            }
        }
    }
}
