using System.Globalization;

namespace DiskGo.Core;

/// <summary>
/// Rebuilds a <see cref="ScanResult"/> from a CSV file written by <see cref="CsvExporter"/>.
///
/// Only the file rows are needed. Folder rows are derived again while the tree
/// is built, which keeps the imported totals consistent even when the export
/// was limited to a folder depth or had its folder rows left out. Columns are
/// matched by their heading, so an export that omitted the optional columns
/// still imports.
///
/// A file whose header is separated by tabs is read as tab separated, which is
/// how a TSV export comes back. Everything else is read as comma separated,
/// including the CSV files other disk tools write: those name the columns
/// differently, and the names below are recognised alongside DiskGo's own.
/// </summary>
public static class CsvImporter
{
    public static ScanResult Import(string filePath, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(filePath);
        string? headerLine = reader.ReadLine();
        if (headerLine is null)
            throw new InvalidDataException("The CSV file is empty.");

        char separator = headerLine.Contains('\t') && !headerLine.Contains(',') ? '\t' : ',';
        Dictionary<string, int> columns = Index(Split(headerLine, separator));
        if (!columns.ContainsKey("Path") && !columns.ContainsKey("Name")
            && !columns.ContainsKey("Full Path") && !columns.ContainsKey("File Name")
            && !columns.ContainsKey("FILENAME") && !columns.ContainsKey("ROOT"))
            throw new InvalidDataException("The CSV file has no Path column.");

        var files = new List<FileEntry>();
        var capacities = new List<(long Total, long Free, long Used, long Reserved)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
                continue;

            string[] cells = Split(line, separator);
            int typeColumn = Column(columns, "Type");
            if (typeColumn >= 0 && typeColumn < cells.Length && !cells[typeColumn].Equals("File", StringComparison.OrdinalIgnoreCase))
                continue;

            string path = First(cells, columns, "Path", "Full Path", "File Name");
            if (path.Length == 0)
                path = CombineSplitPath(cells, columns);
            if (path.Length == 0)
                continue;
            if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
                continue;

            files.Add(new FileEntry
            {
                FullPath = path,
                Name = Path.GetFileName(path),
                Extension = Path.GetExtension(path),
                Size = Integer(cells, columns, "Size", "Logical Size"),
                AllocatedSize = Integer(cells, columns, "Allocated", "Allocated Size", "Size on Disk"),
                Created = When(cells, columns, "Created", "Creation Time", "CREATEDDATE"),
                Modified = When(cells, columns, "Modified", "Last Modified", "Date Modified"),
                Accessed = When(cells, columns, "Accessed", "Last Accessed", "LASTACCESSDATE"),
                Attributes = Attributes(cells, columns),
                MftRecordNumber = (ulong)Integer(cells, columns, "MFT Record", "MFT Record Number", "MFTRECNO"),
                ParentRecordNumber = (ulong)Integer(cells, columns, "Parent Record", "MFTPARENTRECNO"),
                ShortName = First(cells, columns, "Short Name", "DOS Name"),
            });
            capacities.Add((
                Integer(cells, columns, "Drive Capacity", "DRIVECAPACITY"),
                Integer(cells, columns, "Free Space", "FREESPACE"),
                Integer(cells, columns, "Used Space", "USEDSPACE"),
                Integer(cells, columns, "Reserved Space", "RESERVEDSPACE")));
        }

        if (files.Count == 0)
            throw new InvalidDataException("The CSV file does not contain any files.");

