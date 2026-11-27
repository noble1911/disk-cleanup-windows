using SpaceLens.Core.Treemap;

namespace SpaceLens.Core.Tests;

public class TreemapTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(100)]
    public void Layout_fills_bounds_without_overlap(int count)
    {
        var random = new Random(count);
        var sizes = Enumerable.Range(0, count).Select(_ => random.NextDouble() * 1000 + 1).OrderByDescending(x => x).ToArray();
        var bounds = new RectD(10, 20, 800, 500);
        var rects = new RectD[count];

        SquarifiedLayout.Layout(sizes, bounds, rects);

        double total = sizes.Sum();
        for (int i = 0; i < count; i++)
        {
            var r = rects[i];
            Assert.True(r.X >= bounds.X - 1e-6 && r.Y >= bounds.Y - 1e-6, $"rect {i} starts outside bounds");
            Assert.True(r.Right <= bounds.Right + 1e-6 && r.Bottom <= bounds.Bottom + 1e-6, $"rect {i} ends outside bounds");
            Assert.Equal(sizes[i] / total * bounds.Area, r.Area, 3);
        }

        for (int i = 0; i < count; i++)
        for (int j = i + 1; j < count; j++)
        {
            var a = rects[i];
            var b = rects[j];
            double overlapW = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
            double overlapH = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
            Assert.False(overlapW > 1e-6 && overlapH > 1e-6, $"rects {i} and {j} overlap");
        }
    }

    [Fact]
    public void Layout_produces_reasonable_aspect_ratios()
    {
        var sizes = new double[] { 6, 6, 4, 3, 2, 2, 1 };
        var rects = new RectD[sizes.Length];
        SquarifiedLayout.Layout(sizes, new RectD(0, 0, 6, 4), rects);

        foreach (var r in rects)
        {
            double ratio = Math.Max(r.Width / r.Height, r.Height / r.Width);
            Assert.True(ratio < 4, $"aspect ratio {ratio} too extreme");
        }
    }
}
