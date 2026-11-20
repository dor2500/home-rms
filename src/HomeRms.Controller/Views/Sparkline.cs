using System.Windows;
using System.Windows.Media;

namespace HomeRms.Controller.Views;

/// <summary>
/// Minimal sparkline: draws a series as a single StreamGeometry polyline (no chart library).
/// <see cref="Maximum"/> = 0 means auto-scale to the series maximum.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.4, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Number of samples the full width represents (older points scroll off the left).</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(150, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double> Values { get => (IReadOnlyList<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        // Transparent hit-test surface + baseline so empty sparklines still occupy their space.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var values = Values;
        if (values is null || values.Count < 2 || w <= 0 || h <= 0) return;

        var max = Maximum > 0 ? Maximum : Math.Max(1e-9, values.Max());
        var cap = Math.Max(values.Count, Math.Max(2, Capacity));
        var step = w / (cap - 1);
        var x0 = w - (values.Count - 1) * step;
        var pad = StrokeThickness;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var v = Math.Clamp(values[i] / max, 0, 1);
                var p = new Point(x0 + i * step, pad + (h - 2 * pad) * (1 - v));
                if (i == 0) ctx.BeginFigure(p, isFilled: false, isClosed: false);
                else ctx.LineTo(p, isStroked: true, isSmoothJoin: false);
            }
        }
        geo.Freeze();
        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
