using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentReclaim.Core.Analysis;

namespace AgentReclaim.App;

/// <summary>
/// Whether to animate: Windows' "Animation effects" setting. Everything here runs once and ends, nothing loops, and
/// nothing moves while the window is hidden, so the app stays as light as it was.
/// </summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>For storyboards in XAML: quick, or instant with animation off.</summary>
    public static Duration Fast => new(TimeSpan.FromMilliseconds(Enabled ? 120 : 0));

    public static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    /// <summary>Animates a property to a value, or sets it straight away when it shouldn't animate.</summary>
    public static void To(FrameworkElement element, DependencyProperty property, double value, int milliseconds)
    {
        if (!Enabled || !element.IsVisible)
        {
            element.BeginAnimation(property, null);
            element.SetValue(property, value);
            return;
        }
        element.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease });
    }
}

/// <summary>
/// The window's tabs. The sidebar and stat cards live in this control's template, and a stock TabControl only tells screen
/// readers and UI Automation about its tabs and the selected page, so this adds what's in the parts named here.
/// </summary>
public sealed class NavTabControl : TabControl
{
    private static readonly string[] Parts = ["PartGreeting", "PartCards", "PartReclaimed", "PartStatus"];

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(NavTabControl owner) : TabControlAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore()
        {
            var children = base.GetChildrenCore() ?? [];
            foreach (var part in Parts)
            {
                if (owner.Template?.FindName(part, owner) is UIElement element) AddPeers(element, children);
            }
            return children;
        }

        private void AddPeers(DependencyObject parent, List<AutomationPeer> into)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                if (VisualTreeHelper.GetChild(parent, i) is not UIElement { IsVisible: true } child) continue;
                if (child is TextBlock text)
                {
                    if (!string.IsNullOrEmpty(text.Text)) into.Add(_text.GetValue(text, t => new TemplateText(t)));
                }
                else if (UIElementAutomationPeer.CreatePeerForElement(child) is { } peer) into.Add(peer);
                else AddPeers(child, into);
            }
        }

        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, TemplateText> _text = [];

        /// <summary>WPF's own peer hides a TextBlock that's part of a template, but this text is what the card has to say.</summary>
        private sealed class TemplateText(TextBlock text) : FrameworkElementAutomationPeer(text)
        {
            protected override string GetClassNameCore() => nameof(TextBlock);
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
            protected override string GetNameCore() => text.Text;
            protected override bool IsControlElementCore() => true;
            protected override bool IsContentElementCore() => true;
        }
    }
}

/// <summary>A sidebar tab's icon, group heading and count.</summary>
public static class Nav
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Nav), new PropertyMetadata(""));
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached("Group", typeof(string), typeof(Nav), new PropertyMetadata(""));
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached("Badge", typeof(int), typeof(Nav), new PropertyMetadata(0));
    public static readonly DependencyProperty UrgentProperty = DependencyProperty.RegisterAttached("Urgent", typeof(bool), typeof(Nav), new PropertyMetadata(false));

    public static string GetIcon(DependencyObject o) => (string)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string value) => o.SetValue(IconProperty, value);
    public static string GetGroup(DependencyObject o) => (string)o.GetValue(GroupProperty);
    public static void SetGroup(DependencyObject o, string value) => o.SetValue(GroupProperty, value);
    public static int GetBadge(DependencyObject o) => (int)o.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject o, int value) => o.SetValue(BadgeProperty, value);
    public static bool GetUrgent(DependencyObject o) => (bool)o.GetValue(UrgentProperty);
    public static void SetUrgent(DependencyObject o, bool value) => o.SetValue(UrgentProperty, value);
}

/// <summary>Shows content when a count is zero, or with ConverterParameter=Invert when it isn't.</summary>
public sealed class ZeroToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isZero = value is 0 or null;
        return isZero != (parameter as string == "Invert") ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when false.</summary>
