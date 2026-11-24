using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeetJoe.App.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class MeetingStageView : UserControl
{
    private CalendarViewModel? _vm;
    private MeetingStageViewModel? _stage;

    public MeetingStageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel(DataContext as CalendarViewModel);

        // Tunnel so Enter is seen before the text box inserts a newline.
        LiveChatComposer.AddHandler(KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
        LobbyChatComposer.AddHandler(KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
    }

    private void BindViewModel(CalendarViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnCalendarPropertyChanged;
        UnbindStage();
        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnCalendarPropertyChanged;
            BindStage(_vm.Stage);
        }

        SyncMediaHost();
    }

    private void OnCalendarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.Stage))
            BindStage(_vm?.Stage);
    }

    private void BindStage(MeetingStageViewModel? stage)
    {
        UnbindStage();
        _stage = stage;
        if (_stage is not null) _stage.PropertyChanged += OnStagePropertyChanged;
        SyncMediaHost();
    }

    private void UnbindStage()
    {
        if (_stage is not null) _stage.PropertyChanged -= OnStagePropertyChanged;
        _stage = null;
    }

    private void OnStagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MeetingStageViewModel.IsMediaOpen)
            or nameof(MeetingStageViewModel.MediaPageUri))
            SyncMediaHost();
    }

    private void OnChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_stage?.SendChatCommand.CanExecute(null) == true)
            _stage.SendChatCommand.Execute(null);
    }

    private void SyncMediaHost()
    {
        if (_stage is not { IsMediaOpen: true })
        {
            MediaHost.Content = null;
            return;
        }

        OpenMedia(_stage.MediaPageUri);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenMedia(Uri uri)
    {
        if (MediaHost.Content is not InAppMediaHost host)
        {
            host = new InAppMediaHost();
            host.SetLeaveHandler(() =>
            {
                if (_vm?.LeaveCommand.CanExecute(null) == true)
                    _ = _vm.LeaveCommand.ExecuteAsync(null);
            });
            MediaHost.Content = host;
        }

        host.Show(uri);
    }
}
