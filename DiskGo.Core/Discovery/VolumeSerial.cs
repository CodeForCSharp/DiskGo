using System.Runtime.InteropServices;

namespace DiskGo.Core;

/// <summary>
/// The serial number Windows stores in a volume's boot sector. It identifies
/// the volume, not the files on it: formatting assigns a new one, and nothing
/// stops two volumes from carrying the same number.
/// </summary>
internal static partial class VolumeSerial
{
    /// <summary>The serial of the volume that contains <paramref name="path"/>, or null when it cannot be read.</summary>
    public static uint? TryRead(string path)
    {
        string root = Path.GetPathRoot(path) ?? "";
        if (root.Length == 0)
            return null;
        if (root[^1] is not ('\\' or '/'))
            root += "\\";

        // The name and file-system buffers are unused. Passing none is valid;
        // a buffer that is merely too short makes the call fail instead.
        return GetVolumeInformation(root, 0, 0, out uint serial, out _, out _, 0, 0)
            ? serial
            : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(
        string rootPathName,
        nint volumeName,
        int volumeNameSize,
        out uint serialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        nint fileSystemName,
        int fileSystemNameSize);
}