public sealed class NotToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A ring showing how full something is, easing to each new value. The number and word sit beside or inside it.</summary>
public sealed class RingGauge : FrameworkElement
{
    private const double StartAngle = -90;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new PropertyMetadata(0.0, (d, e) => ((RingGauge)d).MoveTo(Math.Clamp((double)e.NewValue, 0, 1))));
    private static readonly DependencyProperty DisplayedProperty = Render<double>("Displayed", 0.0);
    public static readonly DependencyProperty StateProperty = Render<MeterState>(nameof(State), MeterState.Good);
    private static readonly DependencyProperty GoodProperty = Render<Brush?>("Good", null);
    private static readonly DependencyProperty WarnProperty = Render<Brush?>("Warn", null);
    private static readonly DependencyProperty DangerProperty = Render<Brush?>("Danger", null);
    private static readonly DependencyProperty TrackProperty = Render<Brush?>("Track", null);

    private static DependencyProperty Render<T>(string name, T initial) => DependencyProperty.Register(name, typeof(T), typeof(RingGauge),
        new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public MeterState State { get => (MeterState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public RingGauge()
    {
        SetResourceReference(GoodProperty, "Good");
        SetResourceReference(WarnProperty, "Warn");
        SetResourceReference(DangerProperty, "Danger");
        SetResourceReference(TrackProperty, "CardBorder");
    }

    /// <summary>Memory moves a little on every poll: only a change you could see is worth animating, the rest just lands.</summary>
    private void MoveTo(double value)
    {
        var shown = (double)GetValue(DisplayedProperty);
        if (Math.Abs(value - shown) < 0.01)
        {
            BeginAnimation(DisplayedProperty, null);
            SetValue(DisplayedProperty, value);
            return;
        }
        Motion.To(this, DisplayedProperty, value, 300);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var thickness = Math.Max(4, Math.Min(ActualWidth, ActualHeight) * 0.13);
        var radius = (Math.Min(ActualWidth, ActualHeight) - thickness) / 2;
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen((Brush?)GetValue(TrackProperty), thickness), center, radius, radius);

        var fraction = Math.Clamp((double)GetValue(DisplayedProperty), 0, 0.9999);
        if (fraction <= 0) return;
        var brush = (Brush?)GetValue(State switch { MeterState.Danger => DangerProperty, MeterState.Warn => WarnProperty, _ => GoodProperty });
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(PointOn(center, radius, StartAngle), false, false);
            g.ArcTo(PointOn(center, radius, StartAngle + 360 * fraction), new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
    }
}

/// <summary>A small line of recent values. Its tooltip (set by the card) names the range, so it's never colour or shape alone.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    /// <summary>A fixed top for the scale (100 for a percentage), or 0 to fit the line to its own values.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public Sparkline() => SetResourceReference(StrokeProperty, "Series1");

    /// <summary>So its name, the low and high in words, reaches screen readers.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // the whole area shows the tooltip
        if (Values is not { Count: >= 2 } values || GetValue(StrokeProperty) is not Brush stroke) return;

        const double inset = 2;
        var fixedTop = Maximum > 0;
        var top = fixedTop ? Maximum : values.Max();
        var bottom = fixedTop ? 0 : values.Min();
        if (top - bottom < 1e-9) { top = bottom + 1; bottom -= 1; } // a flat line sits in the middle
        var height = ActualHeight - 2 * inset;
        var stepX = (ActualWidth - 2 * inset) / (values.Count - 1);
        Point At(int i) => new(inset + i * stepX, inset + (1 - Math.Clamp((values[i] - bottom) / (top - bottom), 0, 1)) * height);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(0), false, false);
            for (var i = 1; i < values.Count; i++) g.LineTo(At(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(At(0).X, ActualHeight), true, true);
            for (var i = 0; i < values.Count; i++) g.LineTo(At(i), false, false);
            g.LineTo(new Point(At(values.Count - 1).X, ActualHeight), false, false);
        }
        area.Freeze();

        var fill = stroke.Clone();
        fill.Opacity = 0.14;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        dc.DrawEllipse(stroke, null, At(values.Count - 1), 2.5, 2.5);
    }
}

