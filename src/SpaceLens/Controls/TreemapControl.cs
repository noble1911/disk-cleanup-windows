using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SpaceLens.Core;
using SpaceLens.Core.Scanning;
using SpaceLens.Core.Treemap;

namespace SpaceLens.Controls;

public enum TreemapColorMode { Folder, FileType }

/// <summary>
/// Nested squarified treemap of an <see cref="FsNode"/> tree. The map is drawn into a cached
/// visual; hover and selection highlights are drawn on a separate overlay so moving the mouse
/// never re-lays-out the whole map.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const int MaxRectangles = 30_000;
    private const double HeaderHeight = 17;
    private const double MinLabelWidth = 46;

    private readonly DrawingVisual _map = new();
    private readonly DrawingVisual _overlay = new();
    private readonly VisualCollection _visuals;
    private readonly List<(Rect Rect, FsNode Node)> _hits = new(4096);
    private readonly Dictionary<int, SolidColorBrush> _brushes = new();
    private readonly Pen _border = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 0.6));
    private readonly Pen _hoverPen = Freeze(new Pen(Brushes.White, 2));
    private readonly Pen _hoverShadow = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 4));
    private readonly Pen _selectedPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)), 3));
    private readonly Brush _groupBrush;
    private readonly Typeface _typeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _boldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private FsNode? _hovered;
    private int _rectCount;
    private bool _dirty = true;

    public TreemapControl()
    {
        _visuals = new VisualCollection(this) { _map, _overlay };
        ClipToBounds = true;
        Focusable = true;
        var hatch = new DrawingBrush(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC8)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCF, 0xD4, 0xDB)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(((DrawingBrush)hatch).Drawing);
        _groupBrush = Freeze(new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute });
    }

    // ------------------------------------------------------------------ properties

    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(FsNode), typeof(TreemapControl), new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Invalidate()));

    public FsNode? Root
    {
        get => (FsNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(FsNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TreemapControl)d).DrawOverlay()));

    public FsNode? Selected
    {
        get => (FsNode?)GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    /// <summary>Bump this to redraw (e.g. while a scan is filling in the tree).</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(TreemapControl), new PropertyMetadata(0, (d, _) => ((TreemapControl)d).Invalidate()));

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public static readonly DependencyProperty ColorModeProperty = DependencyProperty.Register(
        nameof(ColorMode), typeof(TreemapColorMode), typeof(TreemapControl), new PropertyMetadata(TreemapColorMode.Folder, (d, _) => ((TreemapControl)d).Invalidate()));

    public TreemapColorMode ColorMode
    {
        get => (TreemapColorMode)GetValue(ColorModeProperty);
        set => SetValue(ColorModeProperty, value);
    }

    public static readonly DependencyProperty MaxDepthProperty = DependencyProperty.Register(
        nameof(MaxDepth), typeof(int), typeof(TreemapControl), new PropertyMetadata(14, (d, _) => ((TreemapControl)d).Invalidate()));

    public int MaxDepth
    {
        get => (int)GetValue(MaxDepthProperty);
        set => SetValue(MaxDepthProperty, value);
    }

    public FsNode? Hovered => _hovered;

    public event EventHandler<FsNode?>? HoverChanged;
    /// <summary>Double-click: zoom into a folder.</summary>
    public event EventHandler<FsNode>? NodeActivated;
    public event EventHandler<FsNode>? NodeContextRequested;

    // ------------------------------------------------------------------ visuals

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    private void Invalidate()
    {
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Invalidate();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent hit-test surface for the whole control.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_dirty) DrawMap();
    }

    private void DrawMap()
    {
        _dirty = false;
        _hits.Clear();
        _rectCount = 0;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);

        using (var dc = _map.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)), null, bounds);
            var root = Root;
            if (root is null || bounds.Width < 4 || bounds.Height < 4)
            {
                DrawCenteredText(dc, bounds, "Pick a drive or folder and press Scan to see where your space goes.");
            }
            else if (root.Size <= 0)
            {
                DrawCenteredText(dc, bounds, root.IsRead ? "This folder is empty." : "Scanning…");
            }
            else
            {
                DrawNode(dc, root, bounds, 0, hue: 210);
            }
        }
        DrawOverlay();
    }

    private void DrawNode(DrawingContext dc, FsNode node, Rect rect, int depth, double hue)
    {
        if (rect.Width < 1.5 || rect.Height < 1.5 || _rectCount >= MaxRectangles) return;
        _rectCount++;
        _hits.Add((rect, node));

        if (!node.IsContainer)
        {
            var fill = node.Kind == NodeKind.FileGroup ? _groupBrush : FileBrush(node, hue, depth);
            dc.DrawRectangle(fill, _border, rect);
            if (rect.Width >= MinLabelWidth && rect.Height >= 30)
            {
                DrawLabel(dc, node.Name, rect.X + 4, rect.Y + 3, rect.Width - 8, false, Brushes.Black, 11);
                DrawLabel(dc, ByteFormat.Format(node.Size), rect.X + 4, rect.Y + 17, rect.Width - 8, false, Brushes.Black, 10, 0.65);
            }
            else if (rect.Width >= MinLabelWidth && rect.Height >= 15)
            {
                DrawLabel(dc, node.Name, rect.X + 3, rect.Y + 1, rect.Width - 6, false, Brushes.Black, 10.5);
            }
            return;
        }

        dc.DrawRectangle(Brush(hue, 0.30, Math.Max(0.50, 0.86 - depth * 0.05)), _border, rect);

        Rect inner;
        bool header = rect.Height >= HeaderHeight + 12 && rect.Width >= MinLabelWidth;
        if (header)
        {
            string name = depth == 0 || node.Parent is null ? node.FullPath : node.Name;
            string text = $"{name}  ·  {ByteFormat.Format(node.Size)}";
            DrawLabel(dc, text, rect.X + 4, rect.Y + 1.5, rect.Width - 8, true, Brushes.Black, 11);
            inner = new Rect(rect.X + 2, rect.Y + HeaderHeight, Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - HeaderHeight - 2));
        }
        else
        {
            inner = new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 2));
        }

        if (depth >= MaxDepth || inner.Width < 3 || inner.Height < 3) return;

        var children = node.Children.Where(c => c.Size > 0).ToArray();
        if (children.Length == 0) return;
        Array.Sort(children, static (a, b) => b.Size.CompareTo(a.Size));

        // Children whose total is smaller than the parent (scan still running) leave a gap; keep proportions honest.
        double childTotal = 0;
        foreach (var c in children) childTotal += c.Size;
        double fraction = node.Size > 0 ? Math.Min(1, childTotal / node.Size) : 1;
        if (fraction < 0.999)
        {
            if (inner.Width >= inner.Height) inner.Width *= fraction;
            else inner.Height *= fraction;
        }

        // Drop tiny children: they would be invisible anyway and cost time.
        double minArea = 4.0 * childTotal / Math.Max(1, inner.Width * inner.Height);
        int count = 0;
        while (count < children.Length && (count < 1 || children[count].Size >= minArea)) count++;

        // The dropped tail still takes its share of the area (as an empty slot) so the visible items stay to scale.
        double rest = 0;
        for (int i = count; i < children.Length; i++) rest += children[i].Size;
        var sizes = new double[rest > 0 ? count + 1 : count];
        for (int i = 0; i < count; i++) sizes[i] = children[i].Size;
        if (rest > 0) sizes[count] = rest;
        var rects = new RectD[sizes.Length];
        SquarifiedLayout.Layout(sizes, new RectD(inner.X, inner.Y, inner.Width, inner.Height), rects);

        for (int i = 0; i < count; i++)
        {
            double childHue = depth == 0 ? (hue + i * 137.508) % 360 : hue;
            var r = rects[i];
            DrawNode(dc, children[i], new Rect(r.X, r.Y, r.Width, r.Height), depth + 1, childHue);
        }
    }

    private Brush FileBrush(FsNode file, double hue, int depth)
    {
        if (ColorMode == TreemapColorMode.Folder) return Brush(hue, 0.55, Math.Max(0.48, 0.72 - depth * 0.02));
        return FileCategories.Of(file.Extension) switch
        {
            FileCategory.Video => Brush(275, 0.55, 0.70),
            FileCategory.Image => Brush(140, 0.45, 0.68),
            FileCategory.Audio => Brush(320, 0.50, 0.75),
            FileCategory.Document => Brush(210, 0.60, 0.72),
            FileCategory.Archive => Brush(30, 0.75, 0.68),
            FileCategory.DiskImage => Brush(0, 0.60, 0.70),
            FileCategory.Executable => Brush(350, 0.45, 0.62),
            FileCategory.Code => Brush(180, 0.45, 0.62),
            FileCategory.Database => Brush(50, 0.70, 0.62),
            FileCategory.Log => Brush(60, 0.30, 0.70),
            FileCategory.System => Brush(220, 0.10, 0.62),
            _ => Brush(220, 0.08, 0.78),
        };
    }

    private SolidColorBrush Brush(double hue, double saturation, double lightness)
    {
        int key = ((int)hue << 16) | ((int)(saturation * 100) << 8) | (int)(lightness * 100);
        if (_brushes.TryGetValue(key, out var brush)) return brush;
        brush = new SolidColorBrush(FromHsl(hue, saturation, lightness));
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private void DrawLabel(DrawingContext dc, string text, double x, double y, double maxWidth, bool bold, Brush brush, double size, double opacity = 1)
    {
        if (maxWidth < 10) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? _boldTypeface : _typeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (opacity < 1) dc.PushOpacity(opacity);
        dc.DrawText(ft, new Point(x, y));
        if (opacity < 1) dc.Pop();
    }

    private void DrawCenteredText(DrawingContext dc, Rect bounds, string text)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, 14,
            new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x72)), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(50, bounds.Width - 40),
            TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(ft, new Point(20, Math.Max(0, (bounds.Height - ft.Height) / 2)));
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (Selected is { } selected && FindRect(selected) is { } sr)
            dc.DrawRectangle(null, _selectedPen, Deflate(sr, 1.5));

        if (_hovered is not null && FindRect(_hovered) is { } hr)
        {
            dc.DrawRectangle(null, _hoverShadow, Deflate(hr, 1));
            dc.DrawRectangle(null, _hoverPen, Deflate(hr, 1));
        }
    }

    private static Rect Deflate(Rect r, double by) =>
        new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    private Rect? FindRect(FsNode node)
    {
        foreach (var (rect, n) in _hits)
            if (ReferenceEquals(n, node)) return rect;
        return null;
    }

    // ------------------------------------------------------------------ input

    public FsNode? HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(p)) return _hits[i].Node;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = HitTest(e.GetPosition(this));
        if (ReferenceEquals(node, _hovered)) return;
        _hovered = node;
        DrawOverlay();
        HoverChanged?.Invoke(this, node);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        DrawOverlay();
        HoverChanged?.Invoke(this, null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var node = HitTest(e.GetPosition(this));
        if (node is null) return;

        if (e.ClickCount == 2)
        {
            var target = node.IsContainer ? node : node.Parent;
            if (target is not null) NodeActivated?.Invoke(this, target);
        }
        else
        {
            Selected = node;
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var node = HitTest(e.GetPosition(this));
        if (node is null) return;
        Selected = node;
        NodeContextRequested?.Invoke(this, node);
        e.Handled = true;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
