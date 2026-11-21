namespace DiskGo.Core;

/// <summary>
/// The places a scan could start: every ready drive, with subst letters folded
/// back onto the folder they stand for, plus every portable device Windows can
/// see. Nothing is scanned here. The list only answers "what is there to scan".
/// </summary>
public static class ScanTargetList
{
    /// <summary>
    /// Drives first, then portable devices. A drive that is not ready is left
    /// out, because there is nothing on it to scan. A subst drive is reported
    /// as the folder it points at, so it is not offered as a second copy of the
    /// volume underneath it.
    /// </summary>
    public static IReadOnlyList<ScanTarget> Enumerate()
    {
        var targets = new List<ScanTarget>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                char letter = drive.Name.Length > 0 ? drive.Name[0] : '\0';
                string? subst = letter == '\0' ? null : SubstDrives.TargetOf(letter);
                if (subst is not null)
                {
                    targets.Add(new ScanTarget(ScanTargetKind.Folder, subst, $"{char.ToUpperInvariant(letter)}: ({subst})"));
                    continue;
                }

                string? mapped = letter == '\0' ? null : NetworkDrives.TargetOf(letter);
                if (mapped is not null)
                {
                    targets.Add(new ScanTarget(ScanTargetKind.Network, mapped, $"{char.ToUpperInvariant(letter)}: ({mapped})"));
                    continue;
                }

                string shown = drive.Name.TrimEnd('\\');
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? DefaultVolumeName(drive.DriveType)
                    : drive.VolumeLabel;
                targets.Add(new ScanTarget(ScanTargetKind.Volume, drive.Name, $"{label} ({shown})"));
            }
            catch (IOException)
            {
                // A drive can disappear between being listed and being asked about.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        targets.AddRange(PortableDevices.List());
        targets.AddRange(Unlettered());
        return targets;
    }

    /// <summary>Volumes that are mounted on a folder and have no drive letter, so a letter scan would never reach them.</summary>
    private static List<ScanTarget> Unlettered()
    {
        var targets = new List<ScanTarget>();
        foreach (string volume in VolumeMounts.Volumes())
        {
            try
            {
                List<string> paths = VolumeMounts.PathsOf(volume);
                if (paths.Count == 0 || paths.Exists(static path => path.Length >= 2 && path[1] == ':'))
                    continue;

                string path = paths[0];
                string shown = path.TrimEnd(Path.DirectorySeparatorChar);
                string label = VolumeMounts.TryGetLabel(volume) ?? "\u6302\u8f7d\u5377";
                targets.Add(new ScanTarget(ScanTargetKind.Volume, path, $"{label} ({shown})"));
            }
            catch (IOException)
            {
            }
        }

        return targets;
    }

    private static string DefaultVolumeName(DriveType type)
        => type switch
        {
            DriveType.Fixed => "\u672c\u5730\u78c1\u76d8",
            DriveType.Removable => "\u53ef\u79fb\u52a8\u78c1\u76d8",
            DriveType.Network => "\u7f51\u7edc\u4f4d\u7f6e",
            DriveType.CDRom => "CD \u9a71\u52a8\u5668",
            DriveType.Ram => "RAM \u78c1\u76d8",
            _ => "\u78c1\u76d8",
        };
}
