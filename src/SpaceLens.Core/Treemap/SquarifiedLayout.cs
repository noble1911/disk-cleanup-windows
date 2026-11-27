namespace SpaceLens.Core.Treemap;

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing, van Wijk). Produces rectangles with aspect
/// ratios close to 1, which makes sizes easy to compare by eye.
/// </summary>
public static class SquarifiedLayout
{
    /// <param name="sizes">Positive weights, sorted largest first.</param>
    /// <param name="bounds">Area to fill.</param>
    /// <param name="result">Receives one rectangle per weight, in the same order.</param>
    public static void Layout(ReadOnlySpan<double> sizes, RectD bounds, Span<RectD> result)
    {
        if (result.Length < sizes.Length) throw new ArgumentException("Result span is too small.", nameof(result));

        double remaining = 0;
        foreach (var s in sizes) remaining += s;
        if (remaining <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            for (int k = 0; k < sizes.Length; k++) result[k] = new RectD(bounds.X, bounds.Y, 0, 0);
            return;
        }

        var rect = bounds;
        int i = 0;
        while (i < sizes.Length)
        {
            double scale = rect.Width * rect.Height / remaining;
            double side = Math.Min(rect.Width, rect.Height);
            if (side <= 0 || scale <= 0)
            {
                for (; i < sizes.Length; i++) result[i] = new RectD(rect.X, rect.Y, 0, 0);
                return;
            }

            int start = i;
            int end = i;
            double rowArea = 0;
            double worst = double.MaxValue;
            double max = sizes[start] * scale;

            while (end < sizes.Length)
            {
                double area = sizes[end] * scale;
                double newRowArea = rowArea + area;
                double candidate = Worst(newRowArea, side, max, area);
                if (end > start && candidate > worst) break;
                worst = candidate;
                rowArea = newRowArea;
                end++;
            }

            // Last row: absorb rounding so the rectangle is filled exactly.
            bool last = end == sizes.Length;
            double thickness = last ? (rect.Width >= rect.Height ? rect.Width : rect.Height) : rowArea / side;

            if (rect.Width >= rect.Height)
            {
                // Column on the left, items stacked top to bottom.
                double y = rect.Y;
                for (int k = start; k < end; k++)
                {
                    double h = k == end - 1 ? rect.Bottom - y : sizes[k] * scale / (rowArea / side);
                    result[k] = new RectD(rect.X, y, thickness, Math.Max(0, h));
                    y += h;
                }
                rect = new RectD(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height);
            }
            else
            {
                // Row along the top, items left to right.
                double x = rect.X;
                for (int k = start; k < end; k++)
                {
                    double w = k == end - 1 ? rect.Right - x : sizes[k] * scale / (rowArea / side);
                    result[k] = new RectD(x, rect.Y, Math.Max(0, w), thickness);
                    x += w;
                }
                rect = new RectD(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
            }

            remaining -= rowArea / scale;
            i = end;
        }
    }

    private static double Worst(double rowArea, double side, double max, double min)
    {
        double s2 = side * side;
        double r2 = rowArea * rowArea;
        return Math.Max(s2 * max / r2, r2 / (s2 * min));
    }
}
