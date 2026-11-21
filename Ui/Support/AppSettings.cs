using System.Text.Json;
using System.Text.Json.Serialization;
using DiskGo.Core;

namespace DiskGo;

/// <summary>
/// What the window remembers between runs. Every field is a choice the
/// interface already makes; nothing here changes how a scan reads the disk
/// beyond the options it already accepts.
/// </summary>
public sealed class AppSettings
{
    public bool AlwaysAdministrator { get; set; }
    public bool PreferMftForFolders { get; set; }
    public bool AllowSlowFallback { get; set; } = true;
    public int WalkThreads { get; set; }
    public MftReadMode MftRead { get; set; } = MftReadMode.Auto;
    public bool IncludeNtfsMetadata { get; set; }
    public bool IgnoreHidden { get; set; }
    public FileAttributes ExcludedAttributes { get; set; }
    public bool UseRegexFilter { get; set; }
    public bool MonitorChanges { get; set; }
    public bool ScanOnSelect { get; set; } = true;
    public bool OpenOnDoubleClick { get; set; } = true;
    public bool AllowRename { get; set; } = true;
    public bool ShowNewestModified { get; set; }
    public long FileGroupMinimum { get; set; }

    public SizeUnit SizeUnit { get; set; } = SizeUnit.Automatic;
    public bool BinaryUnits { get; set; }
    public int DecimalPlaces { get; set; } = 1;
    public bool TreemapNames { get; set; } = true;
    public bool TreemapFreeSpace { get; set; } = false;
    public bool TreemapByAllocated { get; set; } = true;

    // Layout & window state
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public double TypePaneWidth { get; set; } = 280;
    public double TreeBandRatio { get; set; } = 0.52;
    public double TreemapHeight { get; set; } = 320;
    public bool ShowTypePane { get; set; } = true;
    public bool ShowFilePane { get; set; }

    // Visible optional columns
    public bool TreeShowAllocated { get; set; }
    public bool TreeShowFiles { get; set; } = true;
    public bool TreeShowFolders { get; set; } = true;
    public bool TreeShowModified { get; set; } = true;

    public bool FileShowAllocated { get; set; } = true;
    public bool FileShowPercent { get; set; } = true;
    public bool FileShowPath { get; set; } = true;
    public bool FileShowModified { get; set; } = true;
    public bool FileShowAttributes { get; set; } = true;

    // Treemap PNG export defaults
    public int PngWidth { get; set; } = 1920;
    public int PngHeight { get; set; } = 1080;

    public static string FilePath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskGo", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file should not stop the window opening. The next save replaces it.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
