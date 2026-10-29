using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// What the Spend view's charts share: the parts' colours from the theme, its quiet ink for labels, and text drawn at the
/// screen's DPI. The charts draw themselves (OnRender) and keep no timer: a Studio column, not the layered overlay.
/// </summary>
internal static class SpendInk
{
    public static Brush Part(FrameworkElement element, SpendPart part) => Brush(element, $"Spend{part}Brush");

    public static Brush Brush(FrameworkElement element, string key) => element.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static FormattedText Text(FrameworkElement element, string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(element).PixelsPerDip);

    /// <summary>A share of tokens as the charts write it: "84k", "1.2M".</summary>
    public static string Thousands(double thousands) => AgentContext.Tokens((long)Math.Round(thousands * 1000));

    /// <summary>The top of an axis: the next round number above <paramref name="value"/>, so that its halves read well.</summary>
    public static double NiceTop(double value)
    {
        if (value <= 0) return 1;
        var step = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var times in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
        {
            if (step * times >= value) return step * times;
        }

        return step * 10;
    }

    /// <summary>The line of a part for a tooltip: its name, its share and its ≈ tokens, then what it holds.</summary>
    public static string PartLine(SpendParts parts, SpendPart part) =>
        $"{SpendReport.Name(part)} · {Math.Round(100.0 * parts[part] / Math.Max(1, parts.Total))}% · ~{AgentContext.Tokens(parts[part])}\n{SpendReport.Hint(part)}";
}

/// <summary>
/// Where tokens went, as one rounded bar of the parts side by side, a gap of the surface between them. The pointer on a
/// part lights it and tells its share; <see cref="Highlight"/> lights one from outside (the legend), and
/// <see cref="Scale"/> draws the bar shorter, so that rows of tasks or runs compare.
/// </summary>
public sealed class SpendBar : FrameworkElement
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(nameof(Parts), typeof(SpendParts), typeof(SpendBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(nameof(Scale), typeof(double), typeof(SpendBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(SpendPart?), typeof(SpendBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(SpendBar),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private SpendPart? _hovered;
    private ToolTip? _tip;

    public SpendBar()
    {
        SnapsToDevicePixels = true;
        ToolTipService.SetInitialShowDelay(this, 120);
    }

    public SpendParts? Parts
    {
        get => (SpendParts?)GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    /// <summary>The bar's length as a share of the width, 0 to 1.</summary>
    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public SpendPart? Highlight
    {
        get => (SpendPart?)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var height = ActualHeight;
        var radius = Math.Min(height / 2, 4);
        drawing.DrawRoundedRectangle(SpendInk.Brush(this, "HairlineBrush"), null, new Rect(0, 0, ActualWidth, height), radius, radius);
        if (Parts is not { Total: > 0 }) return;
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, Length, height), radius, radius));
        var lit = _hovered ?? Highlight;
        foreach (var (part, left, width) in Segments())
        {
            var dim = lit is not null && lit != part;
            if (dim) drawing.PushOpacity(0.32);
            drawing.DrawRectangle(SpendInk.Part(this, part), null, new Rect(left, 0, Math.Max(width, 1), height));
            if (dim) drawing.Pop();
        }

        drawing.Pop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Parts is not { Total: > 0 } parts) return;
        var x = e.GetPosition(this).X;
        SpendPart? under = Segments().Where(segment => x >= segment.Left - Gap / 2 && x <= segment.Left + segment.Width + Gap / 2)
            .Select(segment => (SpendPart?)segment.Part).FirstOrDefault();
        if (under == _hovered && _tip is not null) return;
        _hovered = under;
        _tip ??= new ToolTip();
        ToolTip = _tip;
        _tip.Content = under is { } part ? SpendInk.PartLine(parts, part) : $"~{AgentContext.Tokens(parts.Total)} ≈ tokens";
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered is null) return;
        _hovered = null;
        InvalidateVisual();
    }

    private double Length => ActualWidth * Math.Clamp(Scale, 0, 1);

    // The parts as drawn, in their fixed order; a bar too short for the gaps draws none.
    private IEnumerable<(SpendPart Part, double Left, double Width)> Segments()
    {
        if (Parts is not { Total: > 0 } parts) yield break;
        var shown = SpendParts.All.Where(part => parts[part] > 0).ToArray();
        var gap = Length - Gap * (shown.Length - 1) >= shown.Length * 2 ? Gap : 0;
        var room = Length - gap * (shown.Length - 1);
        var left = 0.0;
        foreach (var part in shown)
        {
            var width = room * parts[part] / parts.Total;
            yield return (part, left, width);
            left += width + gap;
        }
    }
}

