using System.Globalization;
using System.Text;

namespace DiskGo.Core;

/// <summary>How the columns of an export are separated.</summary>
public enum ExportFormat
{
    /// <summary>Comma separated, which is what spreadsheets open by default.</summary>
    Csv,

    /// <summary>Tab separated. Tabs inside a field are still quoted, so a name cannot shift the columns.</summary>
    Tsv,
}

/// <summary>Which columns <see cref="CsvExporter"/> writes, and which rows qualify.</summary>
public sealed class CsvExportOptions
{
    /// <summary>Comma or tab separated. The importer reads either.</summary>
    public ExportFormat Format { get; init; } = ExportFormat.Csv;
    public bool IncludeFiles { get; init; } = true;

    public bool IncludeFolders { get; init; } = true;

    /// <summary>Write only the files that belong to a duplicate group.</summary>
    public bool DuplicatesOnly { get; init; }

    /// <summary>Add the created and last-access timestamps. Last write is always present.</summary>
    public bool IncludeAllDates { get; init; }

    /// <summary>Add the allocated size. The logical size is always present.</summary>
    public bool IncludeAllocatedSize { get; init; } = true;

    /// <summary>Add the size a folder occupies directly, excluding everything inside its subfolders.</summary>
    public bool IncludeOwnSize { get; init; }

    /// <summary>Add the file record number. It is only meaningful for rows that came from an MFT scan.</summary>
    public bool IncludeMftRecord { get; init; }

    /// <summary>Add the record number of the directory that contains the file.</summary>
    public bool IncludeParentRecord { get; init; }

    /// <summary>Add the DOS 8.3 short name, when the file has one.</summary>
    public bool IncludeShortName { get; init; }

    /// <summary>Add the share of the whole volume, which needs the volume size to be known.</summary>
    public bool IncludePercentOfDrive { get; init; }

    /// <summary>Add the volume's total, free, used and reserved bytes on every row.</summary>
    public bool IncludeDriveCapacity { get; init; }

    /// <summary>Stop after this many rows. Zero writes every row.</summary>
    public int RowLimit { get; init; }

    /// <summary>
    /// Write only these paths and, for a folder, everything inside it. Empty
    /// writes the whole scan. A path the scan does not contain is skipped.
    /// </summary>
    public IReadOnlyList<string> OnlyPaths { get; init; } = [];

    /// <summary>Split every path into the drive, the directory, the file name and the extension.</summary>
    public bool SplitPath { get; init; }

    /// <summary>Add the share of the parent folder's size.</summary>
    public bool IncludePercentOfParent { get; init; }

    /// <summary>How many directory levels below the root to include. Zero includes everything.</summary>
    public int MaxDepth { get; init; }

    /// <summary>Write timestamps in UTC instead of local time.</summary>
    public bool UseUtc { get; init; }
}

/// <summary>
/// Writes a scan to a CSV or TSV file with configurable columns and scope. Fields are quoted
/// in the usual way, so names containing commas or quotation marks survive a round trip.
/// </summary>
public static class CsvExporter
{
    public static void Export(ScanResult result, string filePath, CsvExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new CsvExportOptions();
        if (!options.IncludeFiles && !options.IncludeFolders)
            throw new ArgumentException("Export files, folders, or both.", nameof(options));

        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        char separator = options.Format == ExportFormat.Tsv ? '\t' : ',';
        var remaining = new RowBudget(options.RowLimit);
        WriteHeader(writer, options, separator);

        if (options.OnlyPaths.Count > 0)
        {
            foreach (string path in options.OnlyPaths)
                WriteSelection(writer, result, path, options, separator, remaining);
            return;
        }

        if (options.IncludeFolders)
            WriteFolder(writer, result.Root, result, options, separator, remaining, depth: 0);

        if (options.IncludeFiles && !options.IncludeFolders)
            WriteLooseFiles(writer, result, result.Files, options, separator, remaining);
    }

    /// <summary>Writes one selected path: a file on its own, or a folder and everything the options allow inside it.</summary>
    private static void WriteSelection(StreamWriter writer, ScanResult result, string path, CsvExportOptions options, char separator, RowBudget remaining)
    {
        if (!remaining.HasMore)
            return;

        FileEntry? file = result.FindFile(path);
        if (file is not null)
        {
            if (options.IncludeFiles && !(options.DuplicatesOnly && file.DuplicateCount < 2))
            {
                WriteFile(writer, file, result, ParentSize(result.Root, file), options, separator);
                remaining.Take();
            }

            return;
        }

        FolderNode? folder = result.FindFolder(path);
        if (folder is null)
            return;

        if (options.IncludeFolders)
            WriteFolder(writer, folder, result, options, separator, remaining, depth: 0);
        else if (options.IncludeFiles)
            WriteLooseFiles(writer, result, FilesInside(folder), options, separator, remaining);
    }

