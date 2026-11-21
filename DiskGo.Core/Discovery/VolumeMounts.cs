using System.Runtime.InteropServices;

namespace DiskGo.Core;

/// <summary>
/// Volumes that do not have a drive letter, and volumes mounted on a folder.
/// Both are real NTFS volumes with their own MFT, so scanning the letter they
/// happen to sit near would miss them or count them with the wrong volume.
/// </summary>
internal static partial class VolumeMounts
{
    /// <summary>True when <paramref name="path"/> is a volume name such as <c>\\?\Volume{guid}\</c> rather than a drive letter or a folder.</summary>
    public static bool IsVolumeName(string path)
        => path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && path.Contains('}');

    /// <summary>Every mounted volume, as <c>\\?\Volume{guid}\</c>.</summary>
    public static List<string> Volumes()
    {
        var volumes = new List<string>();
        var name = new char[256];
        nint find = FindFirstVolume(ref name[0], name.Length);
        if (find == new nint(-1))
            return volumes;

        try
        {
            do
            {
                volumes.Add(Text(name));
            }
            while (FindNextVolume(find, ref name[0], name.Length));
        }
        finally
        {
            FindVolumeClose(find);
        }

        return volumes;
    }

    /// <summary>
    /// Every folder inside <paramref name="root"/> that has a volume mounted on
    /// it, paired with the volume name (<c>\\?\Volume{guid}\</c>) mounted
    /// there. The folder is a reparse point in the parent volume's own scan, so
    /// the files on the mounted volume are not in it and have to be read from
    /// that volume.
    /// </summary>
    public static List<(string Folder, string Volume)> MountedVolumes(string root)
    {
        var mounted = new List<(string, string)>();
        foreach (string relative in MountPoints(root))
        {
            string folder = Path.Combine(root, relative.TrimEnd('\\'));
            var volume = new char[256];
            if (!GetVolumeNameForVolumeMountPoint(folder.TrimEnd('\\') + "\\", ref volume[0], volume.Length))
                continue;

            string name = Text(volume);
            if (IsVolumeName(name))
                mounted.Add((folder, name));
        }

        return mounted;
    }

    /// <summary>The volume paths mounted somewhere inside <paramref name="root"/>.</summary>
    public static List<string> MountPoints(string root)
    {
        var points = new List<string>();
        var name = new char[512];
        // The query refuses a path that does not end in a separator, so a
        // folder scan and a volume root are asked in the same form.
        string query = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        nint find = FindFirstVolumeMountPoint(query, ref name[0], name.Length);
        if (find == new nint(-1))
            return points;

        try
        {
            do
            {
                points.Add(Text(name));
            }
            while (FindNextVolumeMountPoint(find, ref name[0], name.Length));
        }
        finally
        {
            FindVolumeMountPointClose(find);
        }

        return points;
    }

    /// <summary>The drive letters and folder paths a volume is mounted at. Empty when it is not mounted anywhere.</summary>
    public static unsafe List<string> PathsOf(string volume)
    {
        var paths = new List<string>();
        uint needed = 0;
        GetVolumePathNamesForVolumeName(volume, null, 0, ref needed);
        if (needed <= 1)
            return paths;

        var buffer = new char[needed];
        fixed (char* chars = buffer)
        {
            if (!GetVolumePathNamesForVolumeName(volume, chars, needed, ref needed))
                return paths;
        }

        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
                continue;

            if (i > start)
                paths.Add(new string(buffer, start, i - start));
            start = i + 1;
            if (start < buffer.Length && buffer[start] == '\0')
                break;
        }

        return paths;
    }

    /// <summary>Reads total and available free bytes for a drive root, folder mount point, or <c>\\?\Volume{guid}\</c> path.</summary>
    public static bool TryGetSpace(string path, out long totalBytes, out long availableFreeBytes)
    {
        totalBytes = 0;
        availableFreeBytes = 0;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string query = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        if (!GetDiskFreeSpaceEx(query, out ulong freeToCaller, out ulong total, out _))
            return false;

        totalBytes = total > long.MaxValue ? long.MaxValue : (long)total;
        availableFreeBytes = freeToCaller > long.MaxValue ? long.MaxValue : (long)freeToCaller;
        return totalBytes > 0;
    }

    /// <summary>Reads the volume label for a root path such as <c>C:\</c> or <c>\\?\Volume{guid}\</c>, or null if unavailable.</summary>
    public static unsafe string? TryGetLabel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string root = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        char* buffer = stackalloc char[261];
        if (!GetVolumeInformation(root, buffer, 261, out _, out _, out _, 0, 0))
            return null;

        string label = Text(new ReadOnlySpan<char>(buffer, 261));
        return string.IsNullOrWhiteSpace(label) ? null : label;
    }

    /// <summary>The characters of a Windows string buffer, up to the first null.</summary>
    private static string Text(ReadOnlySpan<char> chars)
    {
        int end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    // The name arguments are the caller's own buffer. A StringBuilder cannot be
    // marshalled by a source-generated call, and a ref to its first character is
    // the pointer the native function writes into.
    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindFirstVolume(ref char name, int capacity);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextVolume(nint find, ref char name, int capacity);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindVolumeClose(nint find);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindFirstVolumeMountPoint(string root, ref char name, int capacity);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextVolumeMountPoint(nint find, ref char name, int capacity);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindVolumeMountPointClose(nint find);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathNamesForVolumeName(string volume, char* names, uint capacity, ref uint needed);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, ref char volume, int capacity);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformation(
        string rootPathName,
        char* volumeName,
        int volumeNameSize,
        out uint serialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        nint fileSystemName,
        int fileSystemNameSize);
}
