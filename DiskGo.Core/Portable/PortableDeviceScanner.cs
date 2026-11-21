using System.Runtime.InteropServices;

namespace DiskGo.Core.Portable;

/// <summary>
/// Walks the storage of one MTP device and produces the same tree a directory
/// walk produces. The device has no paths of its own, so each object is given
/// one by joining its name onto its parent's, under the <c>mtp:</c> path the
/// scan was asked for.
/// </summary>
internal static class PortableDeviceScanner
{
    public static (FolderNode Root, List<FileEntry> Files, List<string> Warnings) Scan(
        string devicePath,
        PathFilter filter,
        CancellationToken cancellationToken,
        Action<long, long>? report = null)
    {
        PortableDevices.EnsureCom();
        string deviceId = PortableDevices.DeviceId(devicePath);

        IPortableDevice device = Open(deviceId);
        try
        {
            device.Content(out IPortableDeviceContent content);
            content.Properties(out IPortableDeviceProperties properties);
            IPortableDeviceKeyCollection keys = WantedKeys();

            string rootPath = PortableDevices.Scheme + deviceId;
            var root = new FolderNode(Label(properties, deviceId), rootPath);
            var files = new List<FileEntry>();
            var warnings = new List<string>();
            long folders = 1;

            var pending = new Stack<(string Id, FolderNode Node)>();
            pending.Push((PortableDeviceApi.DeviceObject, root));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string parentId, FolderNode node) = pending.Pop();

                foreach (string childId in Children(content, parentId, warnings, node.FullPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryRead(properties, keys, childId, out DeviceObject child))
                    {
                        warnings.Add($"{node.FullPath}: an object could not be read.");
                        continue;
                    }

                    if (child.IsFolder)
                    {
                        pending.Push((childId, node.GetOrAddChild(child.Name)));
                        folders++;
                    }
                    else if (filter.Accepts(child.ToFile(node.FullPath)))
                    {
                        FileEntry file = child.ToFile(node.FullPath);
                        files.Add(file);
                        node.AddFile(file);
                    }

                    if (((files.Count + folders) & 0x0FFF) == 0)
                        report?.Invoke(files.Count, folders);
                }
            }

