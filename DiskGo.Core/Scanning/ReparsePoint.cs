using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskGo.Core;

/// <summary>
/// Where a junction or a symbolic link points, when it could be read.
/// <see cref="IsLink"/> is false for the other reparse tags (cloud placeholders,
/// system compression): those are real files and have no separate target.
/// </summary>
public readonly record struct ReparseInfo(bool IsLink, string? Target);

/// <summary>
/// Reads an NTFS reparse point, either from the bytes stored in its
/// <c>$REPARSE_POINT</c> attribute or from the file itself.
/// </summary>
public static partial class ReparsePoint
{
    private const uint MountPoint = 0xA0000003;
    private const uint SymbolicLink = 0xA000000C;
    private const uint ReadAttributes = 0x0080;
    private const uint ShareAll = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint GetReparsePoint = 0x0009_00A8;

    /// <summary>Reads the reparse data of <paramref name="path"/> without following it.</summary>
    public static bool TryRead(string path, out ReparseInfo info)
    {
        info = default;
        SafeFileHandle handle = CreateFile(path, ReadAttributes, ShareAll, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        try
        {
            var buffer = new byte[16 * 1024];
            if (!DeviceIoControl(handle, GetReparsePoint, 0, 0, buffer, buffer.Length, out int returned, 0) || returned < 8)
                return false;

            return TryParse(buffer.AsSpan(0, returned), out info);
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>Parses a <c>REPARSE_DATA_BUFFER</c>. Returns false when the buffer is too short to be one.</summary>
    public static bool TryParse(ReadOnlySpan<byte> value, out ReparseInfo info)
    {
        info = default;
        if (value.Length < 8)
            return false;

        uint tag = BitConverter.ToUInt32(value);
        bool symlink = tag == SymbolicLink;
        if (tag != MountPoint && !symlink)
        {
            info = new ReparseInfo(false, null);
            return true;
        }

        // Mount points keep the path at byte 16. Symbolic links have a flags
        // field first, so their path starts at byte 20. Offsets are relative to
        // that path, and the printable name is the one a person typed.
        int pathStart = symlink ? 20 : 16;
        if (value.Length < pathStart)
            return false;

        string? print = ReadName(value, pathStart, BitConverter.ToUInt16(value[12..14]), BitConverter.ToUInt16(value[14..16]));
        string? substitute = ReadName(value, pathStart, BitConverter.ToUInt16(value[8..10]), BitConverter.ToUInt16(value[10..12]));
        info = new ReparseInfo(true, Clean(string.IsNullOrEmpty(print) ? substitute : print));
        return true;
    }

    private static string? ReadName(ReadOnlySpan<byte> value, int pathStart, int offset, int length)
    {
        if (length <= 0)
            return null;

        int start = pathStart + offset;
        if (start < 0 || start + length > value.Length)
            return null;

        return System.Text.Encoding.Unicode.GetString(value.Slice(start, length));
    }

    /// <summary>Turns the NTFS form <c>\??\C:\Dir</c> into the path a program can open.</summary>
    private static string? Clean(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (path.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path[4..];

        return path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle handle, uint controlCode, nint input, int inputSize,
        byte[] output, int outputSize, out int returned, nint overlapped);
}
