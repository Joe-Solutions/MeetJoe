using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeetJoe.App.Services;

namespace MeetJoe.App.Controls;

/// <summary>
/// Enables dragging files onto a chat surface (conversation + composer). Dropped files become
/// <see cref="PickedFile"/>s for the same upload path as the Attach button.
/// </summary>
public static class ComposerFileDrop
{
    public static void Enable(Control target, Func<IReadOnlyList<PickedFile>, Task> onFilesDropped)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(onFilesDropped);

        // Explicit background so empty areas of the conversation still hit-test for drops.
        if (target is Border { Background: null } border)
            border.Background = Avalonia.Media.Brushes.Transparent;

        DragDrop.SetAllowDrop(target, true);
        // Tunnel so we see the gesture before children; Bubble so Drop still lands if a
        // child marks the tunnel route handled without consuming files.
        const RoutingStrategies routes = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        target.AddHandler(DragDrop.DragEnterEvent, OnDragEnter, routes);
        target.AddHandler(DragDrop.DragOverEvent, OnDragOver, routes);
        target.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, routes);
        target.AddHandler(DragDrop.DropEvent, OnDrop, routes);

        void OnDragEnter(object? sender, DragEventArgs e) => ApplyDragFeedback(target, e);

        void OnDragOver(object? sender, DragEventArgs e) => ApplyDragFeedback(target, e);

        void OnDragLeave(object? sender, DragEventArgs e)
        {
            // Entering a child fires Leave on the parent — only clear when the pointer
            // has actually left the drop surface.
            if (IsPointerInside(target, e)) return;
            target.Classes.Remove("filedrop");
        }

        async void OnDrop(object? sender, DragEventArgs e)
        {
            target.Classes.Remove("filedrop");
            if (!HasFiles(e))
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            e.DragEffects = AllowedEffect(e);
            e.Handled = true;

            var picked = await ToPickedFilesAsync(e).ConfigureAwait(true);
            if (picked.Count == 0) return;
            try { await onFilesDropped(picked).ConfigureAwait(true); }
            catch { /* upload errors handled by the view-model */ }
        }
    }

    private static void ApplyDragFeedback(Control target, DragEventArgs e)
    {
        if (!HasFiles(e))
        {
            e.DragEffects = DragDropEffects.None;
            target.Classes.Remove("filedrop");
            return;
        }

        e.DragEffects = AllowedEffect(e);
        if (!target.Classes.Contains("filedrop"))
            target.Classes.Add("filedrop");
        e.Handled = true;
    }

    private static DragDropEffects AllowedEffect(DragEventArgs e)
    {
        // Intersect with what the source offers — setting Copy alone can reject the drop.
        if ((e.DragEffects & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
        if ((e.DragEffects & DragDropEffects.Link) != 0) return DragDropEffects.Link;
        if ((e.DragEffects & DragDropEffects.Move) != 0) return DragDropEffects.Move;
        return DragDropEffects.None;
    }

    private static bool IsPointerInside(Control target, DragEventArgs e)
    {
        var bounds = target.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        var pos = e.GetPosition(target);
        return pos.X >= 0 && pos.Y >= 0 && pos.X <= bounds.Width && pos.Y <= bounds.Height;
    }

    private static bool HasFiles(DragEventArgs e)
    {
        try
        {
            if (e.DataTransfer is { } transfer && transfer.Contains(DataFormat.File))
                return true;
        }
        catch
        {
            // Fall through to legacy formats.
        }

#pragma warning disable CS0618 // Legacy IDataObject path for older hosts
        try
        {
            if (e.Data is null) return false;
            if (e.Data.Contains(DataFormats.Files)) return true;
            if (e.Data.Contains(DataFormats.FileNames)) return true;
        }
        catch
        {
            // ignore
        }
#pragma warning restore CS0618

        return false;
    }

    private static async Task<IReadOnlyList<PickedFile>> ToPickedFilesAsync(DragEventArgs e)
    {
        var result = new List<PickedFile>();

        try
        {
            if (e.DataTransfer is { } transfer)
            {
                var items = transfer.TryGetFiles();
                if (items is { Length: > 0 })
                {
                    foreach (var item in items)
                        TryAddStorageItem(result, item);
                }
            }
        }
        catch
        {
            // Fall through.
        }

        if (result.Count == 0)
        {
#pragma warning disable CS0618
            try
            {
                var items = e.Data?.GetFiles();
                if (items is not null)
                {
                    foreach (var item in items)
                        TryAddStorageItem(result, item);
                }
            }
            catch
            {
                // ignore
            }
#pragma warning restore CS0618
        }

        await Task.CompletedTask;
        return result;
    }

    private static void TryAddStorageItem(List<PickedFile> result, IStorageItem? item)
    {
        if (item is not IStorageFile file) return;
        var name = file.TryGetLocalPath() is { } path ? Path.GetFileName(path) : file.Name;
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        var storage = file;
        result.Add(new PickedFile(name, () => storage.OpenReadAsync()));
    }
}
