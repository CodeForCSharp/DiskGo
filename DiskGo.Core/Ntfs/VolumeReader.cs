using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskGo.Core.Ntfs;

/// <summary>
/// Reads bytes straight from a volume, which is what makes an MFT scan fast:
/// it never asks the file system to open each file. Opening <c>\\.\C:</c> for
/// reading requires the process to be elevated, and the handle has to be
/// unbuffered, so every offset and length is a multiple of the sector size.
///
/// The handle is overlapped. <see cref="Read"/> waits for one read.
/// <see cref="ReadPipelined"/> keeps several in flight, so the disk is busy
/// while the caller is still parsing the previous chunk.
/// </summary>
internal sealed unsafe partial class VolumeReader : IDisposable
{
    private const uint GenericRead = 0x8000_0000;
    private const uint ShareReadWrite = 0x0000_0001 | 0x0000_0002;
    private const uint OpenExisting = 3;
    private const uint NoBuffering = 0x2000_0000;
    private const uint OverlappedFlag = 0x4000_0000;
    private const int ErrorIoPending = 997;
    private const int ErrorOperationAborted = 995;
    private const uint WaitObject = 0;
    private const uint WaitTimeout = 258;
    private const uint Infinite = 0xFFFF_FFFF;
    /// <summary>Reads kept in flight on a volume. A dump file uses twice this.</summary>
    internal const int PipelineDepth = 4;

    /// <summary>
    /// How long one outstanding read may sit with no bytes before it is given
    /// up. A drive that never answers a cancel keeps its buffer: freeing it
    /// while the read is still posted would hand the disk a dangling pointer.
    /// </summary>
    internal const uint ReadStallMilliseconds = 60_000;
    private const uint StorageQueryProperty = 0x002D_1400;
    private const uint PropertySeekPenalty = 7;
    private const uint PropertyDevice = 0;
    private const uint PropertyQueryDevice = 0;

    /// <summary>
    /// The buses a drive can be attached by, numbered as <c>STORAGE_BUS_TYPE</c>
    /// reports them. The gaps are buses that exist in the enumeration and are
    /// not named here, because none of them earns an overlapped read.
    /// </summary>
    internal enum StorageBus
    {
        Unknown = 0,
        Scsi = 1,
        Atapi = 2,
        Ata = 3,
        Ieee1394 = 4,
        Ssa = 5,
        Fibre = 6,
        Usb = 7,
        Raid = 8,
        iScsi = 9,
        Sas = 10,
        Sata = 11,
        Sd = 12,
        Mmc = 13,
        Virtual = 14,
        FileBackedVirtual = 15,
        Spaces = 16,
        Nvme = 17,
        Scm = 18,
        Ufs = 19,
    }

    private readonly SafeFileHandle _handle;

    private VolumeReader(SafeFileHandle handle) => _handle = handle;

    public static VolumeReader Open(char driveLetter)
    {
        if (!char.IsAsciiLetter(driveLetter))
            throw new ArgumentException($"'{driveLetter}' is not a drive letter.", nameof(driveLetter));

        string device = @"\\.\" + char.ToUpperInvariant(driveLetter) + ":";
        return OpenPath(device, ShareReadWrite);
    }

    /// <summary>Opens an ordinary file the same way a volume is opened. Used to test the pipeline without a raw volume.</summary>
    internal static VolumeReader OpenUnbuffered(string path)
        => OpenPath(path, ShareReadWrite | 0x0000_0004);

    /// <summary>Opens <paramref name="path"/> the way a volume is opened. <paramref name="path"/> is a device path such as <c>\\.\C:</c>.</summary>
    internal static VolumeReader OpenPath(string path)
        => OpenPath(path, ShareReadWrite);

    private static VolumeReader OpenPath(string path, uint share)
    {
        SafeFileHandle handle = CreateFile(path, GenericRead, share, 0, OpenExisting, NoBuffering | OverlappedFlag, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Cannot open {path} for reading (Win32 error {error}). The MFT can only be read from an elevated process.", error);
        }

        return new VolumeReader(handle);
    }

