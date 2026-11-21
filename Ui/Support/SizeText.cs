using System.Globalization;

namespace DiskGo;

/// <summary>
/// How a byte count is written. Automatic picks the largest unit that keeps
/// the number under 1000. The fixed units never promote, so a column of sizes
/// stays comparable.
/// </summary>
public enum SizeUnit
{
    Automatic,
    Bytes,
    Kilobytes,
    Megabytes,
    Gigabytes,
    Terabytes,
}

/// <summary>Turns a byte count into the text a column shows.</summary>
public static class SizeText
{
    public static string Format(long bytes, SizeUnit unit, bool binary, int decimals)
    {
        decimals = Math.Clamp(decimals, 0, 2);
        double step = binary ? 1024d : 1000d;
        string[] names = binary ? ["B", "KiB", "MiB", "GiB", "TiB"] : ["B", "KB", "MB", "GB", "TB"];

        int index = unit switch
        {
            SizeUnit.Bytes => 0,
            SizeUnit.Kilobytes => 1,
            SizeUnit.Megabytes => 2,
            SizeUnit.Gigabytes => 3,
            SizeUnit.Terabytes => 4,
            _ => AutomaticIndex(bytes, step),
        };

        double value = bytes;
        for (int i = 0; i < index; i++)
            value /= step;

        if (index == 0)
            return bytes.ToString("N0", CultureInfo.CurrentCulture) + " " + names[0];

        string pattern = decimals switch
        {
            0 => "N0",
            1 => "N1",
            _ => "N2",
        };
        return value.ToString(pattern, CultureInfo.CurrentCulture) + " " + names[index];
    }

    public static string Percent(double value)
        => value.ToString("0.0", CultureInfo.CurrentCulture) + "%";

    public static string When(DateTime? time)
        => time is null || time.Value == default ? "" : time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string Attributes(FileAttributes attributes)
    {
        Span<char> letters = stackalloc char[6];
        int used = 0;
        if (attributes.HasFlag(FileAttributes.ReadOnly)) letters[used++] = 'R';
        if (attributes.HasFlag(FileAttributes.Hidden)) letters[used++] = 'H';
        if (attributes.HasFlag(FileAttributes.System)) letters[used++] = 'S';
        if (attributes.HasFlag(FileAttributes.Archive)) letters[used++] = 'A';
        if (attributes.HasFlag(FileAttributes.Compressed)) letters[used++] = 'C';
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) letters[used++] = 'L';
        return new string(letters[..used]);
    }

    public static string Serial(uint serial)
        => $"{serial >> 16:X4}-{serial & 0xFFFF:X4}";

    private static int AutomaticIndex(long bytes, double step)
    {
        double value = Math.Abs(bytes);
        int index = 0;
        while (value >= step && index < 4)
        {
            value /= step;
            index++;
        }

        return index;
    }
}
