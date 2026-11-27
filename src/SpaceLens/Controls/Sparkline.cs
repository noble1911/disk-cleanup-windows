using System.Windows;
using System.Windows.Media;

namespace SpaceLens.Controls;

/// <summary>Small area chart for the last N samples of a metric.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Fixed top of the scale (e.g. 100 for percentages). 0 = scale to the largest value.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>How many samples the width represents; fewer values are right-aligned.</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)), 1);
        dc.DrawLine(gridPen, new Point(0, h - 0.5), new Point(w, h - 0.5));

        var values = Values;
        if (values is null || values.Count < 2) return;

        double max = Maximum > 0 ? Maximum : Math.Max(1e-9, values.Max() * 1.15);
        int cap = Math.Max(Capacity, values.Count);
        double step = w / (cap - 1);
        double x0 = w - (values.Count - 1) * step;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            Point P(int i) => new(x0 + i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 2) - 1);
            lc.BeginFigure(P(0), false, false);
            ac.BeginFigure(new Point(x0, h), true, true);
            ac.LineTo(P(0), false, false);
            for (int i = 1; i < values.Count; i++)
            {
                lc.LineTo(P(i), true, true);
                ac.LineTo(P(i), false, false);
            }
            ac.LineTo(new Point(x0 + (values.Count - 1) * step, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var fill = Stroke.CloneCurrentValue();
        fill.Opacity = 0.15;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}
