namespace DiskGo.Core;

/// <summary>
/// How a duplicate group is decided. Size alone is intentionally not offered:
/// files that merely share a length are not duplicates.
/// </summary>
/// <summary>Whether an MFT scan reads ahead or waits for each chunk.</summary>
public enum MftReadMode
{
    /// <summary>Overlap the reads when the drive is fast and seeks for free, and read one chunk at a time otherwise.</summary>
    Auto,

    /// <summary>Always keep several reads in flight.</summary>
    Overlapped,

    /// <summary>Always wait for one chunk before asking for the next. The right choice for a spinning disk.</summary>
    Synchronous,
}

public enum DuplicateMode
{
    /// <summary>Do not look for duplicates.</summary>
    None,

    /// <summary>Same file name and logical size.</summary>
    NameAndSize,

    /// <summary>Same file name, logical size and last-write time.</summary>
    NameSizeAndDate,
}

/// <summary>
/// What one scan should cover and how its results should be shaped.
/// </summary>
public sealed class ScanOptions
{
    /// <summary>
    /// One or more drives and folders. A drive root on an NTFS volume is read
    /// from the Master File Table when that is allowed. Every path on the same
    /// volume shares one MFT read. Two paths may not overlap: <c>D:\Work</c>
    /// and <c>D:\Work\Logs</c> together would count the logs twice.
    /// </summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>The first path. Kept for the common case of scanning exactly one place.</summary>
    public string Path => Paths.Count == 0 ? "" : Paths[0];

    /// <summary>
    /// Read the MFT even when <see cref="Path"/> is a folder rather than a
    /// whole volume. This is faster for folders that hold a very large number
    /// of files and slower for small ones.
    /// </summary>
    public bool PreferMftForFolders { get; init; }

    /// <summary>
    /// Keep reading the directory tree when the MFT cannot be read, instead of
    /// failing the scan.
    /// </summary>
    public bool AllowSlowFallback { get; init; } = true;

    /// <summary>Which files count as duplicates. <see cref="DuplicateMode.None"/> skips the pass entirely.</summary>
    public DuplicateMode Duplicates { get; init; } = DuplicateMode.None;

    /// <summary>
    /// Files directly inside one folder that are smaller than this many bytes are
    /// folded into a single node of that folder. Zero, the default, leaves every
    /// file where it was found. The file list itself is unchanged.
    /// </summary>
    public long FileGroupMinimum { get; init; }

    /// <summary>
    /// A folder's modified time is the newest modified time of anything inside
    /// it, instead of the time the file system recorded for the folder itself.
    /// Off, the default, keeps the folder's own time.
    /// </summary>
    public bool ShowNewestModified { get; init; }

    /// <summary>
    /// Glob-style include patterns separated by <c>;</c>, matched against the
    /// file name. Empty keeps every file. Example: <c>*.log;*.tmp</c>.
    /// </summary>
    public string IncludeFilter { get; init; } = "";

    /// <summary>Glob-style exclude patterns separated by <c>;</c>, matched against the file name.</summary>
    public string ExcludeFilter { get; init; } = "";

    /// <summary>Match <see cref="IncludeFilter"/> and <see cref="ExcludeFilter"/> against the full path instead of the file name.</summary>
    public bool FilterFullPath { get; init; }

    /// <summary>
    /// Read <see cref="IncludeFilter"/> and <see cref="ExcludeFilter"/> as one
    /// regular expression each, instead of as a list of <c>*</c> and <c>?</c>
    /// patterns. An expression that does not compile fails the scan.
    /// </summary>
    public bool UseRegex { get; init; }

    /// <summary>
    /// How many threads list directories at once. Zero picks from the number of
    /// processors, between 2 and 8. Only a directory walk uses this; an MFT read
    /// is one stream of records.
    /// </summary>
    public int WalkThreads { get; init; }

    /// <summary>
    /// How the MFT is read. <see cref="MftReadMode.Auto"/> overlaps the reads on
    /// a drive that answers quickly and has no seek penalty, and reads one
    /// chunk at a time otherwise.
    /// </summary>
    public MftReadMode MftRead { get; init; } = MftReadMode.Auto;

    /// <summary>
    /// Keep files marked hidden. Off drops them, which is what Explorer does
    /// when "show hidden files" is off. System files are not hidden files and
    /// stay either way; exclude them with <see cref="ExcludedAttributes"/>.
    /// </summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>
    /// Drop every file that carries any of these attributes. Zero keeps them
    /// all. Combine flags to exclude more than one, for example
    /// <see cref="FileAttributes.System"/> together with
    /// <see cref="FileAttributes.Temporary"/>.
    /// </summary>
    public FileAttributes ExcludedAttributes { get; init; }

    /// <summary>
    /// Count the NTFS metadata files (<c>$MFT</c>, <c>$Bitmap</c>,
    /// <c>$LogFile</c> and the rest of the records numbered below 16). They are
    /// real allocated space that otherwise shows up as none of the files.
    /// Directory walks never see them, so the flag only changes an MFT scan.
    /// </summary>
    public bool IncludeNtfsMetadata { get; init; }

    /// <summary>Report progress at most this often. The final report is always delivered.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The drive letter a dumped MFT belongs to, used only when <see cref="Path"/>
    /// names an MFT dump rather than a place to scan. The dump has no letter of
    /// its own, and the paths it produces start with this one.
    /// </summary>
    public string DumpVolumeRoot { get; init; } = @"C:\";

    /// <summary>Cluster size assumed for a raw MFT dump, which does not record one. A DiskGo dump carries its own.</summary>
    public int DumpClusterSize { get; init; } = 4096;

    /// <summary>An NTFS <c>$Upcase</c> table to read alongside an MFT dump. Empty uses the ordinary case-insensitive comparison.</summary>
    public string UpcaseFile { get; init; } = "";

    /// <summary>
    /// Subst drives collected before this process was elevated, as produced by
    /// <see cref="SessionLists.SubstList"/>. An elevated process cannot see them
    /// itself, so a path on one of these letters is rewritten from the list.
    /// </summary>
    public string SubstList { get; init; } = "";

    /// <summary>
    /// Mapped network drives collected before this process was elevated, as
    /// produced by <see cref="SessionLists.NetworkList"/>. Same reason as
    /// <see cref="SubstList"/>: elevation drops the user's mappings.
    /// </summary>
    public string NetworkList { get; init; } = "";

    /// <summary>Raised from the scan's own thread.</summary>
    public IProgress<ScanProgress>? Progress { get; init; }
}
