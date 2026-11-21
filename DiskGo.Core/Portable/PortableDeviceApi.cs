using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DiskGo.Core.Portable;

/// <summary>
/// The slice of the Windows Portable Devices API a scan needs: list the
/// devices, open one, and read each object's name, size and timestamps.
///
/// Phones and cameras expose their storage through MTP, which has no drive
/// letter and no directory Windows can enumerate, so the ordinary file APIs
/// cannot see it. The interfaces here are the COM contracts in
/// <c>PortableDeviceApi.dll</c>; only the methods a scan calls are declared.
/// </summary>
internal static partial class PortableDeviceApi
{
    public static readonly Guid PortableDeviceManagerClass = new("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");
    public static readonly Guid PortableDeviceClass = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");
    public static readonly Guid PortableDeviceValuesClass = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    public static readonly Guid PortableDeviceKeyCollectionClass = new("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F");

    public static readonly Guid ManagerInterface = new("A1567595-4C2F-4574-A6FA-ECEF917B9A40");
    public static readonly Guid DeviceInterface = new("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C");
    public static readonly Guid ValuesInterface = new("6848F6F2-3155-4F86-B6F5-263EEEAB3143");
    public static readonly Guid KeyCollectionInterface = new("DADA2357-E0AD-492E-98DB-DD61C53BA353");

    /// <summary>The object id of a device's own root. Everything else hangs under it.</summary>
    public const string DeviceObject = "DEVICE";

    /// <summary>A folder. Anything else that has a name is treated as a file.</summary>
    public static readonly PropertyKey ClientName = new(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 2);
    public static readonly PropertyKey ClientMajorVersion = new(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 3);
    public static readonly PropertyKey ClientMinorVersion = new(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 4);
    public static readonly PropertyKey ClientRevision = new(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 5);
    public static readonly PropertyKey ClientSecurityQuality = new(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 8);
    public static readonly PropertyKey ClientDesiredAccess = new(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 9);

    public static readonly PropertyKey ObjectName = new(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 4);
    public static readonly PropertyKey ObjectContentType = new(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 7);
    public static readonly PropertyKey ObjectSize = new(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 11);
    public static readonly PropertyKey ObjectOriginalName = new(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 24);

    public const uint SecurityImpersonation = 2;
    public const uint GenericRead = 0x8000_0000;

    public const int Ok = 0;
    public const int False = 1;

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    public const uint InprocServer = 1;
    public const uint ApartmentThreaded = 2;

