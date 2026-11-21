using System.Runtime.InteropServices;
using DiskGo.Core.Portable;

namespace DiskGo.Core;

/// <summary>
/// Phones, cameras and the other devices Windows reaches through MTP. They have
/// no drive letter, so a scan addresses one as <c>mtp:</c> followed by the
/// device's Plug and Play id, which is what <see cref="ScanTarget.Path"/> carries.
/// </summary>
public static class PortableDevices
{
    /// <summary>The prefix that marks a path as a portable device rather than a place in the file system.</summary>
    public const string Scheme = "mtp:";

    /// <summary>True when <paramref name="path"/> names a portable device. The comparison ignores case and a leading space.</summary>
    public static bool IsDevicePath(string path)
        => path.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Plug and Play id inside a device path.</summary>
    public static string DeviceId(string path)
    {
        string trimmed = path.Trim();
        if (!trimmed.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) || trimmed.Length == Scheme.Length)
            throw new ArgumentException($"'{path}' is not a portable device path. It should look like mtp:<id>.", nameof(path));

        return trimmed[Scheme.Length..];
    }

    /// <summary>
    /// The connected devices, or an empty list when Windows has no portable
    /// device support or none are plugged in. Failing to list them is not an
    /// error: the file system scans still work.
    /// </summary>
    public static unsafe IReadOnlyList<ScanTarget> List()
    {
        var targets = new List<ScanTarget>();

        try
        {
            EnsureCom();
            IPortableDeviceManager? manager = PortableDeviceApi.Create<IPortableDeviceManager>(
                PortableDeviceApi.PortableDeviceManagerClass, PortableDeviceApi.ManagerInterface);
            if (manager is null)
                return targets;

            uint count = 0;
            manager.GetDevices(null, ref count);
            if (count == 0)
                return targets;

            var ptrs = new nint[count];
            fixed (nint* ptr = ptrs)
                manager.GetDevices(ptr, ref count);

            for (int i = 0; i < count; i++)
            {
                if (ptrs[i] == 0)
                    continue;

                string? id = Marshal.PtrToStringUni(ptrs[i]);
                Marshal.FreeCoTaskMem(ptrs[i]);
                if (string.IsNullOrEmpty(id))
                    continue;

                targets.Add(new ScanTarget(ScanTargetKind.PortableDevice, Scheme + id, FriendlyName(manager, id)));
            }
        }
        catch (COMException)
        {
            // The device layer is optional. A machine without it simply has no such targets.
        }
        catch (InvalidCastException)
        {
        }

        return targets;
    }

    private static unsafe string FriendlyName(IPortableDeviceManager manager, string id)
    {
        try
        {
            uint chars = 0;
            manager.GetDeviceFriendlyName(id, null, ref chars);
            if (chars == 0)
                return id;

            var buffer = new char[chars];
            fixed (char* ptr = buffer)
                manager.GetDeviceFriendlyName(id, ptr, ref chars);
            int len = Array.IndexOf(buffer, '\0');
            string name = len >= 0 ? new string(buffer, 0, len) : new string(buffer);
            return name.Length == 0 ? id : name;
        }
        catch (COMException)
        {
            return id;
        }
    }

    /// <summary>
    /// Makes sure COM is initialised on this thread. Scanning runs on whatever
    /// thread the caller used, which has usually never called <c>CoInitialize</c>.
    /// </summary>
    internal static void EnsureCom()
    {
        int result = PortableDeviceApi.CoInitializeEx(0, PortableDeviceApi.ApartmentThreaded);
        if (result < 0 && result != -2147417850) // RPC_E_CHANGED_MODE: already initialised on another model, which is fine
            Marshal.ThrowExceptionForHR(result);
    }
}
