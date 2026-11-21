namespace DiskGo.Core;

/// <summary>What kind of place a <see cref="ScanTarget"/> points at. It decides which reader can scan it.</summary>
public enum ScanTargetKind
{
    /// <summary>A fixed, removable or network drive that has its own file system.</summary>
    Volume,

    /// <summary>A folder on a volume, including the folder a subst drive stands for.</summary>
    Folder,

    /// <summary>A UNC path, including the share a mapped drive letter stands for.</summary>
    Network,

    /// <summary>
    /// A phone, camera or other device that speaks MTP. It has no drive letter
    /// and no file system Windows can walk, so it needs its own reader.
    /// </summary>
    PortableDevice,
}

/// <summary>
/// One thing the program can offer to scan. <see cref="Path"/> is what gets
/// handed to <see cref="DiskScanner.Scan"/>: a real path for a volume or folder,
/// and <c>mtp:</c> followed by the device's Plug and Play id for a portable device.
/// </summary>
public sealed record ScanTarget(ScanTargetKind Kind, string Path, string Label)
{
    /// <summary>True when the path names a whole volume rather than a folder inside one.</summary>
    public bool IsVolumeRoot => Kind == ScanTargetKind.Volume && ScanTargets.IsVolumeRoot(Path);
}
