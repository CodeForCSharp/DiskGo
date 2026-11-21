using DiskGo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DiskGo;

/// <summary>
/// A WinUI 3 pill-shaped segmented bar showing disk usage broken down by top
/// file types, reserved space, and free space, with per-segment hover tooltips.
/// </summary>
public sealed partial class SegmentedBar : UserControl
{
    private readonly Grid _segments = new();
    private readonly Border _frame = new();

    public SegmentedBar()
    {
        Height = 8;
        _frame.Height = 8;
        _frame.CornerRadius = new CornerRadius(4);
        _frame.BorderThickness = new Thickness(1);
        _frame.Child = _segments;
        Content = _frame;
    }

    public void Update(ScanResult? result, AppSettings settings, bool dark)
    {
        _segments.ColumnDefinitions.Clear();
        _segments.Children.Clear();

        _frame.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(36, 255, 255, 255)
            : Windows.UI.Color.FromArgb(22, 0, 0, 0));
        _frame.BorderBrush = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(44, 255, 255, 255)
            : Windows.UI.Color.FromArgb(36, 0, 0, 0));

        if (result is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        long total = result.Drive.IsKnown ? result.Drive.Total : result.Root.Size;
        if (total <= 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        var slices = new List<(string Label, long Bytes, Windows.UI.Color Color)>();
        long shownFileBytes = 0;

        const int maxTypes = 10;
        for (int i = 0; i < result.Extensions.Count && i < maxTypes; i++)
        {
            ExtensionTotal ext = result.Extensions[i];
            if (ext.Size <= 0)
                continue;
            if ((double)ext.Size / total < 0.003 && slices.Count >= 4)
                break;

            string label = ext.Extension == "(none)" ? "(无扩展名)" : ext.Extension;
            slices.Add((label, ext.Size, TypeColors.For(ext.Extension, dark)));
            shownFileBytes += ext.Size;
        }

        long accountedRoot = result.Root.Size;
        long otherFiles = Math.Max(0, accountedRoot - shownFileBytes);
        if (otherFiles > 0)
        {
            slices.Add(("其他类型", otherFiles, dark
                ? Windows.UI.Color.FromArgb(255, 118, 124, 134)
                : Windows.UI.Color.FromArgb(255, 160, 168, 178)));
        }

        if (result.Drive.IsKnown)
        {
            long otherUsed = Math.Max(0, result.Drive.Used - Math.Max(accountedRoot, result.Drive.Reserved));
            if (result.Drive.Reserved > 0)
                slices.Add(("系统预留 / 元数据", result.Drive.Reserved, TypeColors.Reserved(dark)));
            else if (otherUsed > 0 && accountedRoot < result.Drive.Used)
                slices.Add(("其他已用空间", otherUsed, TypeColors.Reserved(dark)));

            if (result.Drive.Free > 0)
                slices.Add(("空闲空间", result.Drive.Free, Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        }

        int col = 0;
        foreach ((string label, long bytes, Windows.UI.Color color) in slices)
        {
            if (bytes <= 0)
                continue;

            _segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
            var rect = new Rectangle
            {
                Fill = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            double pct = 100.0 * bytes / total;
            string formatted = SizeText.Format(bytes, settings.SizeUnit, settings.BinaryUnits, settings.DecimalPlaces);
            ToolTipService.SetToolTip(rect, $"{label}  ·  {formatted} ({pct:0.0}%)");

            Grid.SetColumn(rect, col++);
            _segments.Children.Add(rect);
        }
    }
}