    /// <summary>
    /// True when the drive has to seek, which is what a spinning disk reports
    /// and a solid-state drive does not. Unknown is treated as no penalty, so a
    /// drive that will not say still gets the faster read.
    /// </summary>
    public bool HasSeekPenalty()
    {
        var query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0), PropertySeekPenalty);
        BitConverter.TryWriteBytes(query.AsSpan(4), PropertyQueryDevice);

        var answer = new byte[16];
        if (!DeviceIoControl(_handle, StorageQueryProperty, query, query.Length, answer, answer.Length, out int returned, 0)
            || returned < 13)
            return false;

        return answer[12] != 0;
    }

    /// <summary>
    /// The same question as <see cref="HasSeekPenalty"/>, asked with a handle
    /// opened only to query the device. A directory walk can call this without
    /// the raw, unbuffered handle an MFT read needs, and without failing when
    /// that handle is refused.
    /// </summary>
    public static bool VolumeSeeks(char driveLetter)
    {
        if (!char.IsAsciiLetter(driveLetter))
            return false;

        string device = @"\\.\" + char.ToUpperInvariant(driveLetter) + ":";
        SafeFileHandle handle = CreateFile(device, 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        try
        {
            var query = new byte[12];
            BitConverter.TryWriteBytes(query.AsSpan(0), PropertySeekPenalty);
            BitConverter.TryWriteBytes(query.AsSpan(4), PropertyQueryDevice);
            var answer = new byte[16];
            if (!DeviceIoControl(handle, StorageQueryProperty, query, query.Length, answer, answer.Length, out int returned, 0)
                || returned < 13)
                return false;

            return answer[12] != 0;
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// How the drive is attached. Unknown when the drive will not say, which is
    /// treated the same as a bus that does not earn overlapped reads.
    /// </summary>
    public StorageBus BusType()
    {
        var query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0), PropertyDevice);
        BitConverter.TryWriteBytes(query.AsSpan(4), PropertyQueryDevice);

        // STORAGE_DEVICE_DESCRIPTOR: the bus type is the single byte at offset 28.
        var answer = new byte[512];
        if (!DeviceIoControl(_handle, StorageQueryProperty, query, query.Length, answer, answer.Length, out int returned, 0)
            || returned < 29)
            return StorageBus.Unknown;

        int bus = answer[28];
        return Enum.IsDefined(typeof(StorageBus), bus) ? (StorageBus)bus : StorageBus.Unknown;
    }

    /// <summary>
    /// Where a file's data lives, in clusters, as the file system reports it.
    /// Used for the MFT itself: asking is cheaper than decoding record 0, and
    /// it still works when that record cannot be decoded. Empty when the file
    /// cannot be asked, so the caller can fall back.
    /// </summary>
    public static List<DataRun> RetrievalPointers(string path, int clusterSize)
    {
        var runs = new List<DataRun>();
        if (clusterSize <= 0)
            return runs;

        const uint readAttributes = 0x0080;
        const uint shareAll = 0x0001 | 0x0002 | 0x0004;
        const uint backup = 0x0200_0000;
        SafeFileHandle handle = CreateFile(path, readAttributes, shareAll, 0, OpenExisting, backup, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return runs;
        }

        try
        {
            long start = 0;
            var input = new byte[8];
            var output = new byte[1024 * 64];
            while (true)
            {
                BitConverter.TryWriteBytes(input, start);
                bool ok = DeviceIoControl(handle, 0x0009_0073, input, input.Length, output, output.Length, out int returned, 0);
                int error = Marshal.GetLastWin32Error();
                if (returned < 16)
                    break;

                int extentCount = BitConverter.ToInt32(output.AsSpan(4, 4));
                if (extentCount < 0)
                    break;

                long cursor = BitConverter.ToInt64(output.AsSpan(8, 8));
                int position = 16;
                for (int i = 0; i < extentCount && position + 16 <= returned; i++)
                {
                    long next = BitConverter.ToInt64(output.AsSpan(position, 8));
                    long lcn = BitConverter.ToInt64(output.AsSpan(position + 8, 8));
                    long clusters = next - cursor;
                    // A negative logical cluster is a hole: the file system reserved nothing there.
                    if (clusters > 0)
                        runs.Add(new DataRun(lcn < 0, clusters, lcn < 0 ? -1 : lcn));
                    cursor = next;
                    position += 16;
                }

                // 234 is ERROR_MORE_DATA: the buffer filled before the file ended.
                if (ok || error != 234)
                    break;

                start = cursor;
            }
        }
        finally
        {
            handle.Dispose();
        }

        return runs;
    }

    /// <summary>Reads <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>.</summary>
    public void Read(long offset, Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return;

        using var done = new ManualResetEvent(false);
        bool held = false;
        done.SafeWaitHandle.DangerousAddRef(ref held);
        try
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
                SetOffset(ref overlapped, offset + filled);
                done.Reset();

                fixed (byte* pointer = &MemoryMarshal.GetReference(buffer))
                {
                    bool ok = ReadFile(_handle, pointer + filled, buffer.Length - filled, out int read, &overlapped);
                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorIoPending)
                            throw new IOException($"Short read at offset {offset + filled} (Win32 error {error}).", error);

                        if (WaitForSingleObject(done.SafeWaitHandle, Infinite) != WaitObject
                            || !GetOverlappedResult(_handle, &overlapped, out read, false))
                        {
                            int failed = Marshal.GetLastWin32Error();
                            throw new IOException($"Short read at offset {offset + filled} (Win32 error {failed}).", failed);
                        }
                    }

                    if (read <= 0)
                        throw new IOException($"Short read at offset {offset + filled}.", Marshal.GetLastWin32Error());

                    filled += read;
                }
            }
        }
        finally
        {
            if (held)
                done.SafeWaitHandle.DangerousRelease();
        }
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes at <paramref name="offset"/>, with
    /// several reads outstanding. <paramref name="consume"/> is called in file
    /// order and must finish with the bytes before it returns, because the
    /// buffer is then reused for the next read.
    ///
    /// The offset, the length and <paramref name="chunkBytes"/> all have to be
    /// multiples of the sector size. The handle is unbuffered, and a misaligned
    /// read is rejected by the system.
    /// </summary>
    public void ReadPipelined(
        long offset, long length, int chunkBytes, Action<ReadOnlySpan<byte>> consume, CancellationToken cancellationToken = default)
    {
        if (length <= 0)
            return;
        if (chunkBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkBytes), "A read must cover at least one byte.");
        ArgumentNullException.ThrowIfNull(consume);

        var slots = new ReadSlot[PipelineDepth];
        try
        {
            for (int i = 0; i < slots.Length; i++)
                slots[i] = ReadSlot.Create(chunkBytes);

            long cursor = offset;
            long end = offset + length;
            int nextIssue = 0;
            int nextComplete = 0;
            int inFlight = 0;

            void Issue()
            {
                if (cursor >= end)
                    return;

                int ask = (int)Math.Min(chunkBytes, end - cursor);
                slots[nextIssue].Begin(_handle, cursor, ask);
                cursor += ask;
                nextIssue = (nextIssue + 1) % slots.Length;
                inFlight++;
            }

            for (int i = 0; i < slots.Length && cursor < end; i++)
                Issue();

            while (inFlight > 0)
            {
                ReadSlot slot = slots[nextComplete];
                slot.Finish(_handle, cancellationToken);
                inFlight--;
                consume(slot.Written);
                nextComplete = (nextComplete + 1) % slots.Length;
                Issue();
            }
        }
        finally
        {
            foreach (ReadSlot? slot in slots)
                slot?.Release(_handle);
        }
    }

    public void Dispose() => _handle.Dispose();

    private static void SetOffset(ref NativeOverlapped overlapped, long offset)
    {
        overlapped.Offset = unchecked((int)offset);
        overlapped.OffsetHigh = unchecked((int)(offset >> 32));
    }

    /// <summary>One outstanding read. The buffer and the overlapped block stay put until the read finishes.</summary>
    private sealed class ReadSlot : IDisposable
    {
        private readonly int _capacity;
        private byte* _buffer;
        private NativeOverlapped* _overlapped;
        private nint _event;
        private bool _pending;
        private int _requested;

        private ReadSlot(int capacity) => _capacity = capacity;

        public static ReadSlot Create(int capacity)
        {
            var slot = new ReadSlot(capacity);
            slot._buffer = (byte*)NativeMemory.AlignedAlloc((nuint)capacity, 4096);
            slot._overlapped = (NativeOverlapped*)NativeMemory.Alloc((nuint)sizeof(NativeOverlapped));
            slot._event = CreateEventW(0, true, false, 0);
            if (slot._event == 0)
            {
                int error = Marshal.GetLastWin32Error();
                slot.Dispose();
                throw new IOException($"Cannot create a read event (Win32 error {error}).", error);
            }

            return slot;
        }

        public ReadOnlySpan<byte> Written => new(_buffer, _requested);

        public void Begin(SafeFileHandle handle, long offset, int length)
        {
            NativeMemory.Clear(_overlapped, (nuint)sizeof(NativeOverlapped));
            _overlapped->Offset = unchecked((int)offset);
            _overlapped->OffsetHigh = unchecked((int)(offset >> 32));
            _overlapped->EventHandle = _event;
            _requested = length;
            ResetEvent(_event);

            // A read that finishes before ReadFile returns does not signal the
            // event. Signal it here so waiting is the same either way.
            bool ready = ReadFile(handle, _buffer, length, out _, _overlapped);
            int error = Marshal.GetLastWin32Error();
            if (ready)
            {
                SetEvent(_event);
                _pending = false;
                return;
            }

            if (error != ErrorIoPending)
                throw new IOException($"Cannot read at offset {offset} (Win32 error {error}).", error);

            _pending = true;
        }

        public void Finish(SafeFileHandle handle, CancellationToken cancellationToken)
        {
            var quiet = System.Diagnostics.Stopwatch.StartNew();
            while (WaitForSingleObject(_event, 50) == WaitTimeout)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    CancelIoEx(handle, _overlapped);
                    WaitForSingleObject(_event, Infinite);
                    _pending = false;
                    throw new OperationCanceledException(cancellationToken);
                }

                // A drive that goes silent is not going to answer. Drop the
                // overlapped reads and let the caller continue one chunk at a time.
                if (quiet.ElapsedMilliseconds >= ReadStallMilliseconds)
                {
                    CancelIoEx(handle, _overlapped);
                    if (WaitForSingleObject(_event, 1000) == WaitTimeout)
                    {
                        // The cancel itself was ignored. The buffer has to stay
                        // allocated for as long as the read might still complete.
                        _pending = false;
                        _buffer = null;
                        _overlapped = null;
                        throw new IOException($"The drive stopped responding while reading (nothing received for {ReadStallMilliseconds / 1000} seconds).");
                    }

                    _pending = false;
                    throw new IOException($"The drive stopped responding while reading (nothing received for {ReadStallMilliseconds / 1000} seconds).");
                }
            }

            _pending = false;
            if (!GetOverlappedResult(handle, _overlapped, out uint read, false) || read < (uint)_requested)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorOperationAborted)
                    throw new OperationCanceledException(cancellationToken);

                throw new IOException($"Short read (Win32 error {error}).", error);
            }
        }

        /// <summary>Cancels a read that is still outstanding, waits for it, then frees the buffers.</summary>
        public void Release(SafeFileHandle handle)
        {
            if (_pending && _overlapped is not null)
            {
                CancelIoEx(handle, _overlapped);
                if (_event != 0 && WaitForSingleObject(_event, 1000) == WaitTimeout)
                {
                    // Same rule as a stalled read: a cancel the drive never
                    // answers leaves the buffer allocated rather than freed
                    // underneath a read that might still land.
                    _pending = false;
                    _buffer = null;
                    _overlapped = null;
                    if (_event != 0)
                    {
                        CloseHandle(_event);
                        _event = 0;
                    }

                    return;
                }

                _pending = false;
            }

            Dispose();
        }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                NativeMemory.AlignedFree(_buffer);
                _buffer = null;
            }

            if (_overlapped is not null)
            {
                NativeMemory.Free(_overlapped);
                _overlapped = null;
            }

            if (_event != 0)
            {
                CloseHandle(_event);
                _event = 0;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        public nint Internal;
        public nint InternalHigh;
        public int Offset;
        public int OffsetHigh;
        public nint EventHandle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle handle, uint controlCode, byte[] input, int inputSize,
        byte[] output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(
        SafeFileHandle handle, byte* buffer, int bytesToRead, out int bytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetOverlappedResult(
        SafeFileHandle handle, NativeOverlapped* overlapped, out int bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetOverlappedResult(
        SafeFileHandle handle, NativeOverlapped* overlapped, out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CancelIoEx(SafeFileHandle handle, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    private static partial nint CreateEventW(nint securityAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ResetEvent(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
}
