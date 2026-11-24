using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MeetJoe.App.Controls;

/// <summary>
/// A line icon drawn on a 24-unit grid in the inherited text colour, so it follows
/// whatever foreground its button or label has.
/// </summary>
public sealed class Icon : Control
{
    private const double Grid = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 20);

    public static readonly StyledProperty<double> StrokeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Stroke), 1.7);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Stroke width in grid units.</summary>
    public double Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || TextElement.GetForeground(this) is not { } brush) return;

        var pen = new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(Size / Grid, Size / Grid)))
            context.DrawGeometry(null, pen, data);
    }
}
