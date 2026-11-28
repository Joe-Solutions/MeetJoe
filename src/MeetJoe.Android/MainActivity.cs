using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using MeetJoe.App.Services;

namespace MeetJoe.Android;

[Activity(
    Label = "MeetJoe",
    Theme = "@style/Theme.MeetJoe",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<global::MeetJoe.App.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        global::MeetJoe.App.App.UpdateServiceFactory = _ => new NullAppUpdateService();
        global::MeetJoe.App.App.SessionStoreFactory = () => new NullSessionStore();

        return base.CustomizeAppBuilder(builder);
    }
}