    private static void WriteLooseFiles(StreamWriter writer, ScanResult result, IEnumerable<FileEntry> files, CsvExportOptions options, char separator, RowBudget remaining)
    {
        foreach (FileEntry file in files)
        {
            if (!remaining.HasMore)
                return;
            if (options.DuplicatesOnly && file.DuplicateCount < 2)
                continue;
            if (ExceedsDepth(file.FullPath, result.Root.FullPath, options.MaxDepth))
                continue;

            WriteFile(writer, file, result, ParentSize(result.Root, file), options, separator);
            remaining.Take();
        }
    }

    private static List<FileEntry> FilesInside(FolderNode folder)
    {
        var files = new List<FileEntry>(folder.Files);
        foreach (FolderNode child in folder.Children)
            files.AddRange(FilesInside(child));
        return files;
    }

    private static void WriteFolder(StreamWriter writer, FolderNode folder, ScanResult result, CsvExportOptions options, char separator, RowBudget remaining, int depth)
    {
        if (!remaining.HasMore || options.MaxDepth > 0 && depth > options.MaxDepth)
            return;

        WriteRow(writer, options, separator, result, "Folder", folder.FullPath, folder.Size, folder.AllocatedSize,
            folder.OwnSize, folder.OwnAllocatedSize, folder.FileCount, folder.Modified,
            folder.PercentOfParent, mftRecord: 0, parentRecord: 0, shortName: "", duplicateCount: 0);
        remaining.Take();

        if (options.IncludeFiles)
        {
            foreach (FileEntry file in folder.Files)
            {
                if (!remaining.HasMore)
                    return;
                if (options.DuplicatesOnly && file.DuplicateCount < 2)
                    continue;

                WriteFile(writer, file, result, folder.Size, options, separator);
                remaining.Take();
            }
        }

        foreach (FolderNode child in folder.ChildrenBySize())
            WriteFolder(writer, child, result, options, separator, remaining, depth + 1);
    }

    private static void WriteFile(StreamWriter writer, FileEntry file, ScanResult result, long parentSize, CsvExportOptions options, char separator)
    {
        double percent = parentSize == 0 ? 0d : 100d * file.Size / parentSize;
        WriteRow(writer, options, separator, result, "File", file.FullPath, file.Size, file.AllocatedSize,
            ownSize: 0, ownAllocated: 0, fileCount: 1, file.Modified,
            percent, file.MftRecordNumber, file.ParentRecordNumber, file.ShortName, file.DuplicateCount, file);
    }

    /// <summary>How many data rows are still allowed. A limit of zero means there is no limit.</summary>
    private sealed class RowBudget
    {
        private readonly int _limit;
        private int _left;

        public RowBudget(int limit)
        {
            _limit = limit;
            _left = limit;
        }

        public bool HasMore => _limit <= 0 || _left > 0;

        public void Take()
        {
            if (_limit > 0)
                _left--;
        }
    }

    private static void WriteHeader(StreamWriter writer, CsvExportOptions options, char separator)
    {
        var columns = new List<string> { "Type" };
        if (options.SplitPath)
            columns.AddRange(["ROOT", "FOLDERNAME", "FILENAME", "FILEEXT"]);
        else
            columns.Add("Path");

        columns.Add("Size");
        if (options.IncludeAllocatedSize)
            columns.Add("Allocated");
        if (options.IncludeOwnSize)
            columns.AddRange(["Own Size", "Own Allocated"]);
        if (options.IncludePercentOfParent)
            columns.Add("% of Parent");

        columns.AddRange(["Files", "Modified"]);
        if (options.IncludeAllDates)
            columns.AddRange(["CREATEDDATE", "LASTACCESSDATE"]);
        if (options.IncludeMftRecord)
            columns.Add("MFT Record");
        if (options.IncludeParentRecord)
            columns.Add("Parent Record");
        if (options.IncludeShortName)
            columns.Add("Short Name");
        if (options.IncludePercentOfDrive)
            columns.Add("% of Drive");
        if (options.IncludeDriveCapacity)
            columns.AddRange(["Drive Capacity", "Free Space", "Used Space", "Reserved Space"]);
        columns.Add("Attributes");
        columns.Add("Duplicates");

        writer.WriteLine(string.Join(separator, columns.Select(value => Quote(value, separator))));
    }

