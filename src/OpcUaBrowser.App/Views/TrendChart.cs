using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

/// <summary>
/// Minimal time/value line chart for recorded numeric samples. Points are reduced to a min/max pair per pixel
/// column before drawing, so tens of thousands of samples stay cheap to render every refresh.
/// </summary>
public sealed class TrendChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<TrendPoint>?> PointsProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<TrendPoint>?>(nameof(Points));

    private const double AxisWidth = 64;
    private const double AxisHeight = 20;
    private const double Pad = 8;

    static TrendChart() => AffectsRender<TrendChart>(PointsProperty);

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

        var plot = new Rect(AxisWidth, Pad, Math.Max(1, bounds.Width - AxisWidth - Pad), Math.Max(1, bounds.Height - AxisHeight - Pad));
        var t0 = points[0].Time;
        var span = Math.Max((points[^1].Time - t0).TotalMilliseconds, 1);
        var (min, max) = (points.Min(p => p.Value), points.Max(p => p.Value));
        if (max - min < 1e-12)
        {
            (min, max) = (min - 1, max + 1);
        }

        var pad = (max - min) * 0.05;
        (min, max) = (min - pad, max + pad);
        double X(DateTimeOffset t) => plot.Left + ((t - t0).TotalMilliseconds / span * plot.Width);
        double Y(double v) => plot.Bottom - ((v - min) / (max - min) * plot.Height);

        var grid = new Pen(Brush("AppBorderBrush", Brushes.Gray), 1);
        var text = Brush("AppMutedTextBrush", Brushes.Gray);
        for (var i = 0; i <= 4; i++)
        {
            var value = min + ((max - min) * i / 4);
            var y = Math.Round(Y(value)) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(context, Format(value), text, new Point(plot.Left - 6, y), alignRight: true);
        }

        DrawText(context, points[0].Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), text, new Point(plot.Left, plot.Bottom + 12), alignRight: false);
        DrawText(context, points[^1].Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), text, new Point(plot.Right, plot.Bottom + 12), alignRight: true);

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

            g.LineTo(new Point(column, Y(lo)));
            g.LineTo(new Point(column, Y(hi)));
            g.LineTo(new Point(column, Y(last)));
            g.EndFigure(isClosed: false);
        }

        using (context.PushClip(plot.Inflate(1)))
        {
            context.DrawGeometry(null, new Pen(Brush("AppAccentBrush", Brushes.DodgerBlue), 1.5), geometry);
        }
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
