using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeetJoe.Contracts;

namespace MeetJoe.App.Controls;

/// <summary>Profile photo (when set) or initials in a circle, with an optional presence dot.</summary>
public sealed class Avatar : Control
{
    public static readonly StyledProperty<string?> DisplayNameProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(DisplayName));

    public static readonly StyledProperty<IImage?> PhotoProperty =
        AvaloniaProperty.Register<Avatar, IImage?>(nameof(Photo));

    public static readonly StyledProperty<PresenceStatus> StatusProperty =
        AvaloniaProperty.Register<Avatar, PresenceStatus>(nameof(Status));

    public static readonly StyledProperty<bool> ShowStatusProperty =
        AvaloniaProperty.Register<Avatar, bool>(nameof(ShowStatus));

    /// <summary>Colour of the surface behind the avatar, used to cut the presence dot out of the circle.</summary>
    public static readonly StyledProperty<IBrush?> SurfaceProperty =
        AvaloniaProperty.Register<Avatar, IBrush?>(nameof(Surface));

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
        AffectsRender<Avatar>(DisplayNameProperty, PhotoProperty, StatusProperty, ShowStatusProperty, SurfaceProperty);
        WidthProperty.OverrideDefaultValue<Avatar>(36);
        HeightProperty.OverrideDefaultValue<Avatar>(36);
    }

    public string? DisplayName
    {
        get => GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public IImage? Photo
    {
        get => GetValue(PhotoProperty);
        set => SetValue(PhotoProperty, value);
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
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            name ?? string.Empty, @"\([^)]*\)", " ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var parts = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Any(char.IsLetterOrDigit))
            .ToArray();
        return parts.Length switch
        {
            0 => "?",
            1 => First(parts[0]),
            _ => First(parts[0]) + First(parts[^1]),
        };

        static string First(string part)
        {
            var i = 0;
            while (i < part.Length && !char.IsLetterOrDigit(part, i))
                i += char.IsSurrogatePair(part, i) ? 2 : 1;
            if (i >= part.Length) return "?";
            return StringInfo.GetNextTextElement(part, i).ToUpperInvariant();
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var name = DisplayName ?? string.Empty;
        var radius = size / 2;
        var center = new Point(radius, radius);
        if (Photo is { } photo)
        {
            using (context.PushGeometryClip(new EllipseGeometry(new Rect(0, 0, size, size))))
            {
                var src = photo.Size;
                if (src.Width > 0 && src.Height > 0)
                {
                    var scale = Math.Max(size / src.Width, size / src.Height);
                    var w = src.Width * scale;
                    var h = src.Height * scale;
                    var dest = new Rect((size - w) / 2, (size - h) / 2, w, h);
                    context.DrawImage(photo, dest);
                }
            }
        }
        else
        {
            context.DrawEllipse(Fills[StableIndex(name, Fills.Length)], null, center, radius, radius);
            var text = new FormattedText(
                InitialsOf(name),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.SemiBold),
                size * 0.38,
                InitialsBrush);
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }

        if (!ShowStatus) return;

        var dot = Math.Max(4, size * 0.15);
        var ring = dot + 2;
        var dotCenter = new Point(size - dot, size - dot);
        context.DrawEllipse(Surface ?? DefaultSurface, null, dotCenter, ring, ring);

        if (Status == PresenceStatus.Offline)
        {
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

    private static int StableIndex(string name, int count)
    {
        var hash = 17u;
        foreach (var ch in name) hash = hash * 31 + ch;
        return (int)(hash % (uint)count);
    }
}
