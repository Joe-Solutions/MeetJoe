using System.ComponentModel;
using Avalonia.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class CalendarView : UserControl
{
    private CalendarViewModel? _vm;

    public CalendarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel(DataContext as CalendarViewModel);
    }

    private void BindViewModel(CalendarViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelPropertyChanged;
        SyncStageHost();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalendarViewModel.IsInMeeting) or nameof(CalendarViewModel.Stage))
            SyncStageHost();
    }

    /// <summary>
    /// MeetingStageView constructs a WebView; building it while browsing Calendar blanks the
    /// whole section when the WebView host is unavailable. Create it only for an active meeting.
    /// </summary>
    private void SyncStageHost()
    {
        if (_vm?.IsInMeeting == true)
        {
            if (StageHost.Content is not MeetingStageView stage)
            {
                stage = new MeetingStageView();
                StageHost.Content = stage;
            }

            stage.DataContext = _vm;
        }
        else
        {
            StageHost.Content = null;
        }
    }
}
