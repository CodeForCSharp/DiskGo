namespace DiskGo.Core;

/// <summary>
/// The paths one scan was asked for, checked for the two selections that would
/// count the same files twice: a folder and something inside it, and the same
/// folder given more than once.
/// </summary>
internal static class ScanTargets
{
    public static string[] Normalize(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            throw new ArgumentException("At least one drive or folder is required.");

        var full = new string[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(paths[i]))
                throw new ArgumentException("A scan path is empty.");

            // A subst letter is a second name for a folder that already lives
            // on another volume. Resolve it first, so selecting both the letter
            // and that folder is caught as the overlap it really is.
            // A portable device has no place in the file system, so it is kept
            // as given. Everything else is a real path, and a subst letter is
            // rewritten to the folder it stands for before the overlap check,
            // so selecting both the letter and that folder is caught.
            full[i] = PortableDevices.IsDevicePath(paths[i]) || VolumeMounts.IsVolumeName(paths[i])
                ? paths[i].Trim()
                : NormalizeOne(NetworkDrives.Resolve(SubstDrives.Resolve(paths[i])));
        }

        for (int i = 0; i < full.Length; i++)
        {
            for (int j = i + 1; j < full.Length; j++)
            {
                if (full[i].Equals(full[j], StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"{Display(full[i])} was given more than once.");

                if (Contains(full[i], full[j]) || Contains(full[j], full[i]))
                    throw new ArgumentException($"{Display(full[i])} and {Display(full[j])} overlap, so they cannot both be scanned.");
            }
        }

        return full;
    }

    public static string NormalizeOne(string path)
    {
        string full = Path.GetFullPath(path);
        return IsVolumeRoot(full) ? DriveRoot(full) : full.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>True when <paramref name="inner"/> is <paramref name="outer"/> itself or lives anywhere inside it.</summary>
    public static bool Contains(string outer, string inner)
    {
        if (inner.Equals(outer, StringComparison.OrdinalIgnoreCase))
            return true;

        // Device paths are joined with '/' because the device has no Windows
        // directories. Treating the scheme colon as a drive would make mtp:a
        // look like it contains mtp:ab.
        if (PortableDevices.IsDevicePath(outer))
            return inner.StartsWith(outer + "/", StringComparison.OrdinalIgnoreCase);

        if (VolumeMounts.IsVolumeName(outer))
            return inner.StartsWith(outer.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        string prefix = outer.EndsWith(Path.DirectorySeparatorChar) ? outer : outer + Path.DirectorySeparatorChar;
        return inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVolumeRoot(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length == 2 && trimmed[1] == ':';
    }

    public static char DriveLetter(string path)
        => path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]) : '\0';

    private static string DriveRoot(string path) => char.ToUpperInvariant(path[0]) + @":\";

    private static string Display(string path) => path;
}
