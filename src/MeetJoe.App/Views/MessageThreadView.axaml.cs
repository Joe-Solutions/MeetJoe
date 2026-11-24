using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

        if (_thread is not null) _thread.MessageAppended -= OnMessageAppended;
        _thread = thread;
        if (_thread is not null) _thread.MessageAppended += OnMessageAppended;
    }

    private void OnMessageAppended()
    {
        // Measured before layout picks up the new message, so this is where the reader was.
        // Someone scrolled up into history is left alone.
        var distanceFromEnd = Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y;
        if (distanceFromEnd > FollowThreshold) return;

        Dispatcher.UIThread.Post(Scroller.ScrollToEnd, DispatcherPriority.Loaded);
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        // Enter sends; Shift+Enter falls through to the text box and adds a line.
        e.Handled = true;
        if (_thread?.SendCommand.CanExecute(null) == true) _thread.SendCommand.Execute(null);
    }
}
