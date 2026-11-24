using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MeetJoe.App.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class ShellView : UserControl
{
    private ShellViewModel? _vm;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel(DataContext as ShellViewModel);
    }

    private void BindViewModel(ShellViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnShellPropertyChanged;
            _vm.Calls.CallChromeChanged -= SyncCallStage;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnShellPropertyChanged;
            _vm.Calls.CallChromeChanged += SyncCallStage;
        }

        SyncCallStage();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.ShowCallStage)
            or nameof(ShellViewModel.ShowCallBar)
            or nameof(ShellViewModel.Current))
            SyncCallStage();
    }

    private void SyncCallStage()
    {
        if (_vm is not { Calls.HasMedia: true, Calls.MediaPageUri: { } uri })
        {
            CallStageRoot.IsVisible = false;
            CallMediaHost.Content = null;
            return;
        }

        CallStageRoot.IsVisible = true;

        if (_vm.ShowCallStage)
        {
            CallStageRoot.ClearValue(WidthProperty);
            CallStageRoot.ClearValue(HeightProperty);
            CallStageRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
            CallStageRoot.VerticalAlignment = VerticalAlignment.Stretch;
            CallStageRoot.Opacity = 1;
            CallStageRoot.IsHitTestVisible = true;
            CallStageHeader.IsVisible = true;
        }
        else
        {
            // Keep the WebView in the tree so LiveKit audio continues while browsing.
            CallStageRoot.Width = 1;
            CallStageRoot.Height = 1;
            CallStageRoot.HorizontalAlignment = HorizontalAlignment.Left;
            CallStageRoot.VerticalAlignment = VerticalAlignment.Top;
            CallStageRoot.Opacity = 0;
            CallStageRoot.IsHitTestVisible = false;
            CallStageHeader.IsVisible = false;
        }

        OpenCallMedia(uri);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenCallMedia(Uri uri)
    {
        if (CallMediaHost.Content is not InAppMediaHost host)
        {
            host = new InAppMediaHost();
            host.SetLeaveHandler(() =>
            {
                if (_vm?.Calls.HangUpCommand.CanExecute(null) == true)
                    _vm.Calls.HangUpCommand.Execute(null);
            });
            CallMediaHost.Content = host;
        }

        host.Show(uri);
    }
}
