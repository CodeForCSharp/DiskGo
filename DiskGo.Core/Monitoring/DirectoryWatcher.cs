using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskGo.Core;

/// <summary>
/// Watches one directory for changes by calling <c>ReadDirectoryChangesW</c>
/// with an overlapped read. The call returns at once and the system completes
/// it when something changes, or when the handle is closed by <see cref="Stop"/>.
///
/// The operating system only queues a limited number of changes. When that
/// queue overflows the individual names are lost, and the watcher reports a
/// single <see cref="FileChangeKind.Overflow"/> so the caller knows it must
/// look at the folder again instead of trusting the list.
/// </summary>
internal sealed partial class DirectoryWatcher : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint ShareAll = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint Overlapped = 0x4000_0000;
    private const int ErrorIoPending = 997;
    private const int ErrorNotifyEnumDir = 1022;
    private const uint WaitObject = 0;
    private const uint Infinite = 0xFFFF_FFFF;

    // Name, size, timestamps and attributes. Security changes do not move disk space, so they are ignored.
    private const uint NotifyFilter = 0x0001 | 0x0002 | 0x0004 | 0x0008 | 0x0010 | 0x0040;

    private readonly string _path;
    private readonly bool _includeSubfolders;
    private readonly byte[] _buffer;
    private SafeFileHandle? _handle;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private Action<IReadOnlyList<FileChange>>? _onChanges;
    private volatile bool _stop;

    public DirectoryWatcher(string path, bool includeSubfolders, int bufferBytes = 64 * 1024)
    {
        _path = path;
        _includeSubfolders = includeSubfolders;
        _buffer = new byte[bufferBytes];
    }

    public void Start(Action<IReadOnlyList<FileChange>> onChanges)
    {
        if (_thread is { IsAlive: true })
            throw new InvalidOperationException($"{_path} is already being monitored. Stop the monitor before watching it again.");

        if (!Directory.Exists(_path))
            throw new DirectoryNotFoundException($"Cannot monitor {_path}, because it is not a directory.");

        SafeFileHandle handle = CreateFile(_path, FileListDirectory, ShareAll, 0, OpenExisting, BackupSemantics | Overlapped, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Cannot monitor {_path} (Win32 error {error}).", error);
        }

        _onChanges = onChanges;
        _stop = false;
        _ready.Reset();
        _handle = handle;
        _thread = new Thread(Loop) { IsBackground = true, Name = "DiskGo.DirectoryWatcher" };
        _thread.Start();

        // A change that lands before the first read is issued is lost, so the
        // caller must not be told the watch is running until that read is pending.
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new IOException($"The monitor for {_path} did not start.");
    }

    public void Stop()
    {
        _stop = true;

        // Closing the handle completes the pending read, which is what unblocks the thread.
        SafeFileHandle? handle = _handle;
        _handle = null;
        handle?.Dispose();

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        using var signal = new ManualResetEvent(false);
        // DangerousGetHandle is only valid while the handle is held. Releasing it
        // lets the handle be closed underneath the pending read, which then
        // never completes.
        bool held = false;
        signal.SafeWaitHandle.DangerousAddRef(ref held);
        var overlapped = new NativeOverlapped { EventHandle = signal.SafeWaitHandle.DangerousGetHandle() };
        try
        {
            ReadUntilStopped(signal, ref overlapped);
        }
        finally
        {
            if (held)
                signal.SafeWaitHandle.DangerousRelease();
        }
    }

    private void ReadUntilStopped(ManualResetEvent signal, ref NativeOverlapped overlapped)
    {

        while (!_stop)
        {
            SafeFileHandle? handle = _handle;
            if (handle is null || handle.IsClosed || handle.IsInvalid)
                return;

            signal.Reset();
            bool read = ReadDirectoryChangesW(
                handle, _buffer, (uint)_buffer.Length, _includeSubfolders, NotifyFilter, out _, ref overlapped, 0);
            int error = Marshal.GetLastWin32Error();
            _ready.Set();
            if (!read && error != ErrorIoPending)
            {
                if (_stop)
                    return;

                if (error == ErrorNotifyEnumDir)
                {
                    Publish([new FileChange(FileChangeKind.Overflow, _path)]);
                    continue;
                }

                return;
            }

            if (WaitForSingleObject(signal.SafeWaitHandle, Infinite) != WaitObject || _stop)
                return;

            if (!GetOverlappedResult(handle, ref overlapped, out uint returned, false))
            {
                if (_stop)
                    return;

                if (Marshal.GetLastWin32Error() == ErrorNotifyEnumDir)
                    Publish([new FileChange(FileChangeKind.Overflow, _path)]);
                continue;
            }

            if (returned == 0)
            {
                Publish([new FileChange(FileChangeKind.Overflow, _path)]);
                continue;
            }

            // Copy before the next read reuses the buffer. Parsing the buffer in
            // place would race with that read and drop or corrupt the names.
            var copy = new byte[returned];
            System.Buffer.BlockCopy(_buffer, 0, copy, 0, (int)returned);
            Publish(Parse(copy, copy.Length));
        }
    }

    private void Publish(IReadOnlyList<FileChange> changes)
    {
        if (_stop || changes.Count == 0)
            return;

        try
        {
            _onChanges?.Invoke(changes);
        }
        catch (Exception)
        {
            // A listener that throws must not take the watch down with it.
        }
    }

    private List<FileChange> Parse(byte[] buffer, int length)
    {
        var changes = new List<FileChange>();
        int offset = 0;
        while (offset + 12 <= length)
        {
            int next = BitConverter.ToInt32(buffer, offset);
            int action = BitConverter.ToInt32(buffer, offset + 4);
            int nameBytes = BitConverter.ToInt32(buffer, offset + 8);
            if (nameBytes < 0 || offset + 12 + nameBytes > length)
                break;

            string relative = Encoding.Unicode.GetString(buffer, offset + 12, nameBytes);
            if (KindOf(action) is FileChangeKind kind)
                changes.Add(new FileChange(kind, Path.Combine(_path, relative)));

            if (next <= 0 || next > length - offset)
                break;

            offset += next;
        }

        return changes;
    }

    private static FileChangeKind? KindOf(int action) => action switch
    {
        1 => FileChangeKind.Added,
        2 => FileChangeKind.Removed,
        3 => FileChangeKind.Modified,
        4 => FileChangeKind.RenamedFrom,
        5 => FileChangeKind.RenamedTo,
        _ => null,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        public nint Internal;
        public nint InternalHigh;
        public int Offset;
        public int OffsetHigh;
        public nint EventHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadDirectoryChangesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadDirectoryChangesW(
        SafeFileHandle handle, byte[] buffer, uint bufferLength, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint filter,
        out uint bytesReturned, ref NativeOverlapped overlapped, nint completion);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOverlappedResult(SafeFileHandle handle, ref NativeOverlapped overlapped, out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