        return Build(files, Capacity(capacities));
    }

    private static ScanResult Build(List<FileEntry> files, DriveSpace capacity)
    {
        string rootPath = CommonRoot(files);
        var root = new FolderNode(RootName(rootPath), rootPath);
        foreach (FileEntry file in files)
        {
            string? directory = Path.GetDirectoryName(file.FullPath);
            FolderNode folder = directory is null || directory.Length == 0 ? root : FolderFor(root, rootPath, directory);
            folder.AddFile(file);
        }

        root.Seal();
        root.SortBySizeDescending();
        return new ScanResult(root.FullPath, ScanMethod.DirectoryWalk, false, root, files, ExtensionSummary.From(files), capacity, TimeSpan.Zero, []);
    }

    /// <summary>
    /// The volume size written on the rows, when every row agrees. A file that
    /// was exported without it, or two files from different volumes, leave it unknown.
    /// </summary>
    private static DriveSpace Capacity(List<(long Total, long Free, long Used, long Reserved)> rows)
    {
        if (rows.Count == 0 || rows[0].Total <= 0)
            return DriveSpace.Unknown;

        (long total, long free, long used, long reserved) = rows[0];
        foreach ((long rowTotal, long rowFree, long rowUsed, long rowReserved) in rows)
        {
            if (rowTotal != total || rowFree != free)
                return DriveSpace.Unknown;
            _ = (rowUsed, rowReserved);
        }

        // Reserved is used minus the bytes the files account for, so the
        // accounted figure is recovered from the reserved column rather than stored.
        long accounted = reserved >= 0 && used >= reserved ? used - reserved : -1;
        return new DriveSpace(total, free, accounted);
    }

    private static FolderNode FolderFor(FolderNode root, string rootPath, string directory)
    {
        string relative = directory.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) && directory.Length > rootPath.Length
            ? directory[rootPath.Length..]
            : directory;

        FolderNode node = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            node = node.GetOrAddChild(segment);
        return node;
    }

    private static string CommonRoot(List<FileEntry> files)
    {
        string root = Path.GetDirectoryName(files[0].FullPath) ?? "";
        foreach (FileEntry file in files)
        {
            string directory = Path.GetDirectoryName(file.FullPath) ?? "";
            while (root.Length > 0 && !directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
                if (parent is null || parent.Equals(root, StringComparison.OrdinalIgnoreCase))
                    return "";
                root = parent;
            }
        }

        return root.Length == 2 && root[1] == ':' ? root + Path.DirectorySeparatorChar : root;
    }

    private static string RootName(string rootPath)
        => rootPath.Length == 0 ? "Imported" : rootPath.TrimEnd(Path.DirectorySeparatorChar);

    private static Dictionary<string, int> Index(string[] headings)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headings.Length; i++)
            columns.TryAdd(headings[i].Trim(), i);
        return columns;
    }

    private static int Column(Dictionary<string, int> columns, string name)
        => columns.TryGetValue(name, out int index) ? index : -1;

    private static string Value(string[] cells, Dictionary<string, int> columns, string name)
    {
        int index = Column(columns, name);
        return index < 0 || index >= cells.Length ? "" : cells[index];
    }

    /// <summary>The first of <paramref name="names"/> that the file actually has a column for.</summary>
    private static string First(string[] cells, Dictionary<string, int> columns, params string[] names)
    {
        foreach (string name in names)
        {
            string value = Value(cells, columns, name);
            if (value.Length > 0)
                return value;
        }

        return "";
    }

    private static long Integer(string[] cells, Dictionary<string, int> columns, params string[] names)
        => long.TryParse(First(cells, columns, names), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long value) ? value : 0;

    private static DateTime When(string[] cells, Dictionary<string, int> columns, params string[] names)
    {
        string text = First(cells, columns, names);
        if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact))
            return exact;

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value) ? value : DateTime.MinValue;
    }

    private static FileAttributes Attributes(string[] cells, Dictionary<string, int> columns)
        => Enum.TryParse(Value(cells, columns, "Attributes"), out FileAttributes value) ? value : 0;

    private static string CombineSplitPath(string[] cells, Dictionary<string, int> columns)
    {
        // ROOT, FOLDERNAME, FILENAME and FILEEXT are one path split into four.
        // FILEEXT is stored without its dot. The older Directory and Name pair
        // is the same idea with two columns.
        string root = Value(cells, columns, "ROOT");
        string folder = Value(cells, columns, "FOLDERNAME");
        string fileName = Value(cells, columns, "FILENAME");
        if (root.Length > 0 || folder.Length > 0 || fileName.Length > 0)
        {
            string extension = Value(cells, columns, "FILEEXT");
            if (extension.Length > 0 && !fileName.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
                fileName += "." + extension.TrimStart('.');

            string directory = root.Length == 0 ? folder : folder.Length == 0 ? root : Path.Combine(root, folder);
            return fileName.Length == 0 ? directory : directory.Length == 0 ? fileName : Path.Combine(directory, fileName);
        }

        string plainDirectory = Value(cells, columns, "Directory");
        string plainName = Value(cells, columns, "Name");
        return plainDirectory.Length == 0 || plainName.Length == 0 ? plainName : Path.Combine(plainDirectory, plainName);
    }

    /// <summary>Splits one line, honouring quotes and the doubled quotes used to escape a quote.</summary>
    private static string[] Split(string line, char separator)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (quoted && character == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == separator && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        cells.Add(current.ToString());
        return [.. cells];
    }
}
