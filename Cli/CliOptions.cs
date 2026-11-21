using System.Globalization;
using DiskGo.Core;

namespace DiskGo;

/// <summary>Parsed non-scan options and command-line flags for CLI execution.</summary>
internal sealed class CliOptions
{
    public string? ExportPath;
    public string? TypesPath;
    public bool Tsv;
    public bool FilesOnly;
    public bool FoldersOnly;
    public bool DuplicatesOnly;
    public bool SplitPath;
    public int MaxDepth;
    public int RowLimit;
    public bool Utc;
    public bool ParentRecord;
    public bool ShortName;
    public bool PercentOfDrive;
    public bool DriveCapacity;
    public bool CopySize;
    public long FileGroupMinimum;
    public bool Monitor;
    public string? DeletePath;
    public DeleteMode DeleteMode;
    public string? RenamePath;
    public string? RenameTo;
    public string? CopyPath;
    public string? RescanPath;
    public string? TransferPath;
    public string? TransferTo;
    public bool TransferMoves;
    public List<string> Selected { get; } = [];

    public static ScanOptions Parse(string[] args, IProgress<ScanProgress>? progress, out CliOptions switches)
    {
        var paths = new List<string>();
        switches = new CliOptions();
        var duplicates = DuplicateMode.None;
        bool preferMft = false;
        string include = "";
        string exclude = "";
        bool fullPath = false;
        bool includeHidden = true;
        var excluded = default(FileAttributes);
        bool includeMetadata = false;
        bool regex = false;
        int threads = 0;
        bool newestModified = false;
        var mftRead = MftReadMode.Auto;
        string dumpRoot = @"C:\";
        int dumpCluster = 4096;
        string upcase = "";
        string subst = "";
        string network = "";

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--duplicates":
                    duplicates = Next(args, ref index) switch
                    {
                        "none" => DuplicateMode.None,
                        "name" => DuplicateMode.NameAndSize,
                        "date" => DuplicateMode.NameSizeAndDate,
                        string other => throw new ArgumentException($"Unknown duplicate mode '{other}'. Use none, name or date."),
                    };
                    break;

                case "--export":
                    switches.ExportPath = Next(args, ref index);
                    break;

                case "--export-tsv":
                    switches.ExportPath = Next(args, ref index);
                    switches.Tsv = true;
                    break;

                case "--export-types":
                    switches.TypesPath = Next(args, ref index);
                    break;

                case "--files-only":
                    switches.FilesOnly = true;
                    break;

                case "--folders-only":
                    switches.FoldersOnly = true;
                    break;

                case "--duplicates-only":
                    switches.DuplicatesOnly = true;
                    break;

                case "--split-path":
                    switches.SplitPath = true;
                    break;

                case "--max-depth":
                    if (!int.TryParse(Next(args, ref index), NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth) || depth < 0)
                        throw new ArgumentException("--max-depth needs a number that is zero or more.");
                    switches.MaxDepth = depth;
                    break;

                case "--limit":
                    if (!int.TryParse(Next(args, ref index), NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit) || limit < 0)
                        throw new ArgumentException("--limit needs a number that is zero or more.");
                    switches.RowLimit = limit;
                    break;

                case "--utc":
                    switches.Utc = true;
                    break;

                case "--parent-record":
                    switches.ParentRecord = true;
                    break;

                case "--short-name":
                    switches.ShortName = true;
                    break;

                case "--percent-of-drive":
                    switches.PercentOfDrive = true;
                    break;

                case "--drive-capacity":
                    switches.DriveCapacity = true;
                    break;

                case "--copy-size":
                    switches.CopySize = true;
                    break;

                case "--delete":
                    switches.DeletePath = Next(args, ref index);
                    switches.DeleteMode = Next(args, ref index) switch
                    {
                        "recycle" => DeleteMode.RecycleBin,
                        "permanent" => DeleteMode.Permanent,
                        string other => throw new ArgumentException($"Unknown delete mode '{other}'. Use recycle or permanent."),
                    };
                    break;

                case "--rename":
                    switches.RenamePath = Next(args, ref index);
                    switches.RenameTo = Next(args, ref index);
                    break;

                case "--copy-path":
                    switches.CopyPath = Next(args, ref index);
                    break;

                case "--include":
                    include = Next(args, ref index);
                    break;

                case "--exclude":
                    exclude = Next(args, ref index);
                    break;

                case "--full-path":
                    fullPath = true;
                    break;

                case "--regex":
                    regex = true;
                    break;

                case "--threads":
                    if (!int.TryParse(Next(args, ref index), NumberStyles.Integer, CultureInfo.InvariantCulture, out threads) || threads < 1)
                        throw new ArgumentException("--threads needs a number that is one or more.");
                    break;

                case "--mft":
                    preferMft = true;
                    break;

                case "--mft-read":
                    mftRead = Next(args, ref index) switch
                    {
                        "auto" => MftReadMode.Auto,
                        "overlapped" => MftReadMode.Overlapped,
                        "sync" => MftReadMode.Synchronous,
                        string other => throw new ArgumentException($"Unknown MFT read mode '{other}'. Use auto, overlapped or sync."),
                    };
                    break;

                case "--no-hidden":
                    includeHidden = false;
                    break;

                case "--exclude-attributes":
                    excluded = ParseAttributes(Next(args, ref index));
                    break;

                case "--metadata":
                    includeMetadata = true;
                    break;

                case "--monitor":
                    switches.Monitor = true;
                    break;

                case "--rescan":
                    switches.RescanPath = Next(args, ref index);
                    switches.Monitor = true;
                    break;

                case "--upcase":
                    upcase = Next(args, ref index);
                    break;

                case "--volume":
                    string volume = Next(args, ref index);
                    if (volume.Length < 1 || !char.IsAsciiLetter(volume[0]))
                        throw new ArgumentException("--volume needs a drive letter.");
                    dumpRoot = char.ToUpperInvariant(volume[0]) + @":\";
                    break;

                case "--cluster-size":
                    if (!int.TryParse(Next(args, ref index), NumberStyles.Integer, CultureInfo.InvariantCulture, out dumpCluster) || dumpCluster < 512)
                        throw new ArgumentException("--cluster-size needs a number of bytes, at least 512.");
                    break;

                case "--group":
                    if (!long.TryParse(Next(args, ref index), NumberStyles.Integer, CultureInfo.InvariantCulture, out long group) || group < 0)
                        throw new ArgumentException("--group needs a number of bytes, or zero to keep every file on its own.");
                    switches.FileGroupMinimum = group;
                    break;

                case "--newest-modified":
                    newestModified = true;
                    break;

                case "--running-as":
                    break;

                case "--subst":
                    subst = Next(args, ref index);
                    break;

                case "--network":
                    network = Next(args, ref index);
                    break;

                case "--copy":
                    switches.TransferPath = Next(args, ref index);
                    switches.TransferTo = Next(args, ref index);
                    switches.TransferMoves = false;
                    break;

                case "--move":
                    switches.TransferPath = Next(args, ref index);
                    switches.TransferTo = Next(args, ref index);
                    switches.TransferMoves = true;
                    break;

                case "--selected":
                    switches.Selected.Add(Next(args, ref index));
                    break;

                case "--help" or "-h" or "/?":
                    throw new ArgumentException(Usage);

                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option '{arg}'.\n\n{Usage}");
                    paths.Add(arg);
                    break;
            }
        }

        if (paths.Count == 0)
            throw new ArgumentException($"Tell DiskGo what to scan.\n\n{Usage}");

        return new ScanOptions
        {
            Paths = paths,
            Duplicates = duplicates,
            PreferMftForFolders = preferMft,
            IncludeFilter = include,
            ExcludeFilter = exclude,
            FilterFullPath = fullPath,
            IncludeHidden = includeHidden,
            ExcludedAttributes = excluded,
            IncludeNtfsMetadata = includeMetadata,
            UseRegex = regex,
            WalkThreads = threads,
            MftRead = mftRead,
            DumpVolumeRoot = dumpRoot,
            DumpClusterSize = dumpCluster,
            UpcaseFile = upcase,
            SubstList = subst,
            NetworkList = network,
            FileGroupMinimum = switches.FileGroupMinimum,
            ShowNewestModified = newestModified,
            Progress = progress,
        };
    }

    public static string? Take(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index < 0 || index + 1 >= args.Length ? null : args[index + 1];
    }

    private static FileAttributes ParseAttributes(string text)
    {
        var combined = default(FileAttributes);
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse(part, ignoreCase: true, out FileAttributes one))
                throw new ArgumentException($"'{part}' is not a file attribute. Use names such as Hidden, System or Temporary.");

            combined |= one;
        }

        return combined;
    }

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"'{args[index]}' needs a value.");

        return args[++index];
    }

    private const string Usage =
        """
        DiskGo <drive or folder> [more folders] [options]

          --duplicates none|name|date   Find files with the same name and size, optionally also the same modified date
          --include *.log;*.tmp         Only include names that match. Also accepts size, date and length terms
          --exclude *.dll               Skip names that match, in the same form as --include
          --regex                       Read --include and --exclude as one regular expression each
          --full-path                   Match the name part of --include and --exclude against the whole path
          --threads 4                   How many threads list directories at once
          --mft-read auto|overlapped|sync   How the MFT is read. auto overlaps on a fast drive
          --no-hidden                   Leave out files marked hidden
          --exclude-attributes System   Leave out files with any of these attributes, separated by commas
          --metadata                    Count the NTFS metadata files, such as $MFT and $Bitmap
          --mft                         Read the MFT even when scanning folders rather than a whole drive
          --monitor                     Scan once, then scan again whenever those folders change
          --list                        Show the drives, subst folders, volumes without a letter and portable devices that can be scanned

        A portable device is scanned by the mtp: path that --list prints for it.
        A subst drive is scanned as the folder it points at, and a mapped network drive as its UNC path.
        A volume with no drive letter is scanned by the \\?\Volume{...}\ path that --list prints for it.
          --export report.csv           Write the tree to a CSV file
          --export-tsv report.tsv       Write the tree with tabs between the columns
          --files-only                  Export files but not folders
          --folders-only                Export folders but not files
          --duplicates-only             Export only the files that have a duplicate
          --split-path                  Write the drive, directory, name and extension separately
          --max-depth 2                 Stop the export this many folders below the root
          --limit 1000                  Stop the export after this many rows
          --utc                         Write timestamps in UTC
          --parent-record               Add the parent directory's MFT record number
          --short-name                  Add the DOS 8.3 short name
          --percent-of-drive            Add each row's share of the whole volume
          --drive-capacity              Add the volume's total, free, used and reserved bytes
          --copy-size                   Copy the size along with the path
          --export-types types.csv      Write one row per file extension
          --import report.csv           Rebuild a scan from a CSV or TSV file
          --delete <path> recycle       Move one scanned file or folder to the recycle bin
          --delete <path> permanent     Delete one scanned file or folder with no way back
          --rename <path> <new name>    Rename one scanned file or folder
          --copy-path <path>            Copy one scanned path to the clipboard
          --dump-mft C.mft              Copy a drive's MFT out to a file
          DiskGo C.mft --volume D:      Scan a dumped MFT as if it were drive D:
          --upcase upcase.dat           Case table to use with a dumped MFT
          --cluster-size 4096           Cluster size assumed for a raw MFT dump
          --group 65536                 Fold files smaller than this many bytes into one node per folder
          --newest-modified             A folder's modified time is the newest time inside it
          --rescan <folder>             After the first scan, read one folder again and replace that branch
          --subst X:=\\??\\C:\\Real       A subst drive this process cannot see, collected before it was elevated
          --network Z:=\\\\server\\share  A mapped drive this process cannot see, collected the same way
          --copy <path> <folder>        Copy one scanned file or folder into a folder
          --move <path> <folder>        Move one scanned file or folder into a folder
          --selected <path>             Export only this scanned file or folder; repeat to export several
        """;
}
