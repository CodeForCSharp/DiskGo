using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace DiskGo;

internal static class ShellHelpers
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RestartAsAdministrator()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void OpenNewWindow()
    {
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    public static void OpenItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// The Windows.Storage pickers fail with E_FAIL while this process is elevated,
    /// which the manifest requires so the MFT can be read. The Windows App SDK pickers
    /// are the ones that work from an administrator desktop app.
    /// </summary>
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(App.Window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = "选择此文件夹",
        };
        PickFolderResult result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    public static async Task<string?> PickOpenFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker(App.Window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        foreach (string extension in extensions)
            picker.FileTypeFilter.Add(extension);

        PickFileResult result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<string?> PickSaveFileAsync(string suggestedName, string label, string extension)
    {
        var picker = new FileSavePicker(App.Window.AppWindow.Id)
        {
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(label, [extension]);
        PickFileResult result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public static bool ShowFileProperties(string path)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
            Verb = "properties",
            File = path,
            Show = 5, // SW_SHOW
        };
        return ShellExecuteEx(ref info);
    }

    public static void SetClipboardFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0)
            return;

        if (!OpenClipboard(0))
            throw new IOException($"无法打开剪贴板 (Win32 {Marshal.GetLastWin32Error()})。");

        try
        {
            if (!EmptyClipboard())
                throw new IOException($"无法清空剪贴板 (Win32 {Marshal.GetLastWin32Error()})。");

            string joined = string.Join('\0', paths) + "\0\0";
            byte[] pathBytes = Encoding.Unicode.GetBytes(joined);
            const int dropFilesSize = 20;
            nint hDrop = GlobalAlloc(0x0042, (nuint)(dropFilesSize + pathBytes.Length));
            if (hDrop == 0)
                throw new IOException("内存不足，无法复制文件到剪贴板。");

            nint locked = GlobalLock(hDrop);
            try
            {
                var drop = new DropFiles
                {
                    FilesOffset = dropFilesSize,
                    Wide = 1,
                };
                Marshal.StructureToPtr(drop, locked, fDeleteOld: false);
                Marshal.Copy(pathBytes, 0, locked + dropFilesSize, pathBytes.Length);
            }
            finally
            {
                GlobalUnlock(hDrop);
            }

            if (SetClipboardData(15, hDrop) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                GlobalFree(hDrop);
                throw new IOException($"写入剪贴板失败 (Win32 {err})。");
            }

            uint format = RegisterClipboardFormat("Preferred DropEffect");
            if (format != 0)
            {
                nint hEffect = GlobalAlloc(0x0042, 4);
                if (hEffect != 0)
                {
                    nint effLock = GlobalLock(hEffect);
                    try
                    {
                        Marshal.WriteInt32(effLock, cut ? 2 : 1);
                    }
                    finally
                    {
                        GlobalUnlock(hEffect);
                    }

                    if (SetClipboardData(format, hEffect) == 0)
                        GlobalFree(hEffect);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Hwnd;
        public string? Verb;
        public string? File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public nint InstApp;
        public nint IDList;
        public string? Class;
        public nint KeyClass;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public int FilesOffset;
        public int X;
        public int Y;
        public int NonClient;
        public int Wide;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo lpExecInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
