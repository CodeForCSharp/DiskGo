using System.Runtime.InteropServices;

namespace DiskGo.Core;

/// <summary>
/// A drive letter created by <c>subst</c> is not a volume of its own. It is a
/// path on some other volume, so reading its MFT would scan that volume a
/// second time and count every file twice. These helpers turn such a letter
/// back into the path it stands for.
/// </summary>
internal static partial class SubstDrives
{
    /// <summary>
    /// The path a subst drive points at, or null when <paramref name="letter"/>
    /// is an ordinary volume. The result has no trailing separator unless it is
    /// a drive root.
    /// </summary>
    public static string? TargetOf(char letter)
    {
        if (!char.IsAsciiLetter(letter))
            return null;

        var buffer = new char[512];
        uint length = QueryDosDevice(letter.ToString().ToUpperInvariant() + ":", ref buffer[0], (uint)buffer.Length);
        if (length == 0)
            return null;

        // A subst drive reports "\??\C:\Real\Path". A real volume reports a
        // device path such as "\Device\HarddiskVolume3", which is not a path
        // anyone can scan, so it is left alone. The count includes the null.
        int end = buffer.AsSpan(0, (int)Math.Min(length, buffer.Length)).IndexOf('\0');
        string reported = end < 0 ? new string(buffer, 0, (int)Math.Min(length, buffer.Length)) : new string(buffer, 0, end);
        if (!reported.StartsWith(@"\??\", StringComparison.Ordinal))
            return null;

        string target = reported[4..];
        if (target.Length < 3 || target[1] != ':' || target[2] != '\\')
            return null;

        return ScanTargets.IsVolumeRoot(target) ? char.ToUpperInvariant(target[0]) + @":\" : target.TrimEnd('\\');
    }

    /// <summary>
    /// Rewrites a path that starts on a subst drive so it starts on the real
    /// volume instead. A path that does not is returned unchanged.
    /// </summary>
    public static string Resolve(string path)
    {
        char letter = ScanTargets.DriveLetter(path);
        if (letter == '\0')
            return path;

        string? target = TargetOf(letter);
        if (target is null)
            return path;

        string relative = path.Length <= 3 ? "" : path[3..];
        return relative.Length == 0 ? target : Path.Combine(target, relative);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint QueryDosDevice(string deviceName, ref char target, uint capacity);
}
