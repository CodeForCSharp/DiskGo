using System.Globalization;
using System.Text;

namespace DiskGo.Core;

/// <summary>
/// Writes the per-extension summary on its own. The full CSV export already
/// carries every file; this is the short table that answers which kinds of
/// file account for the space.
/// </summary>
public static class ExtensionExporter
{
    public static void Export(ScanResult result, string filePath)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("Extension,Files,Size,Allocated,% of Total");

        foreach (ExtensionTotal total in result.Extensions)
        {
            writer.Write(Quote(total.Extension));
            writer.Write(',');
            writer.Write(total.FileCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(total.Size.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(total.AllocatedSize.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(total.PercentOf(result.Root.Size).ToString("0.00", CultureInfo.InvariantCulture));
        }
    }

    private static string Quote(string value)
        => value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
