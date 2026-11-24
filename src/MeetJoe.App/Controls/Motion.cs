using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace MeetJoe.App.Controls;

/// <summary>
/// The pane transition used when moving between sections: the new content fades in
/// while sliding a short distance. See "Motion" in docs/DESIGN.md.
/// </summary>
public sealed class SlideFadeTransition : IPageTransition
{
    /// <summary>Turned off by UI tests so screenshots are not taken mid-animation.</summary>
    public static bool Enabled { get; set; } = true;

    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(180);

    /// <summary>Horizontal distance travelled, in pixels. Zero gives a plain fade.</summary>
    public double Offset { get; set; } = 14;

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (from is not null) from.IsVisible = false;
        if (to is null) return;

        to.IsVisible = true;
        if (!Enabled || cancellationToken.IsCancellationRequested) return;

        var animation = new Animation
        {
            Duration = Duration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0.0),
                        new Setter(TranslateTransform.XProperty, Offset),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1.0),
                        new Setter(TranslateTransform.XProperty, 0.0),
                    },
                },
            },
        };

        await animation.RunAsync(to, cancellationToken);
    }
}
