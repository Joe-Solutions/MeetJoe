using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class MusicView : UserControl
{
    public MusicView() => InitializeComponent();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is MusicViewModel music && music.SearchCommand.CanExecute(null))
        {
            music.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