    private static void WriteRow(
        StreamWriter writer, CsvExportOptions options, char separator, ScanResult result, string type, string path,
        long size, long allocated, long ownSize, long ownAllocated, long fileCount,
        DateTime? modified, double percentOfParent, ulong mftRecord, ulong parentRecord, string shortName, int duplicateCount,
        FileEntry? file = null)
    {
        var cells = new List<string> { type };
        if (options.SplitPath)
        {
            // The folder column is the directory without the drive, and without
            // a leading separator, so the four columns join back into the path.
            string root = Path.GetPathRoot(path) ?? "";
            string directory = Path.GetDirectoryName(path) ?? "";
            string folder = directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? directory[root.Length..] : directory;
            cells.Add(root);
            cells.Add(folder.Trim(Path.DirectorySeparatorChar));
            cells.Add(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
            cells.Add(file is null ? "" : file.Extension.TrimStart('.'));
        }
        else
        {
            cells.Add(path);
        }

        cells.Add(size.ToString(CultureInfo.InvariantCulture));
        if (options.IncludeAllocatedSize)
        {
            // A hard link after the first one occupies no more clusters. A leading
            // zero is how that is written, and it still reads back as the same number.
            string allocatedText = allocated.ToString(CultureInfo.InvariantCulture);
            cells.Add(file is { CountsTowardTotal: false } ? "0" + allocatedText : allocatedText);
        }
        if (options.IncludeOwnSize)
        {
            cells.Add(ownSize.ToString(CultureInfo.InvariantCulture));
            cells.Add(ownAllocated.ToString(CultureInfo.InvariantCulture));
        }

        if (options.IncludePercentOfParent)
            cells.Add(percentOfParent.ToString("0.00", CultureInfo.InvariantCulture));

        cells.Add(fileCount.ToString(CultureInfo.InvariantCulture));
        cells.Add(Format(modified, options.UseUtc));
        if (options.IncludeAllDates)
        {
            cells.Add(file is null ? "" : Format(file.Created, options.UseUtc));
            cells.Add(file is null ? "" : Format(file.Accessed, options.UseUtc));
        }

        if (options.IncludeMftRecord)
            cells.Add(mftRecord == 0 ? "" : mftRecord.ToString(CultureInfo.InvariantCulture));
        if (options.IncludeParentRecord)
            cells.Add(parentRecord == 0 ? "" : parentRecord.ToString(CultureInfo.InvariantCulture));
        if (options.IncludeShortName)
            cells.Add(shortName);
        if (options.IncludePercentOfDrive)
            cells.Add(result.Drive.PercentOf(size).ToString("0.00", CultureInfo.InvariantCulture));
        if (options.IncludeDriveCapacity)
        {
            cells.Add(result.Drive.Total.ToString(CultureInfo.InvariantCulture));
            cells.Add(result.Drive.Free.ToString(CultureInfo.InvariantCulture));
            cells.Add(result.Drive.Used.ToString(CultureInfo.InvariantCulture));
            cells.Add(result.Drive.Reserved.ToString(CultureInfo.InvariantCulture));
        }

        cells.Add(file is null ? "" : file.Attributes.ToString());
        cells.Add(duplicateCount > 1 ? duplicateCount.ToString(CultureInfo.InvariantCulture) : "");

        writer.WriteLine(string.Join(separator, cells.Select(value => Quote(value, separator))));
    }

    private static string Format(DateTime? value, bool utc)
    {
        if (value is not DateTime time || time == DateTime.MinValue)
            return "";

        DateTime shown = utc ? time.ToUniversalTime() : time;
        return shown.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static bool ExceedsDepth(string fullPath, string rootPath, int maxDepth)
    {
        if (maxDepth <= 0)
            return false;

        string relative = fullPath.Length > rootPath.Length ? fullPath[rootPath.Length..] : "";
        int separators = relative.Count(static character => character == Path.DirectorySeparatorChar);
        return separators > maxDepth;
    }

    private static long ParentSize(FolderNode root, FileEntry file)
    {
        string? directory = Path.GetDirectoryName(file.FullPath);
        return directory is null ? root.Size : Find(root, directory)?.Size ?? root.Size;
    }

    private static FolderNode? Find(FolderNode node, string path)
    {
        if (string.Equals(node.FullPath.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return node;

        foreach (FolderNode child in node.Children)
        {
            if (Find(child, path) is FolderNode found)
                return found;
        }

        return null;
    }

    private static string Quote(string value, char separator)
    {
        bool special = value.Contains(separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return special ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
