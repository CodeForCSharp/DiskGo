using DiskGo.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DiskGo;

/// <summary>One rectangle the treemap drew, so a click can be turned back into a folder or a file.</summary>
public readonly record struct TreemapHit(
    Rect Bounds,
    FolderNode? Folder,
    FileEntry? File,
    bool Free,
    long FreeSize = 0,
    int RemainderCount = 0,
    long RemainderBytes = 0)
{
    public string Name => Free
        ? "空闲空间"
        : RemainderCount > 0
            ? $"{Folder?.Name ?? ""} (其余 {RemainderCount:N0} 项)"
            : Folder?.Name ?? File?.Name ?? "";

    public string Path => Folder?.FullPath ?? File?.FullPath ?? "";

    public long Size => Free
        ? FreeSize
        : RemainderCount > 0
            ? RemainderBytes
            : Folder?.Size ?? File?.Size ?? 0;

    public long AllocatedSize => Free
        ? FreeSize
        : RemainderCount > 0
            ? RemainderBytes
            : Folder?.AllocatedSize ?? File?.AllocatedSize ?? 0;
}

/// <summary>
/// Renders a <see cref="FolderNode"/> into a BGRA pixel buffer using <see cref="TreemapLayout"/>
/// for rectangle calculation and Win2D for crisp DPI-aware text labels.
/// </summary>
public sealed class Treemap
{
    private readonly List<TreemapHit> _hits = [];

    public IReadOnlyList<TreemapHit> Hits => _hits;

    public bool ShowNames { get; set; } = true;

    public bool ShowFreeSpace { get; set; }

    public bool ByAllocated { get; set; } = true;

    public bool Monochrome { get; set; }

    public bool Dark { get; set; }

    public bool HighContrast { get; set; }

    public long FreeBytes { get; set; }

    /// <summary>How many physical pixels one device-independent pixel covers. Labels are sized from this.</summary>
    public float PixelsPerDip { get; set; } = 1;

    /// <summary>
    /// Paints <paramref name="folder"/> into <paramref name="pixels"/>, which is
    /// <paramref name="width"/> by <paramref name="height"/> in BGRA order.
    /// </summary>
    public void Draw(byte[] pixels, int width, int height, FolderNode? folder)
    {
        _hits.Clear();
        if (pixels.Length < width * height * 4)
            return;

        Color background = Dark ? Color.FromArgb(255, 28, 28, 30) : Color.FromArgb(255, 246, 247, 249);
        Fill(pixels, width, height, 0, 0, width, height, background);
        if (folder is null || width < 8 || height < 8)
            return;

        var labels = new List<Label>();
        float contentWidth = width;
        long folderSize = ByAllocated ? folder.AllocatedSize : folder.Size;

        if (ShowFreeSpace && FreeBytes > 0 && folderSize >= 0)
        {
            long combined = folderSize + FreeBytes;
            if (combined > 0)
            {
                float freeWidth = (float)(width * ((double)FreeBytes / combined));
                int freeW = (int)Math.Round(freeWidth);
                if (freeW >= 10 && width - freeW >= 16)
                {
                    int freeX = width - freeW;
                    contentWidth = freeX;
                    int gap = Math.Max(1, (int)Math.Round(PixelsPerDip));
                    int fx = freeX + gap;
                    int fy = gap;
                    int fw = Math.Max(1, freeW - gap * 2);
                    int fh = Math.Max(1, height - gap * 2);

                    Fill(pixels, width, height, fx, fy, fw, fh, TypeColors.Free(Dark));
                    StrokeDashed(pixels, width, height, fx, fy, fw, fh, TypeColors.FreeBorder(Dark), Math.Max(4, (int)(4 * PixelsPerDip)));
                    _hits.Add(new TreemapHit(new Rect(freeX, 0, freeW, height), null, null, Free: true, FreeSize: FreeBytes));

                    int minW = (int)Math.Round(40 * PixelsPerDip);
                    int minH = (int)Math.Round(18 * PixelsPerDip);
                    if (ShowNames && fw >= minW && fh >= minH)
                    {
                        int pad = Math.Max(4, (int)Math.Round(5 * PixelsPerDip));
                        int lblH = Math.Min(fh - 2, Math.Max(15, (int)Math.Round(18 * PixelsPerDip)));
                        labels.Add(new Label(fx + pad, fy + 1, Math.Max(1, fw - pad * 2), lblH,
                            "空闲空间", Dark ? Color.FromArgb(255, 190, 190, 195) : Color.FromArgb(255, 90, 96, 104)));
                    }
                }
            }
        }

        Paint(pixels, width, height, folder, 0, 0, contentWidth, height, depth: 0, labels);
        DrawLabels(pixels, width, height, labels);
    }