    /// <summary>Creates a COM object and returns it as the interface asked for, or null when the class is not registered.</summary>
    public static T? Create<T>(Guid classId, Guid interfaceId) where T : class
    {
        int result = CoCreateInstance(classId, 0, InprocServer, interfaceId, out nint instance);
        if (result < 0 || instance == 0)
            return null;

        try
        {
            return (T)ComWrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    public readonly record struct PropertyKey(Guid FormatId, uint PropertyId);
}

/// <summary>Lists the portable devices currently connected. Defined in <c>PortableDeviceApi.dll</c>.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe partial interface IPortableDeviceManager
{
    /// <summary>
    /// Pass a null buffer to learn how many devices there are, then a buffer of
    /// that size to receive their Plug and Play ids. The caller frees each string.
    /// </summary>
    void GetDevices(nint* deviceIds, ref uint count);

    void RefreshDeviceList();

    void GetDeviceFriendlyName(string deviceId, char* name, ref uint charCount);
}

/// <summary>An open connection to one portable device.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IPortableDevice
{
    void Open(string deviceId, IPortableDeviceValues clientInfo);

    void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);

    void Content(out IPortableDeviceContent content);

    void Capabilities(out nint capabilities);

    void Cancel();

    void Close();
}

/// <summary>The objects stored on a device, addressed by the device's own ids rather than by paths.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6A96ED84-7C73-4480-9938-BF5AF477D426")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IPortableDeviceContent
{
    void EnumObjects(uint flags, string parentId, IPortableDeviceValues? filter, out IEnumPortableDeviceObjectIDs enumerator);

    void Properties(out IPortableDeviceProperties properties);
}

/// <summary>Walks the children of one object. <see cref="Next"/> returns 1 when the collection is exhausted.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("10ECE955-CF41-4728-BFA0-41EEDF1BBF19")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe partial interface IEnumPortableDeviceObjectIDs
{
    [PreserveSig]
    int Next(uint count, nint* ids, ref uint fetched);
}

/// <summary>Reads the properties of one object.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("7F6D695C-03DF-4439-A809-59266BEEE3A6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IPortableDeviceProperties
{
    void GetSupportedProperties(string objectId, out IPortableDeviceKeyCollection keys);

    void GetPropertyAttributes(string objectId, ref PortableDeviceApi.PropertyKey key, out IPortableDeviceValues attributes);

    void GetValues(string objectId, IPortableDeviceKeyCollection? keys, out IPortableDeviceValues values);
}

/// <summary>The keys to ask <see cref="IPortableDeviceProperties.GetValues"/> for. Null asks for every property, which is far slower.</summary>
[GeneratedComInterface]
[Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IPortableDeviceKeyCollection
{
    void GetCount(ref uint count);

    void GetAt(uint index, out PortableDeviceApi.PropertyKey key);

    void Add(ref PortableDeviceApi.PropertyKey key);

    void Clear();
}

/// <summary>A bag of property values. Each getter fails when the device did not supply that property.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IPortableDeviceValues
{
    void GetCount(ref uint count);

    void GetAt(uint index, out PortableDeviceApi.PropertyKey key, nint value);

    void SetValue(ref PortableDeviceApi.PropertyKey key, nint value);

    void GetValue(ref PortableDeviceApi.PropertyKey key, nint value);

    void SetStringValue(ref PortableDeviceApi.PropertyKey key, string value);

    void GetStringValue(ref PortableDeviceApi.PropertyKey key, out string value);

    void SetUnsignedIntegerValue(ref PortableDeviceApi.PropertyKey key, uint value);

    void GetUnsignedIntegerValue(ref PortableDeviceApi.PropertyKey key, out uint value);

    void SetSignedIntegerValue(ref PortableDeviceApi.PropertyKey key, int value);

    void GetSignedIntegerValue(ref PortableDeviceApi.PropertyKey key, out int value);

    void SetUnsignedLargeIntegerValue(ref PortableDeviceApi.PropertyKey key, ulong value);

    void GetUnsignedLargeIntegerValue(ref PortableDeviceApi.PropertyKey key, out ulong value);

    void SetSignedLargeIntegerValue(ref PortableDeviceApi.PropertyKey key, long value);

    void GetSignedLargeIntegerValue(ref PortableDeviceApi.PropertyKey key, out long value);

    void SetFloatValue(ref PortableDeviceApi.PropertyKey key, float value);

    void GetFloatValue(ref PortableDeviceApi.PropertyKey key, out float value);

    void SetErrorValue(ref PortableDeviceApi.PropertyKey key, int value);

    void GetErrorValue(ref PortableDeviceApi.PropertyKey key, out int value);

    void SetKeyValue(ref PortableDeviceApi.PropertyKey key, ref PortableDeviceApi.PropertyKey value);

    void GetKeyValue(ref PortableDeviceApi.PropertyKey key, out PortableDeviceApi.PropertyKey value);

    void SetBoolValue(ref PortableDeviceApi.PropertyKey key, int value);

    void GetBoolValue(ref PortableDeviceApi.PropertyKey key, out int value);

    void SetIUnknownValue(ref PortableDeviceApi.PropertyKey key, nint value);

    void GetIUnknownValue(ref PortableDeviceApi.PropertyKey key, out nint value);

    void SetGuidValue(ref PortableDeviceApi.PropertyKey key, ref Guid value);

    void GetGuidValue(ref PortableDeviceApi.PropertyKey key, out Guid value);
}