            root.Seal();
            report?.Invoke(files.Count, folders);
            return (root, files, warnings);
        }
        finally
        {
            TryClose(device);
        }
    }

    private static IPortableDevice Open(string deviceId)
    {
        IPortableDevice? device = PortableDeviceApi.Create<IPortableDevice>(
            PortableDeviceApi.PortableDeviceClass, PortableDeviceApi.DeviceInterface)
            ?? throw new IOException("Windows Portable Devices is not available on this machine.");

        IPortableDeviceValues client = ClientInfo();
        try
        {
            device.Open(deviceId, client);
            return device;
        }
        catch (COMException ex)
        {
            throw new IOException($"Cannot open the portable device ({ex.ErrorCode:X8}). It may be locked or in use by another program.", ex);
        }
    }

    /// <summary>
    /// Tells the driver who is connecting. Some devices refuse a connection that
    /// arrives with no client name, and some need to impersonate the caller.
    /// </summary>
    private static IPortableDeviceValues ClientInfo()
    {
        IPortableDeviceValues values = PortableDeviceApi.Create<IPortableDeviceValues>(
            PortableDeviceApi.PortableDeviceValuesClass, PortableDeviceApi.ValuesInterface)
            ?? throw new IOException("Windows Portable Devices is not available on this machine.");

        SetString(values, PortableDeviceApi.ClientName, "DiskGo");
        SetUnsigned(values, PortableDeviceApi.ClientMajorVersion, 1);
        SetUnsigned(values, PortableDeviceApi.ClientMinorVersion, 0);
        SetUnsigned(values, PortableDeviceApi.ClientRevision, 0);
        SetUnsigned(values, PortableDeviceApi.ClientSecurityQuality, PortableDeviceApi.SecurityImpersonation);
        SetUnsigned(values, PortableDeviceApi.ClientDesiredAccess, PortableDeviceApi.GenericRead);
        return values;
    }

    private static void SetString(IPortableDeviceValues values, PortableDeviceApi.PropertyKey key, string value)
    {
        try
        {
            values.SetStringValue(ref key, value);
        }
        catch (COMException)
        {
            // A missing client property only costs the driver its chance to optimise.
        }
    }

    private static void SetUnsigned(IPortableDeviceValues values, PortableDeviceApi.PropertyKey key, uint value)
    {
        try
        {
            values.SetUnsignedIntegerValue(ref key, value);
        }
        catch (COMException)
        {
        }
    }

    private static IPortableDeviceKeyCollection WantedKeys()
    {
        IPortableDeviceKeyCollection keys = PortableDeviceApi.Create<IPortableDeviceKeyCollection>(
            PortableDeviceApi.PortableDeviceKeyCollectionClass, PortableDeviceApi.KeyCollectionInterface)
            ?? throw new IOException("Windows Portable Devices is not available on this machine.");

        Add(keys, PortableDeviceApi.ObjectName);
        Add(keys, PortableDeviceApi.ObjectOriginalName);
        Add(keys, PortableDeviceApi.ObjectContentType);
        Add(keys, PortableDeviceApi.ObjectSize);
        return keys;
    }

    private static void Add(IPortableDeviceKeyCollection keys, PortableDeviceApi.PropertyKey key)
    {
        PortableDeviceApi.PropertyKey copy = key;
        keys.Add(ref copy);
    }

    private static unsafe List<string> Children(IPortableDeviceContent content, string parentId, List<string> warnings, string parentPath)
    {
        var children = new List<string>();
        IEnumPortableDeviceObjectIDs enumerator;
        try
        {
            content.EnumObjects(0, parentId, null, out enumerator);
        }
        catch (COMException ex)
        {
            warnings.Add($"{parentPath}: {ex.Message}");
            return children;
        }

        const uint batchSize = 64;
        nint* batch = stackalloc nint[(int)batchSize];
        while (true)
        {
            uint fetched = 0;
            int result = enumerator.Next(batchSize, batch, ref fetched);
            for (int i = 0; i < fetched; i++)
            {
                if (batch[i] == 0)
                    continue;

                string? id = Marshal.PtrToStringUni(batch[i]);
                Marshal.FreeCoTaskMem(batch[i]);
                if (!string.IsNullOrEmpty(id))
                    children.Add(id);
            }

            if (result != PortableDeviceApi.Ok || fetched == 0)
                break;
        }

        return children;
    }

    private static bool TryRead(IPortableDeviceProperties properties, IPortableDeviceKeyCollection keys, string objectId, out DeviceObject value)
    {
        value = default;
        try
        {
            properties.GetValues(objectId, keys, out IPortableDeviceValues values);
            string name = Text(values, PortableDeviceApi.ObjectOriginalName);
            if (name.Length == 0)
                name = Text(values, PortableDeviceApi.ObjectName);
            if (!Usable(name))
                return false;

            value = new DeviceObject(
                name,
                GuidOf(values, PortableDeviceApi.ObjectContentType) == FolderContentType,
                Integer(values, PortableDeviceApi.ObjectSize));
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    private static readonly Guid FolderContentType = new("27E2E392-A111-48E0-AB0C-E17705A05F85");

    private static string Label(IPortableDeviceProperties properties, string deviceId)
    {
        try
        {
            properties.GetValues(PortableDeviceApi.DeviceObject, null, out IPortableDeviceValues values);
            string name = Text(values, PortableDeviceApi.ObjectName);
            return name.Length == 0 ? deviceId : name;
        }
        catch (COMException)
        {
            return deviceId;
        }
    }

    private static string Text(IPortableDeviceValues values, PortableDeviceApi.PropertyKey key)
    {
        try
        {
            values.GetStringValue(ref key, out string value);
            return value ?? "";
        }
        catch (COMException)
        {
            return "";
        }
    }

    private static long Integer(IPortableDeviceValues values, PortableDeviceApi.PropertyKey key)
    {
        try
        {
            values.GetUnsignedLargeIntegerValue(ref key, out ulong value);
            return value > long.MaxValue ? long.MaxValue : (long)value;
        }
        catch (COMException)
        {
            return 0;
        }
    }

    private static Guid GuidOf(IPortableDeviceValues values, PortableDeviceApi.PropertyKey key)
    {
        try
        {
            values.GetGuidValue(ref key, out Guid value);
            return value;
        }
        catch (COMException)
        {
            return Guid.Empty;
        }
    }

    private static PortableDeviceApi.PropertyKey Key(PortableDeviceApi.PropertyKey key) => key;

    private static bool Usable(string name)
    {
        if (name.Length is 0 or > 255)
            return false;

        foreach (char character in name)
        {
            if (character < ' ' || "<>:\"/\\|?*".Contains(character))
                return false;
        }

        return true;
    }

    private static void TryClose(IPortableDevice device)
    {
        try
        {
            device.Close();
        }
        catch (COMException)
        {
        }
    }

    private readonly record struct DeviceObject(string Name, bool IsFolder, long Size)
    {
        public FileEntry ToFile(string directory)
        {
            string fullPath = directory + "/" + Name;
            int dot = Name.LastIndexOf('.');
            return new FileEntry
            {
                FullPath = fullPath,
                Name = Name,
                Extension = dot <= 0 ? "" : Name[dot..],
                Size = Size,
                // MTP reports one size. There is no cluster rounding to recover,
                // so the allocated size is the same number.
                AllocatedSize = Size,
                Created = DateTime.MinValue,
                Modified = DateTime.MinValue,
                Accessed = DateTime.MinValue,
                Attributes = 0,
            };
        }
    }
}