/// <summary>A thin bar under a number in a table cell.</summary>
public sealed class MiniBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(MiniBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty FillProperty = DependencyProperty.Register("Fill", typeof(Brush), typeof(MiniBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty TrackProperty = DependencyProperty.Register("Track", typeof(Brush), typeof(MiniBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public MiniBar()
    {
        SetResourceReference(FillProperty, "Series1");
        SetResourceReference(TrackProperty, "CardBorder");
        Height = 3;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = ActualHeight / 2;
        dc.DrawRoundedRectangle((Brush?)GetValue(TrackProperty), null, new Rect(0, 0, ActualWidth, ActualHeight), r, r);
        if (Value <= 0) return;
        var width = Math.Max(ActualHeight, ActualWidth * Math.Clamp(Value, 0, 1));
        dc.DrawRoundedRectangle((Brush?)GetValue(FillProperty), null, new Rect(0, 0, width, ActualHeight), r, r);
    }
}

/// <summary>A dot that gives one soft pulse each time fresh numbers arrive: it really is a heartbeat, and stops when polling does.</summary>
public sealed class HeartbeatDot : FrameworkElement
{
    public static readonly DependencyProperty BeatProperty = DependencyProperty.Register(nameof(Beat), typeof(int), typeof(HeartbeatDot),
        new PropertyMetadata(0, (d, _) => ((HeartbeatDot)d).Pulse()));
    private static readonly DependencyProperty FillProperty = DependencyProperty.Register("Fill", typeof(Brush), typeof(HeartbeatDot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Beat { get => (int)GetValue(BeatProperty); set => SetValue(BeatProperty, value); }

    public HeartbeatDot()
    {
        SetResourceReference(FillProperty, "Good");
        Width = Height = 8;
        IsHitTestVisible = false;
    }

    private void Pulse()
    {
        if (!Motion.Enabled || !IsVisible) return;
        var pulse = new DoubleAnimationUsingKeyFrames();
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)), Motion.Ease));
        BeginAnimation(OpacityProperty, pulse);
    }

    protected override void OnRender(DrawingContext dc) =>
        dc.DrawEllipse((Brush?)GetValue(FillProperty), null, new Point(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2, ActualHeight / 2);
}

/// <summary>Which agent: a coloured dot and its name, in ordinary text colour so it reads the same in both themes.</summary>
public sealed class AgentBadge : Border
{
    public static readonly DependencyProperty AgentProperty = DependencyProperty.Register(nameof(Agent), typeof(string), typeof(AgentBadge),
        new PropertyMetadata("", (d, e) => ((AgentBadge)d).Show((string)e.NewValue)));

    private readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };

    public string Agent { get => (string)GetValue(AgentProperty); set => SetValue(AgentProperty, value); }

    public AgentBadge()
    {
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(7, 1, 8, 2);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BorderBrushProperty, "CardBorder");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_name);
        Child = row;
    }

    private void Show(string agent)
    {
        _name.Text = agent;
        _dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, agent switch
        {
            "Claude Code" => "AgentClaude",
            "Codex" => "AgentCodex",
            "Gemini CLI" => "AgentGemini",
            _ => "Muted",
        });
    }
}

