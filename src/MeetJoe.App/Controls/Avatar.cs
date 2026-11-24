using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MeetJoe.Contracts;

namespace MeetJoe.App.Controls;

/// <summary>A person's initials in a circle, with an optional presence dot.</summary>
public sealed class Avatar : Control
{
    public static readonly StyledProperty<string?> DisplayNameProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(DisplayName));

    public static readonly StyledProperty<PresenceStatus> StatusProperty =
        AvaloniaProperty.Register<Avatar, PresenceStatus>(nameof(Status));

    public static readonly StyledProperty<bool> ShowStatusProperty =
        AvaloniaProperty.Register<Avatar, bool>(nameof(ShowStatus));

    /// <summary>Colour of the surface behind the avatar, used to cut the presence dot out of the circle.</summary>
    public static readonly StyledProperty<IBrush?> SurfaceProperty =
        AvaloniaProperty.Register<Avatar, IBrush?>(nameof(Surface));

    // Muted tones that sit on the navy surfaces without competing with the teal accent.
    private static readonly IBrush[] Fills =
    [
        new SolidColorBrush(Color.Parse("#2B5F8A")).ToImmutable(),
        new SolidColorBrush(Color.Parse("#1F7A7A")).ToImmutable(),
        new SolidColorBrush(Color.Parse("#5A5FA8")).ToImmutable(),
        new SolidColorBrush(Color.Parse("#8A5A7B")).ToImmutable(),
        new SolidColorBrush(Color.Parse("#9A6B3F")).ToImmutable(),
        new SolidColorBrush(Color.Parse("#3F7F5F")).ToImmutable(),
    ];

    private static readonly IBrush InitialsBrush = new SolidColorBrush(Color.Parse("#F2F8FA")).ToImmutable();
    private static readonly IBrush DefaultSurface = new SolidColorBrush(Color.Parse("#122A40")).ToImmutable();
    private static readonly IBrush AvailableBrush = new SolidColorBrush(Color.Parse("#3DD68C")).ToImmutable();
    private static readonly IBrush AwayBrush = new SolidColorBrush(Color.Parse("#F2B94B")).ToImmutable();
    private static readonly IBrush BusyBrush = new SolidColorBrush(Color.Parse("#E85D4C")).ToImmutable();
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#5B7286")).ToImmutable();

    static Avatar()
    {
        AffectsRender<Avatar>(DisplayNameProperty, StatusProperty, ShowStatusProperty, SurfaceProperty);
        WidthProperty.OverrideDefaultValue<Avatar>(36);
        HeightProperty.OverrideDefaultValue<Avatar>(36);
    }

    public string? DisplayName
    {
        get => GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public PresenceStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public bool ShowStatus
    {
        get => GetValue(ShowStatusProperty);
        set => SetValue(ShowStatusProperty, value);
    }

    public IBrush? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    internal static string InitialsOf(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => First(parts[0]),
            _ => First(parts[0]) + First(parts[^1]),
        };

        static string First(string part) => StringInfo.GetNextTextElement(part).ToUpperInvariant();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var name = DisplayName ?? string.Empty;
        var radius = size / 2;
        var center = new Point(radius, radius);
        context.DrawEllipse(Fills[StableIndex(name, Fills.Length)], null, center, radius, radius);

        var text = new FormattedText(
            InitialsOf(name),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.SemiBold),
            size * 0.38,
            InitialsBrush);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));

        if (!ShowStatus) return;

        var dot = Math.Max(4, size * 0.15);
        var ring = dot + 2;
        var dotCenter = new Point(size - dot, size - dot);
        context.DrawEllipse(Surface ?? DefaultSurface, null, dotCenter, ring, ring);

        if (Status == PresenceStatus.Offline)
        {
            // Hollow, so "offline" reads without relying on colour alone.
            context.DrawEllipse(null, new Pen(OfflineBrush, 1.6), dotCenter, dot - 0.8, dot - 0.8);
            return;
        }

        var brush = Status switch
        {
            PresenceStatus.Available => AvailableBrush,
            PresenceStatus.Away => AwayBrush,
            _ => BusyBrush,
        };
        context.DrawEllipse(brush, null, dotCenter, dot, dot);

        if (Status == PresenceStatus.DoNotDisturb)
        {
            var bar = new Pen(Surface ?? DefaultSurface, Math.Max(1.4, dot * 0.4), lineCap: PenLineCap.Round);
            context.DrawLine(bar, dotCenter - new Point(dot * 0.5, 0), dotCenter + new Point(dot * 0.5, 0));
        }
    }

    /// <summary>The same name always gets the same colour, on every device.</summary>
    private static int StableIndex(string name, int count)
    {
        var hash = 17u;
        foreach (var ch in name) hash = hash * 31 + ch;
        return (int)(hash % (uint)count);
    }
}
