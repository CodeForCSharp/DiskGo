using System.Runtime.InteropServices;

namespace DiskGo.Core;

/// <summary>
/// A mapped network drive is a letter in front of a UNC path. Scanning the
/// letter and the UNC path together would count the same files twice, so the
/// letter is rewritten to the UNC path before the two are compared.
/// </summary>
internal static partial class NetworkDrives
{
    /// <summary>The UNC path <paramref name="letter"/> is mapped to, or null when it is not a mapped drive.</summary>
    public static string? TargetOf(char letter)
    {
        if (!char.IsAsciiLetter(letter))
            return null;

        var buffer = new char[1024];
        int length = buffer.Length;
        int result = WNetGetConnection(char.ToUpperInvariant(letter) + ":", ref buffer[0], ref length);
        if (result != 0)
            return null;

        int end = buffer.AsSpan().IndexOf('\0');
        string target = end < 0 ? new string(buffer) : new string(buffer, 0, end);
        return target.StartsWith(@"\\", StringComparison.Ordinal) ? target.TrimEnd('\\') : null;
    }

    /// <summary>Rewrites a path on a mapped drive so it starts with the UNC path. Anything else is returned unchanged.</summary>
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

    [LibraryImport("mpr.dll", EntryPoint = "WNetGetConnectionW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WNetGetConnection(string local, ref char remote, ref int length);
}