    public static async Task ExportPngAsync(
        string filePath,
        int width,
        int height,
        FolderNode folder,
        long freeBytes,
        bool showNames,
        bool showFreeSpace,
        bool byAllocated,
        bool monochrome,
        bool dark)
    {
        var map = new Treemap
        {
            ShowNames = showNames,
            ShowFreeSpace = showFreeSpace,
            ByAllocated = byAllocated,
            Monochrome = monochrome,
            Dark = dark,
            FreeBytes = freeBytes,
            PixelsPerDip = Math.Max(1f, width / 1280f),
        };

        byte[] bgra = new byte[width * height * 4];
        map.Draw(bgra, width, height, folder);

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using IRandomAccessStream randomAccess = stream.AsRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomAccess);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)width,
            (uint)height,
            96,
            96,
            bgra);
        await encoder.FlushAsync();
    }

    private void Paint(byte[] pixels, int stride, int canvasHeight, FolderNode folder, float x, float y, float width, float height, int depth, List<Label> labels)
    {
        var items = new List<TreemapLayoutItem>();
        var ranked = new List<TreemapLayoutItem>();
        long total = TreemapLayout.Collect(folder, width * height, ByAllocated, items, ranked);
        if (total <= 0 || items.Count == 0)
            return;

        TreemapLayout.Squarify(items, total, x, y, width, height);

        int pad = Math.Max(3, (int)Math.Round(4 * PixelsPerDip));
        int minLabelW = Math.Max(26, (int)Math.Round(28 * PixelsPerDip));
        int minLabelH = Math.Max(14, (int)Math.Round(15 * PixelsPerDip));
        int minHeaderW = (int)Math.Round(52 * PixelsPerDip);
        int minHeaderH = (int)Math.Round(32 * PixelsPerDip);
        int minRecurseSize = Math.Max(5, (int)Math.Round(5 * PixelsPerDip));

        int boundLeft = (int)Math.Round(x);
        int boundTop = (int)Math.Round(y);
        int boundRight = (int)Math.Round(x + width);
        int boundBottom = (int)Math.Round(y + height);

        foreach (TreemapLayoutItem item in items)
        {
            int left = Math.Clamp((int)Math.Round(item.X), boundLeft, boundRight);
            int top = Math.Clamp((int)Math.Round(item.Y), boundTop, boundBottom);
            int right = Math.Clamp((int)Math.Round(item.X + item.Width), left, boundRight);
            int bottom = Math.Clamp((int)Math.Round(item.Y + item.Height), top, boundBottom);
            int cellWidth = right - left;
            int cellHeight = bottom - top;
            if (cellWidth < 1 || cellHeight < 1)
                continue;

            if (item.IsRemainder)
            {
                if (cellWidth >= 3 && cellHeight >= 3)
                    _hits.Add(new TreemapHit(new Rect(left, top, cellWidth, cellHeight), folder, null, false, RemainderCount: item.RemainderCount, RemainderBytes: item.Size));
                List<ColorSwatch> swatches = BuildRemainderSwatches(ranked, item.RemainderStartIndex);
                DrawRemainderMosaic(pixels, stride, canvasHeight, left, top, cellWidth, cellHeight, swatches);
                continue;
            }

            bool canRecurse = item.Folder is { } child
                && depth < 12
                && cellWidth >= minRecurseSize
                && cellHeight >= minRecurseSize
                && (child.Children.Count > 0 || child.Files.Count > 0);

            if (cellWidth >= 3 && cellHeight >= 3)
                _hits.Add(new TreemapHit(new Rect(left, top, cellWidth, cellHeight), item.Folder, item.File, false));

            int gap = depth == 0 && cellWidth > 6 && cellHeight > 6
                ? Math.Max(1, (int)Math.Round(PixelsPerDip))
                : 0;
            int fillX = left + gap;
            int fillY = top + gap;
            int fillW = cellWidth - gap * 2;
            int fillH = cellHeight - gap * 2;
            if (fillW < 1 || fillH < 1)
                continue;

            if (canRecurse)
            {
                // Draw a compact folder header only on top levels when the box has plenty of height,
                // while always recursing down so leaf files render as a detailed color mosaic.
                bool showHeader = depth < 2 && fillW >= minHeaderW && fillH >= minHeaderH;
                int header = showHeader
                    ? Math.Min(fillH / 4, Math.Max(14, (int)Math.Round(16 * PixelsPerDip)))
                    : 0;

                Color band = TypeColors.Folder(Dark);
                Fill(pixels, stride, canvasHeight, fillX, fillY, fillW, fillH, band);

                if (header > 0)
                {
                    Color headerFill = Darken(band);
                    Fill(pixels, stride, canvasHeight, fillX + 1, fillY + 1, Math.Max(1, fillW - 2), header - 1, headerFill);
                    if (ShowNames && fillW >= minLabelW && header >= minLabelH - 2)
                        labels.Add(new Label(fillX + pad, fillY + 1, Math.Max(1, fillW - pad * 2), header - 1, item.Folder!.Name, InkFor(headerFill)));
                    Paint(pixels, stride, canvasHeight, item.Folder!, fillX + 1, fillY + header, Math.Max(1, fillW - 2), Math.Max(1, fillH - header - 1), depth + 1, labels);
                }
                else
                {
                    int inset = fillW >= 6 && fillH >= 6 ? 1 : 0;
                    Paint(pixels, stride, canvasHeight, item.Folder!, fillX + inset, fillY + inset, Math.Max(1, fillW - inset * 2), Math.Max(1, fillH - inset * 2), depth + 1, labels);
                }

                // Draw outer border last so folder boundaries stay crisp over nested leaves
                if (fillW >= 4 && fillH >= 4)
                    Stroke(pixels, stride, canvasHeight, fillX, fillY, fillW, fillH, TypeColors.FolderBorder(Dark));
            }
            else
            {
                bool isFolder = item.Folder is not null;
                Color fill = isFolder ? RepresentativeColor(item.Folder!) : TypeColors.For(item.File!.Extension, Dark, Monochrome);
                Fill(pixels, stride, canvasHeight, fillX, fillY, fillW, fillH, fill);

                if (fillW >= 4 && fillH >= 4)
                {
                    Color border = isFolder
                        ? TypeColors.FolderBorder(Dark)
                        : Dark ? Color.FromArgb(140, 0, 0, 0) : Color.FromArgb(90, 255, 255, 255);
                    StrokeBlend(pixels, stride, canvasHeight, fillX, fillY, fillW, fillH, border);
                }

                if (!isFolder && HighContrast)
                {
                    int slot = TypeColors.SlotFor(item.File!.Extension);
                    DrawHatch(pixels, stride, canvasHeight, fillX, fillY, fillW, fillH, slot, InkFor(fill));
                }

                if (ShowNames && fillW >= minLabelW && fillH >= minLabelH)
                {
                    int labelH = Math.Min(fillH - 2, Math.Max(14, (int)Math.Round(16 * PixelsPerDip)));
                    labels.Add(new Label(fillX + pad, fillY + 1, Math.Max(1, fillW - pad * 2), labelH, item.Folder?.Name ?? item.File?.Name ?? "", InkFor(fill)));
                }
            }
        }
    }

    private List<ColorSwatch> BuildRemainderSwatches(List<TreemapLayoutItem> ranked, int startIndex)
    {
        var weights = new Dictionary<uint, (Color Color, long Bytes)>();
        int limit = Math.Min(ranked.Count, startIndex + 2048);
        for (int i = startIndex; i < limit; i++)
        {
            TreemapLayoutItem it = ranked[i];
            Color color = it.Folder is { } sub
                ? RepresentativeColor(sub)
                : TypeColors.For(it.File!.Extension, Dark, Monochrome);
            uint key = ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            if (weights.TryGetValue(key, out var existing))
                weights[key] = (color, existing.Bytes + it.Size);
            else
                weights[key] = (color, it.Size);
        }

        var list = new List<ColorSwatch>(weights.Count);
        foreach (var entry in weights.Values)
            list.Add(new ColorSwatch(entry.Color, entry.Bytes));
        list.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        if (list.Count > 24)
            list.RemoveRange(24, list.Count - 24);
        return list;
    }

    private void DrawRemainderMosaic(byte[] pixels, int stride, int canvasHeight, int x, int y, int width, int height, List<ColorSwatch> swatches)
    {
        if (swatches.Count == 0)
        {
            Fill(pixels, stride, canvasHeight, x, y, width, height, TypeColors.Folder(Dark));
            return;
        }

        Color fallback = swatches[0].Color;
        if (width < 4 || height < 4)
        {
            Fill(pixels, stride, canvasHeight, x, y, width, height, fallback);
            return;
        }

        int tile = Math.Max(3, (int)Math.Round(4 * PixelsPerDip));
        int cols = Math.Max(1, (width + tile - 1) / tile);
        int rows = Math.Max(1, (height + tile - 1) / tile);

        long totalWeight = 0;
        foreach (ColorSwatch s in swatches)
            totalWeight += Math.Max(1, s.Bytes);

        // Precompute cumulative color thresholds for fast proportional tiling
        Span<long> prefix = swatches.Count <= 32 ? stackalloc long[swatches.Count] : new long[swatches.Count];
        long acc = 0;
        for (int i = 0; i < swatches.Count; i++)
        {
            acc += Math.Max(1, swatches[i].Bytes);
            prefix[i] = acc;
        }

        Color gridBorder = Dark ? Color.FromArgb(120, 0, 0, 0) : Color.FromArgb(80, 255, 255, 255);

        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            int ty = y + r * tile;
            int th = Math.Min(tile, y + height - ty);
            if (th <= 0)
                continue;

            for (int c = 0; c < cols; c++)
            {
                int tx = x + c * tile;
                int tw = Math.Min(tile, x + width - tx);
                if (tw <= 0)
                    continue;

                // Spread swatches evenly using a golden-ratio fractional step
                double frac = ((idx * 0.6180339887498949) % 1.0);
                long target = (long)(frac * totalWeight);
                Color color = fallback;
                for (int s = 0; s < prefix.Length; s++)
                {
                    if (target < prefix[s])
                    {
                        color = swatches[s].Color;
                        break;
                    }
                }

                Fill(pixels, stride, canvasHeight, tx, ty, tw, th, color);
                if (tw >= 3 && th >= 3)
                    StrokeBlend(pixels, stride, canvasHeight, tx, ty, tw, th, gridBorder);
                idx++;
            }
        }
    }

    private Color RepresentativeColor(FolderNode folder)
    {
        FolderNode? cur = folder;
        for (int depth = 0; depth < 3 && cur is not null; depth++)
        {
            if (cur.Files.Count > 0)
            {
                FileEntry? best = null;
                int checkedCount = 0;
                foreach (FileEntry file in cur.Files)
                {
                    if (best is null || file.Size > best.Size)
                        best = file;
                    if (++checkedCount >= 8)
                        break;
                }
                if (best is not null)
                    return TypeColors.For(best.Extension, Dark, Monochrome);
            }

            FolderNode? next = null;
            foreach (FolderNode child in cur.Children)
            {
                next = child;
                break;
            }
            cur = next;
        }

        return TypeColors.Folder(Dark);
    }

    private static Color Darken(Color color)
        => Color.FromArgb(255, (byte)(color.R * 0.85), (byte)(color.G * 0.85), (byte)(color.B * 0.85));

    private static Color InkFor(Color fill)
    {
        double luma = 0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B;
        return luma > 150 ? Color.FromArgb(255, 24, 24, 26) : Colors.White;
    }

    private void DrawLabels(byte[] pixels, int width, int height, List<Label> labels)
    {
        if (labels.Count == 0)
            return;

        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            using var format = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = Math.Max(9.5f, 10.5f * PixelsPerDip),
                WordWrapping = CanvasWordWrapping.NoWrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis,
                VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            foreach (Label label in labels)
            {
                if (label.Width < 12 || label.Height < 12)
                    continue;
                var bounds = new Rect(label.X, label.Y, label.Width, label.Height);
                using (session.CreateLayer(1, bounds))
                    session.DrawText(label.Text, bounds, label.Ink, format);
            }
        }

        byte[] text = target.GetPixelBytes();
        for (int i = 0; i + 3 < text.Length && i + 3 < pixels.Length; i += 4)
        {
            byte alpha = text[i + 3];
            if (alpha == 0)
                continue;
            if (alpha == 255)
            {
                pixels[i] = text[i];
                pixels[i + 1] = text[i + 1];
                pixels[i + 2] = text[i + 2];
                pixels[i + 3] = 255;
                continue;
            }

            int inverse = 255 - alpha;
            pixels[i] = (byte)((text[i] * alpha + pixels[i] * inverse) / 255);
            pixels[i + 1] = (byte)((text[i + 1] * alpha + pixels[i + 1] * inverse) / 255);
            pixels[i + 2] = (byte)((text[i + 2] * alpha + pixels[i + 2] * inverse) / 255);
            pixels[i + 3] = 255;
        }
    }

    private readonly record struct Label(int X, int Y, int Width, int Height, string Text, Color Ink);

    /// <summary>The rectangle under a point, or null when the point misses every one large enough to hit.</summary>
    public TreemapHit? Hit(Point point)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Bounds.Contains(point))
                return _hits[i];
        }

        return null;
    }

    private static void Fill(byte[] pixels, int stride, int height, int x, int y, int width, int rowHeight, Color color)
    {
        int x0 = Math.Clamp(x, 0, stride);
        int y0 = Math.Clamp(y, 0, height);
        int x1 = Math.Clamp(x + width, 0, stride);
        int y1 = Math.Clamp(y + rowHeight, 0, height);
        for (int row = y0; row < y1; row++)
        {
            int offset = (row * stride + x0) * 4;
            for (int column = x0; column < x1; column++)
            {
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = color.A;
                offset += 4;
            }
        }
    }

    private static void Stroke(byte[] pixels, int stride, int height, int x, int y, int width, int rowHeight, Color color)
    {
        if (width < 2 || rowHeight < 2)
            return;
        Fill(pixels, stride, height, x, y, width, 1, color);
        Fill(pixels, stride, height, x, y + rowHeight - 1, width, 1, color);
        Fill(pixels, stride, height, x, y, 1, rowHeight, color);
        Fill(pixels, stride, height, x + width - 1, y, 1, rowHeight, color);
    }

    private static void StrokeBlend(byte[] pixels, int stride, int height, int x, int y, int width, int rowHeight, Color color)
    {
        if (width < 2 || rowHeight < 2)
            return;
        int x0 = Math.Clamp(x, 0, stride - 1);
        int y0 = Math.Clamp(y, 0, height - 1);
        int x1 = Math.Clamp(x + width - 1, 0, stride - 1);
        int y1 = Math.Clamp(y + rowHeight - 1, 0, height - 1);
        for (int col = x0; col <= x1; col++)
        {
            SetPixel(pixels, stride, col, y0, color);
            SetPixel(pixels, stride, col, y1, color);
        }
        for (int row = y0 + 1; row < y1; row++)
        {
            SetPixel(pixels, stride, x0, row, color);
            SetPixel(pixels, stride, x1, row, color);
        }
    }

    private static void StrokeDashed(byte[] pixels, int stride, int height, int x, int y, int width, int rowHeight, Color color, int dash)
    {
        int x0 = Math.Clamp(x, 0, stride - 1);
        int y0 = Math.Clamp(y, 0, height - 1);
        int x1 = Math.Clamp(x + width - 1, 0, stride - 1);
        int y1 = Math.Clamp(y + rowHeight - 1, 0, height - 1);
        int period = dash * 2;
        for (int col = x0; col <= x1; col++)
        {
            if ((col - x0) % period < dash)
            {
                SetPixel(pixels, stride, col, y0, color);
                SetPixel(pixels, stride, col, y1, color);
            }
        }
        for (int row = y0; row <= y1; row++)
        {
            if ((row - y0) % period < dash)
            {
                SetPixel(pixels, stride, x0, row, color);
                SetPixel(pixels, stride, x1, row, color);
            }
        }
    }

    private static void DrawHatch(byte[] pixels, int stride, int height, int x, int y, int width, int rowHeight, int slot, Color ink)
    {
        int step = 6 + (slot % 3) * 2;
        int x0 = Math.Clamp(x, 0, stride);
        int y0 = Math.Clamp(y, 0, height);
        int x1 = Math.Clamp(x + width, 0, stride);
        int y1 = Math.Clamp(y + rowHeight, 0, height);
        bool reverse = (slot & 1) == 1;
        for (int row = y0; row < y1; row++)
        {
            for (int col = x0; col < x1; col++)
            {
                int key = reverse ? col - row : col + row;
                if (((key % step) + step) % step == 0)
                    SetPixel(pixels, stride, col, row, Color.FromArgb(80, ink.R, ink.G, ink.B));
            }
        }
    }

    private static void SetPixel(byte[] pixels, int stride, int x, int y, Color color)
    {
        int offset = (y * stride + x) * 4;
        if (color.A == 255)
        {
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = 255;
            return;
        }

        int a = color.A;
        int inv = 255 - a;
        pixels[offset] = (byte)((color.B * a + pixels[offset] * inv) / 255);
        pixels[offset + 1] = (byte)((color.G * a + pixels[offset + 1] * inv) / 255);
        pixels[offset + 2] = (byte)((color.R * a + pixels[offset + 2] * inv) / 255);
        pixels[offset + 3] = 255;
    }

    private readonly record struct ColorSwatch(Color Color, long Bytes);
}