/// <summary>
/// A run's context request by request, in thousands of tokens: an area under a line in the accent, the budget as a dashed
/// line, and under the pointer a crosshair with the request's size.
/// </summary>
public sealed class SpendCurve : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<int>), typeof(SpendCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BudgetProperty = DependencyProperty.Register(nameof(Budget), typeof(int?), typeof(SpendCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double AxisWidth = 34;
    private const double AxisHeight = 16;
    private int? _hovered;

    public IReadOnlyList<int>? Points
    {
        get => (IReadOnlyList<int>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>The context budget in thousands of tokens; null draws none.</summary>
    public int? Budget
    {
        get => (int?)GetValue(BudgetProperty);
        set => SetValue(BudgetProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        // The whole box takes the pointer, not only the line.
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Points is not { Count: > 0 } points || ActualWidth <= AxisWidth + 10) return;
        var plot = Plot;
        var top = SpendInk.NiceTop(Math.Max(points.Max(), Budget ?? 0) * 1.05);
        var muted = SpendInk.Brush(this, "MutedBrush");
        var hairline = new Pen(SpendInk.Brush(this, "HairlineBrush"), 1);
        foreach (var share in new[] { 0, 0.5, 1 })
        {
            var y = Math.Round(plot.Bottom - plot.Height * share) + 0.5;
            drawing.DrawLine(hairline, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = SpendInk.Text(this, share == 0 ? "0" : SpendInk.Thousands(top * share), 10.5, muted);
            drawing.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }

        Point At(int index) => new(X(index, points.Count), plot.Bottom - plot.Height * points[index] / top);
        var accent = SpendInk.Brush(this, "AccentBrush");
        var area = new StreamGeometry();
        using (var context = area.Open())
        {
            context.BeginFigure(new Point(X(0, points.Count), plot.Bottom), true, true);
            for (var index = 0; index < points.Count; index++) context.LineTo(At(index), true, true);
            context.LineTo(new Point(X(points.Count - 1, points.Count), plot.Bottom), true, true);
        }

        var color = accent is SolidColorBrush solid ? solid.Color : Colors.Orange;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0x08, color.R, color.G, color.B), 90);
        drawing.DrawGeometry(fill, null, area);
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(At(0), false, false);
            for (var index = 1; index < points.Count; index++) context.LineTo(At(index), true, true);
        }

        drawing.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, line);

        if (Budget is { } budget)
        {
            var y = Math.Round(plot.Bottom - plot.Height * budget / top) + 0.5;
            drawing.DrawLine(new Pen(muted, 1) { DashStyle = new DashStyle([4, 3], 0) }, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = SpendInk.Text(this, "budget " + SpendInk.Thousands(budget), 10.5, muted);
            drawing.DrawText(label, new Point(plot.Right - label.Width, y - label.Height - 1));
        }

        var first = SpendInk.Text(this, "1", 10.5, muted);
        drawing.DrawText(first, new Point(plot.Left, plot.Bottom + 3));
        if (points.Count > 1)
        {
            var last = SpendInk.Text(this, $"request {points.Count}", 10.5, muted);
            drawing.DrawText(last, new Point(plot.Right - last.Width, plot.Bottom + 3));
        }

        if (_hovered is not { } hovered || hovered >= points.Count) return;
        var at = At(hovered);
        drawing.DrawLine(new Pen(muted, 1), new Point(Math.Round(at.X) + 0.5, plot.Top), new Point(Math.Round(at.X) + 0.5, plot.Bottom));
        drawing.DrawEllipse(accent, new Pen(SpendInk.Brush(this, "WindowSurfaceBrush"), 2), at, 4.5, 4.5);
        var readout = SpendInk.Text(this, $"Request {hovered + 1} · {SpendInk.Thousands(points[hovered])}", 11, SpendInk.Brush(this, "TextBrush"), bold: true);
        var box = new Rect(Math.Clamp(at.X - readout.Width / 2 - 7, plot.Left, plot.Right - readout.Width - 14), plot.Top, readout.Width + 14, readout.Height + 6);
        if (at.Y < box.Bottom + 6) box.Y = Math.Min(at.Y + 10, plot.Bottom - box.Height);
        drawing.DrawRoundedRectangle(SpendInk.Brush(this, "PopupSurfaceBrush"), new Pen(SpendInk.Brush(this, "GlassBorderBrush"), 1), box, 5, 5);
        drawing.DrawText(readout, new Point(box.X + 7, box.Y + 3));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Points is not { Count: > 0 } points) return;
        var plot = Plot;
        var share = points.Count == 1 ? 0 : Math.Clamp((e.GetPosition(this).X - plot.Left) / plot.Width, 0, 1);
        var index = (int)Math.Round(share * (points.Count - 1));
        if (index == _hovered) return;
        _hovered = index;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        InvalidateVisual();
    }

    private Rect Plot => new(AxisWidth, 4, Math.Max(1, ActualWidth - AxisWidth - 2), Math.Max(1, ActualHeight - AxisHeight - 4));

    private double X(int index, int count) => count == 1 ? Plot.Left + Plot.Width / 2 : Plot.Left + Plot.Width * index / (count - 1);
}

/// <summary>
/// The days of the history as columns, each stacked by where its tokens went, round at the top and square on the baseline.
/// The pointer lights a day and tells it with <see cref="DayHovered"/>; null when it leaves.
/// </summary>
public sealed class SpendHistory : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(nameof(Days), typeof(IReadOnlyList<SpendDay>), typeof(SpendHistory),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double AxisWidth = 34;
    private const double AxisHeight = 16;
    private int? _hovered;

    public event Action<SpendDay?>? DayHovered;

    public IReadOnlyList<SpendDay>? Days
    {
        get => (IReadOnlyList<SpendDay>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Days is not { Count: > 0 } days || ActualWidth <= AxisWidth + 10) return;
        var plot = Plot;
        var top = SpendInk.NiceTop(days.Max(day => day.Parts.Total) / 1000.0);
        var muted = SpendInk.Brush(this, "MutedBrush");
        var hairline = new Pen(SpendInk.Brush(this, "HairlineBrush"), 1);
        foreach (var share in new[] { 0, 0.5, 1 })
        {
            var y = Math.Round(plot.Bottom - plot.Height * share) + 0.5;
            drawing.DrawLine(hairline, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = SpendInk.Text(this, share == 0 ? "0" : SpendInk.Thousands(top * share), 10.5, muted);
            drawing.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }

        var column = plot.Width / days.Count;
        var gap = column >= 6 ? 2 : column >= 3 ? 1 : 0;
        var width = Math.Max(1, column - gap);
        var radius = Math.Min(3, width / 2);
        drawing.PushClip(new RectangleGeometry(plot));
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            if (day.Parts.Total <= 0) continue;
            var x = plot.Left + column * index + gap / 2.0;
            var height = plot.Height * day.Parts.Total / 1000.0 / top;
            var dim = _hovered is { } lit && lit != index;
            if (dim) drawing.PushOpacity(0.35);
            // Round at the top only: the clip's lower corners fall below the baseline, which the plot's clip cuts.
            drawing.PushClip(new RectangleGeometry(new Rect(x, plot.Bottom - height, width, height + radius), radius, radius));
            var bottom = plot.Bottom;
            foreach (var part in SpendParts.All)
            {
                if (day.Parts[part] <= 0) continue;
                var piece = height * day.Parts[part] / day.Parts.Total;
                // A hairline of the surface between the parts, where the piece is tall enough to keep one.
                var seam = piece > 3 && bottom < plot.Bottom ? 1 : 0;
                drawing.DrawRectangle(SpendInk.Part(this, part), null, new Rect(x, bottom - piece, width, Math.Max(0.5, piece - seam)));
                bottom -= piece;
            }

            drawing.Pop();
            if (dim) drawing.Pop();
        }

        drawing.Pop();
        Label(drawing, days[0].Day, plot.Left, muted, alignRight: false);
        Label(drawing, days[^1].Day, plot.Right, muted, alignRight: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Days is not { Count: > 0 } days) return;
        var plot = Plot;
        var index = (int)Math.Clamp(Math.Floor((e.GetPosition(this).X - plot.Left) / (plot.Width / days.Count)), 0, days.Count - 1);
        if (index == _hovered) return;
        _hovered = index;
        InvalidateVisual();
        DayHovered?.Invoke(days[index]);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        InvalidateVisual();
        DayHovered?.Invoke(null);
    }

    private void Label(DrawingContext drawing, DateOnly day, double x, Brush brush, bool alignRight)
    {
        var text = day == DateOnly.FromDateTime(DateTime.Today) ? "today" : day.ToString("MMM d", CultureInfo.InvariantCulture);
        var label = SpendInk.Text(this, text, 10.5, brush);
        drawing.DrawText(label, new Point(alignRight ? x - label.Width : x, Plot.Bottom + 3));
    }

    private Rect Plot => new(AxisWidth, 4, Math.Max(1, ActualWidth - AxisWidth - 2), Math.Max(1, ActualHeight - AxisHeight - 4));
}

/// <summary>
/// A usage window as a thin bar: what is used in a quiet gray (the error colour when it will not last), and of it, at its
/// end, this task's part in the accent.
/// </summary>
public sealed class SpendGaugeBar : FrameworkElement
{
    public static readonly DependencyProperty GaugeProperty = DependencyProperty.Register(nameof(Gauge), typeof(SpendGauge), typeof(SpendGaugeBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SpendGaugeBar() => SnapsToDevicePixels = true;

    public SpendGauge? Gauge
    {
        get => (SpendGauge?)GetValue(GaugeProperty);
        set => SetValue(GaugeProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var height = ActualHeight;
        var radius = height / 2;
        var whole = new Rect(0, 0, ActualWidth, height);
        drawing.DrawRoundedRectangle(SpendInk.Brush(this, "HairlineBrush"), null, whole, radius, radius);
        if (Gauge is not { } gauge || gauge.Used <= 0) return;
        drawing.PushClip(new RectangleGeometry(whole, radius, radius));
        var used = ActualWidth * gauge.Used / 100;
        var mine = ActualWidth * gauge.Mine / 100;
        var others = Math.Max(0, used - mine - (mine > 0 ? 2 : 0));
        drawing.DrawRectangle(SpendInk.Brush(this, gauge.Warn ? "ErrorBrush" : "IdleOrbBrush"), null, new Rect(0, 0, others, height));
        if (mine > 0) drawing.DrawRectangle(SpendInk.Brush(this, "AccentBrush"), null, new Rect(used - mine, 0, Math.Max(mine, 2), height));
        drawing.Pop();
    }
}
