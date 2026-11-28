using Foundation;
using Avalonia;
using Avalonia.iOS;
using MeetJoe.App.Services;

namespace MeetJoe.iOS;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<global::MeetJoe.App.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        global::MeetJoe.App.App.UpdateServiceFactory = _ => new NullAppUpdateService();
        global::MeetJoe.App.App.SessionStoreFactory = () => new NullSessionStore();

        return base.CustomizeAppBuilder(builder);
    }
}
