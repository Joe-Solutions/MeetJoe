using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeetJoe.App.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class MessageThreadView : UserControl
{
    /// <summary>How close to the end counts as "reading the latest", in pixels.</summary>
    private const double FollowThreshold = 120;

    private MessageThreadViewModel? _thread;

    public MessageThreadView()
    {
        InitializeComponent();

        // Tunnel, so Enter is seen before the text box turns it into a new line.
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        // Edit boxes live in item templates — catch Enter/Esc via the scroll surface.
        Scroller.AddHandler(KeyDownEvent, OnEditKeyDown, RoutingStrategies.Tunnel);
        Scroller.ScrollChanged += OnScrollChanged;
        ComposerFileDrop.Enable(ThreadDropZone, files =>
            _thread?.AttachFilesAsync(files) ?? Task.CompletedTask);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Follow(DataContext as MessageThreadViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow(DataContext as MessageThreadViewModel);
        Composer.Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Follow(null);
    }

    private void Follow(MessageThreadViewModel? thread)
    {
        if (ReferenceEquals(thread, _thread)) return;

        if (_thread is not null)
        {
            _thread.MessageAppended -= OnMessageAppended;
            _thread.ScrollToLatestRequested -= OnScrollToLatestRequested;
            _thread.FocusComposerRequested -= OnFocusComposerRequested;
            _thread.FocusEditRequested -= OnFocusEditRequested;
        }

        _thread = thread;
        if (_thread is not null)
        {
            _thread.MessageAppended += OnMessageAppended;
            _thread.ScrollToLatestRequested += OnScrollToLatestRequested;
            _thread.FocusComposerRequested += OnFocusComposerRequested;
            _thread.FocusEditRequested += OnFocusEditRequested;
            _thread.ClearNewMessagesHint();
        }
    }

    private void OnFocusComposerRequested() =>
        Dispatcher.UIThread.Post(() =>
        {
            Composer.Focus();
            if (Composer.Text is { Length: > 0 } text)
                Composer.CaretIndex = text.Length;
        }, DispatcherPriority.Input);

    private void OnFocusEditRequested() =>
        Dispatcher.UIThread.Post(FocusActiveEditBox, DispatcherPriority.Loaded);

    private void FocusActiveEditBox()
    {
        var box = this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.Classes.Contains("messageedit") && t.IsEffectivelyVisible);
        if (box is null) return;
        box.Focus();
        if (box.Text is { Length: > 0 } text)
            box.CaretIndex = text.Length;
    }

    private void OnMessageAppended()
    {
        // Measured before layout picks up the new message, so this is where the reader was.
        var distanceFromEnd = Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y;
        if (distanceFromEnd <= FollowThreshold)
        {
            _thread?.ClearNewMessagesHint();
            Dispatcher.UIThread.Post(Scroller.ScrollToEnd, DispatcherPriority.Loaded);
            return;
        }

        // Scrolled up into history — show the floating “New messages” chip.
        _thread?.NotifyUnseenMessages();
    }

    private void OnScrollToLatestRequested() =>
        Dispatcher.UIThread.Post(Scroller.ScrollToEnd, DispatcherPriority.Loaded);

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_thread is not { ShowNewMessagesHint: true }) return;
        var distanceFromEnd = Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y;
        if (distanceFromEnd <= FollowThreshold)
            _thread.ClearNewMessagesHint();
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        // Enter sends; Shift+Enter falls through to the text box and adds a line.
        e.Handled = true;
        if (_thread?.SendCommand.CanExecute(null) == true) _thread.SendCommand.Execute(null);
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { Classes: var classes } box || !classes.Contains("messageedit"))
            return;

        if (box.DataContext is not MessageViewModel message) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (message.CancelEditCommand.CanExecute(null))
                message.CancelEditCommand.Execute(null);
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        // Enter saves the edit; Shift+Enter inserts a new line.
        e.Handled = true;
        if (message.SaveEditCommand.CanExecute(null))
            message.SaveEditCommand.Execute(null);
    }
}
