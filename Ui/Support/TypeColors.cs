using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DiskGo;

/// <summary>
/// The color of one file type. The same extension always lands on the same
/// slot, so a type keeps its color between scans. High contrast is not a
/// palette: callers draw a hatch instead of trusting the color.
/// </summary>
public static class TypeColors
{
    private static readonly Color[] Light =
    [
        Color.FromArgb(255, 0, 120, 212),
        Color.FromArgb(255, 16, 124, 16),
        Color.FromArgb(255, 196, 43, 28),
        Color.FromArgb(255, 135, 100, 184),
        Color.FromArgb(255, 227, 123, 0),
        Color.FromArgb(255, 0, 153, 153),
        Color.FromArgb(255, 180, 0, 158),
        Color.FromArgb(255, 92, 45, 145),
        Color.FromArgb(255, 0, 99, 177),
        Color.FromArgb(255, 73, 130, 5),
        Color.FromArgb(255, 142, 86, 46),
        Color.FromArgb(255, 0, 130, 114),
        Color.FromArgb(255, 177, 70, 194),
        Color.FromArgb(255, 190, 120, 0),
        Color.FromArgb(255, 74, 110, 160),
        Color.FromArgb(255, 140, 60, 80),
    ];

    private static readonly Color[] Dark =
    [
        Color.FromArgb(255, 96, 176, 240),
        Color.FromArgb(255, 96, 196, 112),
        Color.FromArgb(255, 240, 128, 112),
        Color.FromArgb(255, 190, 160, 230),
        Color.FromArgb(255, 245, 180, 80),
        Color.FromArgb(255, 80, 210, 200),
        Color.FromArgb(255, 240, 130, 190),
        Color.FromArgb(255, 170, 140, 230),
        Color.FromArgb(255, 130, 180, 240),
        Color.FromArgb(255, 170, 210, 90),
        Color.FromArgb(255, 210, 160, 110),
        Color.FromArgb(255, 90, 200, 180),
        Color.FromArgb(255, 230, 150, 220),
        Color.FromArgb(255, 230, 190, 90),
        Color.FromArgb(255, 150, 180, 220),
        Color.FromArgb(255, 220, 140, 150),
    ];

    private static readonly SolidColorBrush?[] LightBrushes = new SolidColorBrush?[Light.Length];
    private static readonly SolidColorBrush?[] DarkBrushes = new SolidColorBrush?[Dark.Length];

    public static int SlotFor(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension is "(无)" or "(none)")
            return 0;

        int hash = 0;
        foreach (char letter in extension)
            hash = hash * 31 + char.ToLowerInvariant(letter);
        return (hash & int.MaxValue) % Light.Length;
    }

    public static Color For(string extension, bool dark, bool monochrome = false)
    {
        int slot = SlotFor(extension);
        if (monochrome)
        {
            byte shade = dark
                ? (byte)(76 + (slot % 8) * 18)
                : (byte)(110 + (slot % 8) * 14);
            return Color.FromArgb(255, shade, shade, shade);
        }

        Color[] palette = dark ? Dark : Light;
        return palette[slot];
    }

    /// <summary>
    /// Returns a cached <see cref="SolidColorBrush"/> for <paramref name="extension"/>.
    /// Must be called on the UI thread.
    /// </summary>
    public static SolidColorBrush BrushFor(string extension, bool dark)
    {
        int slot = SlotFor(extension);
        SolidColorBrush?[] cache = dark ? DarkBrushes : LightBrushes;
        return cache[slot] ??= new SolidColorBrush((dark ? Dark : Light)[slot]);
    }

    /// <summary>The neutral color of a folder rectangle. It is not a file type.</summary>
    public static Color Folder(bool dark)
        => dark ? Color.FromArgb(255, 48, 54, 62) : Color.FromArgb(255, 208, 218, 228);

    public static Color FolderBorder(bool dark)
        => dark ? Color.FromArgb(255, 84, 94, 108) : Color.FromArgb(255, 150, 164, 180);

    public static Color Free(bool dark)
        => dark ? Color.FromArgb(255, 28, 28, 30) : Color.FromArgb(255, 240, 242, 245);

    public static Color FreeBorder(bool dark)
        => dark ? Color.FromArgb(255, 90, 90, 96) : Color.FromArgb(255, 165, 170, 178);

    public static Color Reserved(bool dark)
        => dark ? Color.FromArgb(255, 96, 96, 104) : Color.FromArgb(255, 154, 160, 168);
}
