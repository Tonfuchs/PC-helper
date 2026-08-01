using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace PCHelper.Controls;

/// <summary>
/// Schlanke Verlaufsgrafik ohne externe Bibliothek: zeichnet eine Zahlenreihe
/// als Linie mit weicher Flaeche darunter.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (Sparkline)d;

        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= control.OnCollectionChanged;

        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += control.OnCollectionChanged;

        control.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Die Messreihe wird aus einem Hintergrundthread befuellt.
        Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values?.ToArray();
        if (values is null || values.Length < 2) return;

        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        double min = Minimum, max = Maximum;
        if (max <= min)
        {
            min = values.Min();
            max = values.Max();
            if (Math.Abs(max - min) < 0.001) { max = min + 1; }
        }

        double stepX = w / (values.Length - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();

        using (var lineCtx = line.Open())
        using (var areaCtx = area.Open())
        {
            var points = new Point[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                double norm = Math.Clamp((values[i] - min) / (max - min), 0, 1);
                points[i] = new Point(i * stepX, h - norm * (h - 2) - 1);
            }

            lineCtx.BeginFigure(points[0], isFilled: false, isClosed: false);
            lineCtx.PolyLineTo(points[1..], isStroked: true, isSmoothJoin: true);

            areaCtx.BeginFigure(new Point(0, h), isFilled: true, isClosed: true);
            areaCtx.PolyLineTo(points, isStroked: false, isSmoothJoin: false);
            areaCtx.LineTo(new Point(w, h), isStroked: false, isSmoothJoin: false);
        }

        line.Freeze();
        area.Freeze();

        if (Stroke is SolidColorBrush solid)
        {
            var c = solid.Color;
            var fill = new LinearGradientBrush(
                Color.FromArgb(70, c.R, c.G, c.B),
                Color.FromArgb(0, c.R, c.G, c.B),
                new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
