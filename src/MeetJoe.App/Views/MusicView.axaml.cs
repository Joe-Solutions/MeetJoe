using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class MusicView : UserControl
{
    /// <summary>Shell docks the persistent YouTube host into this panel while Music is visible.</summary>
    public static event Action<Panel?>? PlayerSlotChanged;

    public MusicView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => PlayerSlotChanged?.Invoke(PlayerHost);
        DetachedFromVisualTree += (_, _) => PlayerSlotChanged?.Invoke(null);
    }

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is MusicViewModel music && music.OpenUrlCommand.CanExecute(null))
        {
            music.OpenUrlCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: MusicRecentItemViewModel item }) return;
        if (DataContext is not MusicViewModel music) return;
        if (music.OpenRecentCommand.CanExecute(item))
            music.OpenRecentCommand.Execute(item);
    }
}
