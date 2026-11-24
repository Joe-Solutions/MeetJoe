using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace MeetJoe.App.Services;

/// <summary>File pickers, clipboard, dialogs, and URI launch through Avalonia / the OS.</summary>
public sealed class AvaloniaPlatformServices : IPlatformServices
{
    /// <summary>Set by the app once its window or root view exists.</summary>
    public Func<TopLevel?> TopLevelAccessor { get; set; } = () => null;

    public async Task<PickedFile?> PickFileAsync()
    {
        if (TopLevelAccessor() is not { } topLevel) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Attach a file", AllowMultiple = false });
        if (files is not [var file, ..]) return null;

        // Prefer the on-disk name so Photos / temporary picks keep a useful extension.
        var name = file.TryGetLocalPath() is { } path ? Path.GetFileName(path) : file.Name;
        return new PickedFile(name, file.OpenReadAsync);
    }

    public async Task<Stream?> OpenSaveStreamAsync(string suggestedName)
    {
        if (TopLevelAccessor() is not { } topLevel) return null;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions { Title = "Save file", SuggestedFileName = suggestedName });
        return file is null ? null : await file.OpenWriteAsync();
    }

    public async Task CopyTextAsync(string text)
    {
        if (TopLevelAccessor()?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public Task OpenUriAsync(Uri uri)
    {
        // Prefer Avalonia's launcher when a window exists; fall back to Process for headless tests.
        if (TopLevelAccessor() is { Launcher: { } launcher })
            return launcher.LaunchUriAsync(uri);

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public async Task<bool> ConfirmAsync(
        string title, string message, string confirmLabel = "Confirm", string cancelLabel = "Cancel")
    {
        if (TopLevelAccessor() is not Window owner) return false;

        var confirmed = false;
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            MinHeight = 180,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SystemDecorations = SystemDecorations.BorderOnly,
            Background = Brush.Parse("#122A40"),
            Foreground = Brush.Parse("#E8F1F5"),
        };

        var confirm = new Button
        {
            Content = confirmLabel,
            Classes = { "danger" },
            MinWidth = 100,
        };
        var cancel = new Button
        {
            Content = cancelLabel,
            Classes = { "secondary" },
            MinWidth = 100,
        };
        confirm.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 18,
                        FontWeight = FontWeight.Bold,
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.85,
                        LineHeight = 21,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { cancel, confirm },
                    },
                },
            },
        };

        await dialog.ShowDialog(owner);
        return confirmed;
    }

    public Task ShowImageAsync(Bitmap image) =>
        ShowPreviewWindowAsync("Image", BuildImagePreview(image));

    public async Task ShowFilePreviewAsync(string fileName, Stream content, string? contentType = null)
    {
        if (content.CanSeek) content.Position = 0;

        Control body;
        var resolved = FileTypes.ResolveContentType(fileName, content);
        if (content.CanSeek) content.Position = 0;

        if (FileTypes.IsImage(resolved, fileName) || FileTypes.IsImage(contentType, fileName))
        {
            body = BuildImagePreview(new Bitmap(content));
        }
        else if (LooksLikeText(resolved, fileName))
        {
            using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync();
            body = BuildTextPreview(text);
        }
        else
        {
            body = new TextBlock
            {
                Text = "Preview isn’t available for this file type. Use Download to save it.",
                FontSize = 15,
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(48),
                MaxWidth = 480,
            };
        }

        await ShowPreviewWindowAsync(fileName, body);
    }

    private async Task ShowPreviewWindowAsync(string title, Control body)
    {
        if (TopLevelAccessor() is not Window owner) return;

        var dialog = new Window
        {
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowState = WindowState.Maximized,
            SystemDecorations = SystemDecorations.Full,
            CanResize = true,
            Background = Brush.Parse("#0B1624"),
            Foreground = Brush.Parse("#E8F1F5"),
            Width = owner.ClientSize.Width,
            Height = owner.ClientSize.Height,
        };

        var close = new Button
        {
            Width = 44,
            Height = 44,
            MinWidth = 44,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0),
            Background = Brush.Parse("#CC122A40"),
            CornerRadius = new CornerRadius(22),
            Cursor = new Cursor(StandardCursorType.Hand),
            ZIndex = 10,
            Content = new TextBlock
            {
                Text = "✕",
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#E8F1F5"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => dialog.Close();

        var stage = new Panel { Children = { body, close } };
        var root = new Border
        {
            Background = Brush.Parse("#F00B1624"),
            Child = stage,
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.Close();
        };

        dialog.Content = root;
        await dialog.ShowDialog(owner);
    }

    private static Image BuildImagePreview(Bitmap image) =>
        new()
        {
            Source = image,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(32),
        };

    private static Control BuildTextPreview(string text) =>
        new ScrollViewer
        {
            Margin = new Thickness(32, 56, 32, 32),
            Content = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            },
        };

    private static bool LooksLikeText(string? contentType, string fileName)
    {
        if (contentType is not null
            && (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)))
            return true;

        return Path.GetExtension(fileName).ToLowerInvariant() is ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log" or ".cs" or ".ts" or ".js";
    }
}
