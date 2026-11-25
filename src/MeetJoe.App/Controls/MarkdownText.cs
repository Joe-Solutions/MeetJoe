using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MeetJoe.App.Controls;

/// <summary>Renders a small markdown subset: **bold**, *italic*, `code`, and fenced code blocks.</summary>
public sealed partial class MarkdownText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

    /// <summary>
    /// Use Fluent's SelectableTextBlock theme (selection brush, I-beam, Copy context flyout).
    /// Without this, Avalonia styles the control as MarkdownText and selection/copy feel broken.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static MarkdownText()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownText>((control, _) => control.Rebuild());
    }

    private void Rebuild()
    {
        Inlines?.Clear();
        var source = Markdown ?? string.Empty;
        if (source.Length == 0) return;

        Inlines ??= [];
        var parts = FencePattern().Split(source);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            if (i % 2 == 1)
            {
                Inlines.Add(new Run(part)
                {
                    FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                    FontSize = FontSize - 1,
                });
                continue;
            }

            AppendInlineMarkdown(part);
        }
    }

    private void AppendInlineMarkdown(string text)
    {
        var matches = InlinePattern().Matches(text);
        var index = 0;
        foreach (Match match in matches)
        {
            if (match.Index > index)
                Inlines!.Add(new Run(text[index..match.Index]));

            if (match.Groups["bold"].Success)
                Inlines!.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeight.Bold });
            else if (match.Groups["italic"].Success)
                Inlines!.Add(new Run(match.Groups["italic"].Value) { FontStyle = FontStyle.Italic });
            else if (match.Groups["code"].Success)
                Inlines!.Add(new Run(match.Groups["code"].Value)
                {
                    FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                });

            index = match.Index + match.Length;
        }

        if (index < text.Length) Inlines!.Add(new Run(text[index..]));
    }

    [GeneratedRegex(@"```(?:\w+\n)?(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|\*(?<italic>.+?)\*|`(?<code>[^`]+)`")]
    private static partial Regex InlinePattern();
}
