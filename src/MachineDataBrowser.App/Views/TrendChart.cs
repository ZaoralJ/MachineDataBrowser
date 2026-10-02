using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

/// <summary>
/// Minimal time/value line chart for recorded numeric samples. Points with different <see cref="TrendPoint.Series"/>
/// are drawn as separate coloured lines on a shared axis with a legend. Each line is reduced to a min/max pair per pixel
/// column before drawing, so tens of thousands of samples stay cheap to render every refresh.
/// </summary>
public sealed class TrendChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<TrendPoint>?> PointsProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<TrendPoint>?>(nameof(Points));

    public static readonly StyledProperty<TrendPoint?> HighlightProperty =
        AvaloniaProperty.Register<TrendChart, TrendPoint?>(nameof(Highlight), defaultBindingMode: BindingMode.TwoWay);

    private const double AxisWidth = 64;
    private const int MaxLegendEntries = 8;

    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#2F9E6E")), new SolidColorBrush(Color.Parse("#E5484D")),
        new SolidColorBrush(Color.Parse("#F5A524")), new SolidColorBrush(Color.Parse("#0091FF")),
        new SolidColorBrush(Color.Parse("#AB4ABA")), new SolidColorBrush(Color.Parse("#12A594")),
        new SolidColorBrush(Color.Parse("#D6409F")), new SolidColorBrush(Color.Parse("#8E8C99")),
    ];
    private const double AxisHeight = 20;
    private const double Pad = 8;

    static TrendChart() => AffectsRender<TrendChart>(PointsProperty, HighlightProperty);

    public TrendChart() => Cursor = new Cursor(StandardCursorType.Cross);

    /// <summary>The sample drawn with a marker; clicking the chart selects the nearest sample.</summary>
    public TrendPoint? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Points is not { Count: > 1 } points || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var plot = PlotArea(new Rect(Bounds.Size));
        var span = Math.Max((points[^1].Time - points[0].Time).TotalMilliseconds, 1);
        var at = points[0].Time.AddMilliseconds(Math.Clamp((e.GetPosition(this).X - plot.Left) / plot.Width, 0, 1) * span);

        // Points are in time order: binary search for the sample closest to the clicked time.
        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].Time < at) lo = mid + 1; else hi = mid;
        }

        if (lo > 0 && (at - points[lo - 1].Time) < (points[lo].Time - at))
        {
            lo--;
        }

        // Several series: among the samples closest in time, take the one nearest to the clicked height.
        // Compare heights as a fraction of the plot, so it also works when lines are scaled to their own ranges.
        var (min, max) = Padded(points.Min(p => p.Value), points.Max(p => p.Value));
        var clicked = 1 - ((e.GetPosition(this).Y - plot.Top) / plot.Height);
        double Fraction(TrendPoint p) => _ranges is { } r && r.TryGetValue(p.Series, out var own) ? (p.Value - own.Min) / (own.Max - own.Min) : (p.Value - min) / (max - min);
        var window = TimeSpan.FromMilliseconds(span / Math.Max(plot.Width, 1) * 4);
        Highlight = points.Where(p => (p.Time - points[lo].Time).Duration() <= window)
            .OrderBy(p => Math.Abs(Fraction(p) - clicked))
            .FirstOrDefault() ?? points[lo];
        e.Handled = true;
    }

    private static Rect PlotArea(Rect bounds) =>
        new(AxisWidth, Pad, Math.Max(1, bounds.Width - AxisWidth - Pad), Math.Max(1, bounds.Height - AxisHeight - Pad));

    public IReadOnlyList<TrendPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brush("AppSurfaceBrush", Brushes.Transparent), bounds);
        if (Points is not { Count: > 1 } points)
        {
            return;
        }

        var plot = PlotArea(bounds);
        var t0 = points[0].Time;
        var span = Math.Max((points[^1].Time - t0).TotalMilliseconds, 1);
        var (min, max) = Padded(points.Min(p => p.Value), points.Max(p => p.Value));

        // Lines whose ranges differ a lot (e.g. 0–100 next to ±2·10⁹) would flatten each other on a shared axis:
        // then every line is scaled to its own range and the axis shows percent of range.
        var ranges = points.GroupBy(p => p.Series).ToDictionary(g => g.Key, g => Padded(g.Min(p => p.Value), g.Max(p => p.Value)));
        var widths = ranges.Values.Select(r => r.Max - r.Min).ToList();
        var normalized = ranges.Count > 1 && widths.Max() / widths.Min() > 10;
        _ranges = normalized ? ranges : null;
        double X(DateTimeOffset t) => plot.Left + ((t - t0).TotalMilliseconds / span * plot.Width);
        Func<double, double> YOf(string series)
        {
            var (lo, hi) = normalized ? ranges[series] : (min, max);
            return v => plot.Bottom - ((v - lo) / (hi - lo) * plot.Height);
        }

        var grid = new Pen(Brush("AppBorderBrush", Brushes.Gray), 1);
        var text = Brush("AppMutedTextBrush", Brushes.Gray);
        for (var i = 0; i <= 4; i++)
        {
            var y = Math.Round(plot.Bottom - (plot.Height * i / 4)) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(context, normalized ? $"{i * 25}%" : Format(min + ((max - min) * i / 4)), text, new Point(plot.Left - 6, y), alignRight: true);
        }

        DrawText(context, Timestamps.Format(points[0].Time), text, new Point(plot.Left, plot.Bottom + 12), alignRight: false);
        DrawText(context, Timestamps.Format(points[^1].Time), text, new Point(plot.Right, plot.Bottom + 12), alignRight: true);

        var accent = Brush("AppAccentBrush", Brushes.DodgerBlue);
        var series = points.GroupBy(p => p.Series).ToList();
        IBrush ColorOf(string name) => series.Count == 1 ? accent : series.FindIndex(s => s.Key == name) is var i and >= 0 ? (i == 0 ? accent : Palette[(i - 1) % Palette.Length]) : accent;

        using (context.PushClip(plot.Inflate(1)))
        {
            foreach (var line in series)
            {
                context.DrawGeometry(null, new Pen(ColorOf(line.Key), 1.5), Line([.. line], X, YOf(line.Key)));
            }
        }

        if (series.Count > 1)
        {
            var y = plot.Top + 4;
            foreach (var line in series.Take(MaxLegendEntries))
            {
                context.DrawLine(new Pen(ColorOf(line.Key), 2.5), new Point(plot.Left + 8, y + 7), new Point(plot.Left + 22, y + 7));
                var label = normalized ? $"{line.Key}  ({Format(line.Min(p => p.Value))} … {Format(line.Max(p => p.Value))})" : line.Key;
                DrawText(context, label, text, new Point(plot.Left + 28, y + 7), alignRight: false);
                y += 16;
            }

            if (series.Count > MaxLegendEntries)
            {
                DrawText(context, $"+{series.Count - MaxLegendEntries} more", text, new Point(plot.Left + 28, y + 7), alignRight: false);
            }
        }

        if (Highlight is { } h && h.Time >= t0 && h.Time <= points[^1].Time)
        {
            var x = Math.Round(X(h.Time)) + 0.5;
            var y = YOf(ranges.ContainsKey(h.Series) ? h.Series : series[0].Key)(h.Value);
            var guide = Brush("AppMutedTextBrush", Brushes.Gray);
            context.DrawLine(new Pen(guide, 1, new DashStyle([3, 3], 0)), new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawEllipse(Brush("AppSurfaceBrush", Brushes.White), new Pen(ColorOf(h.Series), 2), new Point(x, y), 4.5, 4.5);

            var label = new FormattedText(
                $"{(series.Count > 1 ? h.Series + "  ·  " : string.Empty)}{Format(h.Value)}  ·  {Timestamps.Format(h.Time)}",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brush("SystemControlForegroundBaseHighBrush", Brushes.White));
            var box = new Rect(0, 0, label.Width + 12, label.Height + 6);
            var left = x + 8 + box.Width > plot.Right ? x - 8 - box.Width : x + 8;
            var top = Math.Clamp(y - box.Height - 6, plot.Top, plot.Bottom - box.Height);
            box = box.Translate(new Vector(left, top));
            context.DrawRectangle(Brush("AppSurfaceAltBrush", Brushes.Black), new Pen(Brush("AppBorderStrongBrush", Brushes.Gray), 1), box, 4, 4);
            context.DrawText(label, new Point(box.X + 6, box.Y + 3));
        }
    }

    private Dictionary<string, (double Min, double Max)>? _ranges;

    private static (double Min, double Max) Padded(double min, double max)
    {
        if (max - min < 1e-12)
        {
            (min, max) = (min - 1, max + 1);
        }

        var pad = (max - min) * 0.05;
        return (min - pad, max + pad);
    }

    private static StreamGeometry Line(List<TrendPoint> points, Func<DateTimeOffset, double> X, Func<double, double> Y)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            // One min/max pair per pixel column keeps the envelope of dense data without drawing every point.
            var column = int.MinValue;
            double lo = 0, hi = 0, last = 0;
            var started = false;
            foreach (var p in points)
            {
                var x = (int)X(p.Time);
                if (x != column)
                {
                    if (started)
                    {
                        g.LineTo(new Point(column, Y(lo)));
                        g.LineTo(new Point(column, Y(hi)));
                        g.LineTo(new Point(column, Y(last)));
                    }

                    column = x;
                    lo = hi = p.Value;
                    if (!started)
                    {
                        g.BeginFigure(new Point(x, Y(p.Value)), isFilled: false);
                        started = true;
                    }
                }
                else
                {
                    lo = Math.Min(lo, p.Value);
                    hi = Math.Max(hi, p.Value);
                }

                last = p.Value;
            }

            if (started)
            {
                g.LineTo(new Point(column, Y(lo)));
                g.LineTo(new Point(column, Y(hi)));
                g.LineTo(new Point(column, Y(last)));
                g.EndFigure(isClosed: false);
            }
        }

        return geometry;
    }

    private static string Format(double value) =>
        Math.Abs(value) >= 1e6 || (Math.Abs(value) < 1e-3 && value != 0)
            ? value.ToString("0.##E+0", CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void DrawText(DrawingContext context, string text, IBrush brush, Point anchor, bool alignRight)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
        var x = alignRight ? anchor.X - formatted.Width : anchor.X;
        context.DrawText(formatted, new Point(x, anchor.Y - (formatted.Height / 2)));
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