/// <summary>
/// The mascot: a small robot vacuum that picks up after your agents. Drawn in code on a 100×100 grid, so it stays sharp at
/// any size and needs no image files. Decorative: what its mood means is always said in words next to it.
/// </summary>
public sealed class Mascot : FrameworkElement
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(nameof(Mood), typeof(MascotMood), typeof(Mascot),
        new FrameworkPropertyMetadata(MascotMood.Content, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Visor = Frozen(Color.FromRgb(0x14, 0x1B, 0x24));
    private static readonly Brush Eye = Frozen(Colors.White);
    private static readonly Brush Bumper = Frozen(Color.FromRgb(0xF0, 0x95, 0x4E));
    private static readonly Brush BodyFill = FrozenGradient(Color.FromRgb(0x5D, 0x72, 0x89), Color.FromRgb(0x36, 0x45, 0x55));
    private static readonly Pen Rim = FrozenPen(Color.FromRgb(0x7D, 0x92, 0xA8), 1.5);
    private static readonly Brush LedGood = Frozen(Color.FromRgb(0x4C, 0xC3, 0x8A));
    private static readonly Brush LedWarn = Frozen(Color.FromRgb(0xF0, 0xB4, 0x29));

    private readonly TranslateTransform _hop = new();

    public MascotMood Mood { get => (MascotMood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }

    public Mascot()
    {
        RenderTransform = _hop;
    }

    /// <summary>One happy hop.</summary>
    public void Hop()
    {
        if (!Motion.Enabled || !IsVisible) return;
        var hop = new DoubleAnimationUsingKeyFrames();
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-ActualHeight * 0.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), Motion.Ease));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new BounceEase { Bounces = 1, Bounciness = 3 }));
        _hop.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    protected override void OnMouseEnter(MouseEventArgs e) => Hop();

    protected override void OnRender(DrawingContext dc)
    {
        var scale = Math.Min(ActualWidth, ActualHeight) / 100;
        if (scale <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hover anywhere on it
        dc.PushTransform(new TranslateTransform((ActualWidth - 100 * scale) / 2, (ActualHeight - 100 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));

        var mood = Mood;
        Line(dc, Rim.Brush, 3, 50, 22, 50, 12); // antenna
        dc.DrawEllipse(mood == MascotMood.Content || mood == MascotMood.Proud ? LedGood : mood == MascotMood.Concerned ? LedWarn : Bumper, null, new Point(50, 10), 5, 5);

        // Side brush, sweeping away at the bottom left.
        foreach (var (dx, dy) in new[] { (-9.0, 4.0), (-7.0, 9.0), (-2.0, 10.0) }) Line(dc, Rim.Brush, 2.5, 15, 82, 15 + dx, 82 + dy);

        dc.DrawEllipse(BodyFill, Rim, new Point(50, 56), 42, 36);

        // Orange bumper along the bottom edge.
        var bumper = Arc(new Point(13, 67), new Point(87, 67), 42, 36, large: false, SweepDirection.Counterclockwise);
        dc.DrawGeometry(null, new Pen(Bumper, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, bumper);

        dc.DrawRoundedRectangle(Visor, null, new Rect(21, 34, 58, 32), 15, 15);
        DrawFace(dc, mood);

        dc.Pop();
        dc.Pop();
    }

    private static void DrawFace(DrawingContext dc, MascotMood mood)
    {
        const double left = 38, right = 62, eyes = 46, mouth = 56;
        switch (mood)
        {
            case MascotMood.Proud:
                dc.DrawGeometry(null, Stroke(3.5), Arc(new Point(left - 6, eyes + 3), new Point(left + 6, eyes + 3), 6, 6, false, SweepDirection.Clockwise));
                dc.DrawGeometry(null, Stroke(3.5), Arc(new Point(right - 6, eyes + 3), new Point(right + 6, eyes + 3), 6, 6, false, SweepDirection.Clockwise));
                dc.DrawGeometry(Eye, null, Arc(new Point(41, mouth - 2), new Point(59, mouth - 2), 9, 9, false, SweepDirection.Counterclockwise, closed: true));
                break;
            case MascotMood.Spotted:
                dc.DrawEllipse(Eye, null, new Point(left, eyes), 6, 6.5);
                dc.DrawEllipse(Eye, null, new Point(right, eyes), 6, 6.5);
                dc.DrawEllipse(Visor, null, new Point(left + 1.5, eyes), 2.5, 2.5);
                dc.DrawEllipse(Visor, null, new Point(right + 1.5, eyes), 2.5, 2.5);
                dc.DrawEllipse(Eye, null, new Point(50, mouth + 1), 2.5, 2.5);
                break;
            case MascotMood.Concerned:
                dc.DrawEllipse(Eye, null, new Point(left, eyes + 1), 4.5, 5);
                dc.DrawEllipse(Eye, null, new Point(right, eyes + 1), 4.5, 5);
                Line(dc, Eye, 2.5, left - 6, eyes - 6, left + 5, eyes - 9);
                Line(dc, Eye, 2.5, right - 5, eyes - 9, right + 6, eyes - 6);
                dc.DrawGeometry(null, Stroke(3), Arc(new Point(44, mouth + 3), new Point(56, mouth + 3), 8, 8, false, SweepDirection.Clockwise));
                break;
            default:
                dc.DrawEllipse(Eye, null, new Point(left, eyes), 5, 5.5);
                dc.DrawEllipse(Eye, null, new Point(right, eyes), 5, 5.5);
                dc.DrawGeometry(null, Stroke(3), Arc(new Point(42, mouth), new Point(58, mouth), 10, 10, false, SweepDirection.Counterclockwise));
                break;
        }
    }

    private static Pen Stroke(double thickness) => new(Eye, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    private static void Line(DrawingContext dc, Brush brush, double thickness, double x1, double y1, double x2, double y2) =>
        dc.DrawLine(new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(x1, y1), new Point(x2, y2));

    private static StreamGeometry Arc(Point from, Point to, double rx, double ry, bool large, SweepDirection sweep, bool closed = false)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(from, closed, closed);
            g.ArcTo(to, new Size(rx, ry), 0, large, sweep, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush FrozenGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}

/// <summary>What a tab shows when it has nothing: the mascot, a short heading and what would appear here.</summary>
public sealed class EmptyState : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, e) => ((EmptyState)d)._title.Text = (string)e.NewValue));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, e) => ((EmptyState)d)._text.Text = (string)e.NewValue));
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(nameof(Mood), typeof(MascotMood), typeof(EmptyState),
        new PropertyMetadata(MascotMood.Content, (d, e) => ((EmptyState)d)._mascot.Mood = (MascotMood)e.NewValue));

    private readonly Mascot _mascot = new() { Width = 84, Height = 84, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460, Margin = new Thickness(0, 4, 0, 0) };

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public MascotMood Mood { get => (MascotMood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }

    public EmptyState()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Children.Add(_mascot);
        Children.Add(_title);
        Children.Add(_text);
    }
}
