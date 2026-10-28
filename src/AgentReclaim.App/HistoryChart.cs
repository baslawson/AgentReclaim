using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentReclaim.App;

/// <summary>One line on the history chart: a name, a theme brush key, and a value per sample (null where it had none).</summary>
public sealed record ChartSeries(string Name, string BrushKey, IReadOnlyList<long?> Values, bool IsTotal = false);

/// <summary>
/// Line chart of memory over the last 24 hours: one shared time axis and one byte axis, 2px lines, a
/// recessive grid, and a crosshair with a tooltip listing every series at the hovered moment.
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    private const double Left = 56, Right = 12, Top = 10, Bottom = 24;
    private IReadOnlyList<DateTime> _times = [];
    private IReadOnlyList<ChartSeries> _series = [];
    private Point? _hover;

    public static readonly DependencyProperty InkProperty = Brush(nameof(Ink));
    public static readonly DependencyProperty MutedProperty = Brush(nameof(Muted));
    public static readonly DependencyProperty GridLinesProperty = Brush(nameof(GridLines));
    public static readonly DependencyProperty SurfaceProperty = Brush(nameof(Surface));

    public System.Windows.Media.Brush? Ink { get => (System.Windows.Media.Brush?)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public System.Windows.Media.Brush? Muted { get => (System.Windows.Media.Brush?)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    public System.Windows.Media.Brush? GridLines { get => (System.Windows.Media.Brush?)GetValue(GridLinesProperty); set => SetValue(GridLinesProperty, value); }
    public System.Windows.Media.Brush? Surface { get => (System.Windows.Media.Brush?)GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }

    public HistoryChart()
    {
        SetResourceReference(InkProperty, "ChartInk");
        SetResourceReference(MutedProperty, "Muted");
        SetResourceReference(GridLinesProperty, "ChartGrid");
        SetResourceReference(SurfaceProperty, "Card");
        // Series brushes are looked up while drawing; a theme switch changes these four, which redraws.
        MinHeight = 180;
        ClipToBounds = true;
    }

    public void SetData(IReadOnlyList<DateTime> localTimes, IReadOnlyList<ChartSeries> series)
    {
        _times = localTimes;
        _series = series;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        // Transparent fill so the whole area takes mouse input.
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h));
        var plot = new Rect(Left, Top, Math.Max(10, w - Left - Right), Math.Max(10, h - Top - Bottom));
        var muted = Muted ?? System.Windows.Media.Brushes.Gray;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_times.Count < 2)
        {
            Text(dc, "Collecting: one point a minute. The chart fills in as AgentReclaim runs.", muted, plot.Left, plot.Top + plot.Height / 2 - 8, dpi);
            return;
        }

        var end = DateTime.Now;
        var start = end - Core.Analysis.MemoryHistory.Window;
        var max = Math.Max(1, _series.SelectMany(s => s.Values).Max(v => v ?? 0));
        var step = NiceStep(max / 3.0);
        var top = Math.Ceiling(max / step) * step;

        double X(DateTime t) => plot.Left + (t - start).TotalSeconds / (end - start).TotalSeconds * plot.Width;
        double Y(double v) => plot.Bottom - v / top * plot.Height;

        // Grid and axes: recessive, labels in muted ink.
        var gridPen = new Pen(GridLines ?? muted, 1);
        for (var v = 0.0; v <= top + 1; v += step)
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Text(dc, Format.Bytes((long)v), muted, 4, y - 8, dpi);
        }
        for (var hoursAgo = 24; hoursAgo >= 0; hoursAgo -= 6)
        {
            var t = end.AddHours(-hoursAgo);
            var label = hoursAgo == 0 ? "now" : t.ToString("t", CultureInfo.CurrentCulture);
            var x = X(t);
            var ft = Formatted(label, muted, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, plot.Left, w - ft.Width - 2), plot.Bottom + 4));
        }

        // Lines; a gap of more than five minutes (app not running) breaks the line.
        foreach (var s in _series.Reverse())
        {
            var pen = new Pen(BrushFor(s), s.IsTotal ? 2.5 : 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var dots = new List<Point>();
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var drawing = false;
                DateTime? previous = null;
                var run = 0;
                Point first = default;
                for (var i = 0; i < _times.Count; i++)
                {
                    if (s.Values[i] is not { } v || _times[i] < start)
                    {
                        drawing = false;
                        continue;
                    }
                    var p = new Point(X(_times[i]), Y(v));
                    if (!drawing || previous is null || _times[i] - previous > TimeSpan.FromMinutes(5))
                    {
                        if (run == 1) dots.Add(first);
                        g.BeginFigure(p, false, false);
                        first = p;
                        run = 1;
                    }
                    else
                    {
                        g.LineTo(p, true, true);
                        run++;
                    }
                    drawing = true;
                    previous = _times[i];
                }
                if (run == 1) dots.Add(first);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            // A run of one sample has no length to draw: show it as a dot.
            foreach (var dot in dots) dc.DrawEllipse(pen.Brush, null, dot, 3, 3);
        }

        if (_hover is { } hover && plot.Contains(hover)) DrawHover(dc, plot, hover, X, Y, dpi);
    }

    private void DrawHover(DrawingContext dc, Rect plot, Point hover, Func<DateTime, double> x, Func<double, double> y, double dpi)
    {
        var index = 0;
        for (var i = 1; i < _times.Count; i++)
            if (Math.Abs(x(_times[i]) - hover.X) < Math.Abs(x(_times[index]) - hover.X)) index = i;
        var cx = x(_times[index]);
        var ink = Ink ?? System.Windows.Media.Brushes.Black;
        dc.DrawLine(new Pen(Muted ?? ink, 1), new Point(cx, plot.Top), new Point(cx, plot.Bottom));

        var lines = new List<(string Text, System.Windows.Media.Brush Swatch)> { (_times[index].ToString("t", CultureInfo.CurrentCulture), System.Windows.Media.Brushes.Transparent) };
        foreach (var s in _series)
        {
            if (s.Values[index] is not { } v) continue;
            var brush = BrushFor(s);
            dc.DrawEllipse(Surface, new Pen(brush, 2), new Point(cx, y(v)), 4, 4);
            lines.Add(($"{s.Name}: {Format.Bytes(v)}", brush));
        }

        var texts = lines.Select(l => Formatted(l.Text, ink, dpi)).ToList();
        var boxW = texts.Max(t => t.Width) + 28;
        var boxH = texts.Sum(t => t.Height + 2) + 10;
        var bx = cx + 12 + boxW > plot.Right ? cx - 12 - boxW : cx + 12;
        var box = new Rect(Math.Max(0, bx), plot.Top + 4, boxW, boxH);
        dc.DrawRoundedRectangle(Surface, new Pen(GridLines ?? Muted, 1), box, 4, 4);
        var ty = box.Top + 5;
        for (var i = 0; i < texts.Count; i++)
        {
            if (i > 0) dc.DrawRoundedRectangle(lines[i].Swatch, null, new Rect(box.Left + 8, ty + texts[i].Height / 2 - 4, 10, 8), 2, 2);
            dc.DrawText(texts[i], new Point(box.Left + (i == 0 ? 8 : 22), ty));
            ty += texts[i].Height + 2;
        }
    }

    private System.Windows.Media.Brush BrushFor(ChartSeries s) =>
        TryFindResource(s.BrushKey) as System.Windows.Media.Brush ?? Ink ?? System.Windows.Media.Brushes.Gray;

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        var magnitude = Math.Pow(1024, Math.Floor(Math.Log(raw, 1024)));
        foreach (var m in new[] { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 })
            if (m * magnitude >= raw) return m * magnitude;
        return 1024 * magnitude;
    }

    private void Text(DrawingContext dc, string text, System.Windows.Media.Brush brush, double x, double y, double dpi) =>
        dc.DrawText(Formatted(text, brush, dpi), new Point(x, y));

    private FormattedText Formatted(string text, System.Windows.Media.Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            11, brush, dpi);

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(System.Windows.Media.Brush), typeof(HistoryChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
