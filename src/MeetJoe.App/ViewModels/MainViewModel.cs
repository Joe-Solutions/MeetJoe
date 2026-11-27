using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.App.Services;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

public partial class LoginViewModel(IAuthService auth, IMeetJoeApi api, Func<Task> onSignedIn) : ViewModelBase
{
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmitText), nameof(ToggleText), nameof(TogglePrompt))]
    private bool _isRegistering;

    public string SubmitText => IsRegistering ? "Create account" : "Sign in";
    public string TogglePrompt => IsRegistering ? "Already have an account?" : "New to MeetJoe?";
    public string ToggleText => IsRegistering ? "Sign in" : "Create an account";

    public string VersionText { get; } = $"Version {AppVersion.Current}";

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegistering = !IsRegistering;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Enter your email and password.";
            return;
        }

        if (IsRegistering && string.IsNullOrWhiteSpace(DisplayName))
        {
            ErrorMessage = "Enter the name your colleagues will see.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var email = Email.Trim();
            if (IsRegistering) await api.RegisterAsync(new RegisterRequest(email, Password, DisplayName.Trim()));
            await auth.SignInAsync(email, Password);
            Password = string.Empty;
            await onSignedIn();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Shown to a signed-in user who does not belong to an organization yet.</summary>
public partial class OrganizationSetupViewModel(
    IMeetJoeApi api, string displayName, Func<Task> onReady, Func<Task> signOut) : ViewModelBase
{
    public string Greeting { get; } = $"Welcome, {displayName}";

    [ObservableProperty] private string _organizationName = string.Empty;
    [ObservableProperty] private string _joinCode = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private Task CreateAsync() => RunAsync(() => api.CreateOrganizationAsync(OrganizationName));

    [RelayCommand]
    private Task JoinAsync() => RunAsync(() => api.JoinOrganizationAsync(JoinCode));

    [RelayCommand]
    private Task SignOutAsync() => signOut();

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
            await onReady();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Root of the app: decides between sign-in, organization setup and the shell.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly AppConfig _config;
    private readonly IAuthService _auth;
    private readonly IMeetJoeApi _api;
    private readonly IRealtimeClient _realtime;
    private readonly IPlatformServices _platform;
    private readonly ICallTonePlayer _callTones;
    private readonly IOsNotificationService _osNotifications;
    private readonly IAppUpdateService _updates;
    private ShellViewModel? _shell;

    public MainViewModel(
        AppConfig config,
        IAuthService auth,
        IMeetJoeApi api,
        IRealtimeClient realtime,
        IPlatformServices platform,
        ICallTonePlayer? callTones = null,
        IOsNotificationService? osNotifications = null,
        IAppUpdateService? updates = null)
    {
        _config = config;
        _auth = auth;
        _api = api;
        _realtime = realtime;
        _platform = platform;
        _callTones = callTones ?? new NullCallTonePlayer();
        _osNotifications = osNotifications ?? new NullOsNotificationService();
        _updates = updates ?? new NullAppUpdateService();
        _current = NewLogin();

        auth.SessionExpired += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = SignOutAsync(
            "Your session has ended. Sign in again."));
    }

    [ObservableProperty] private ViewModelBase _current;

    public IAppUpdateService Updates => _updates;

    /// <summary>Runs once at startup — downloads and applies a newer semver release when installed.</summary>
    public Task StartUpdateCheckAsync() => _updates.CheckAsync(applyAutomatically: true);

    /// <summary>At startup: sign in silently with the saved session, if any.</summary>
    public async Task ResumeSessionAsync()
    {
        if (Current is not LoginViewModel login) return;
        login.IsBusy = true;
        try
        {
            if (!await _auth.TryResumeAsync()) return;
            await EnterAsync();
        }
        catch (Exception ex)
        {
            await SignOutAsync(Describe.Error(ex));
        }
        finally
        {
            login.IsBusy = false;
        }
    }

    /// <summary>Loads the signed-in user and opens the right screen for them.</summary>
    public async Task EnterAsync()
    {
        var me = await _api.GetMeAsync();
        var organizations = await _api.GetOrganizationsAsync();
        if (organizations.Count == 0)
        {
            Current = new OrganizationSetupViewModel(_api, me.DisplayName, EnterAsync, () => SignOutAsync());
            return;
        }

        // One organization at a time; switching between several is not built yet.
        var organization = organizations[0];
        var people = new PeopleStore(me.Id);
        people.Get(me.Id, me.DisplayName).ApplyAvatar(me.AvatarUrl);

        var shell = new ShellViewModel(
            new SessionContext(
                _api, _realtime, _platform, _config, organization, people, _callTones, _osNotifications),
            me, _config, () => SignOutAsync(), LeftOrganizationAsync, _updates);
        _shell = shell;
        Current = shell;
        await shell.InitializeAsync();
        StartDeepLinkWatch();

        if (App.PendingDeepLink is { Length: > 0 } token)
        {
            App.PendingDeepLink = null;
            shell.ShowCalendar();
            _ = shell.Calendar.JoinFromInviteTokenAsync(token);
        }

        if (App.PendingChatDeepLink is { } chatId)
        {
            App.PendingChatDeepLink = null;
            _ = shell.OpenChatFromDeepLinkAsync(chatId);
        }
    }

    /// <summary>Focus / notification click — open a pending @mention chat when present.</summary>
    public Task HandleWindowActivatedAsync()
    {
        ConsumeDeepLinkDrop();
        return _shell?.TryOpenPendingMentionAsync() ?? Task.CompletedTask;
    }

    private CancellationTokenSource? _deepLinkWatch;

    private void StartDeepLinkWatch()
    {
        _deepLinkWatch?.Cancel();
        _deepLinkWatch?.Dispose();
        var cts = new CancellationTokenSource();
        _deepLinkWatch = cts;
        _ = WatchDeepLinkDropAsync(cts.Token);
    }

    private async Task WatchDeepLinkDropAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(750, ct); }
            catch (OperationCanceledException) { return; }
            ConsumeDeepLinkDrop();
        }
    }

    private void ConsumeDeepLinkDrop()
    {
        try
        {
            if (!File.Exists(App.DeepLinkDropPath)) return;
            var text = File.ReadAllText(App.DeepLinkDropPath).Trim();
            File.Delete(App.DeepLinkDropPath);
            if (string.IsNullOrWhiteSpace(text) || _shell is not { } shell) return;

            if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && uri.Scheme.Equals("meetjoe", StringComparison.OrdinalIgnoreCase))
            {
                // join?token=
                foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length == 2
                        && pair[0].Equals("token", StringComparison.OrdinalIgnoreCase)
                        && pair[1].Length > 0)
                    {
                        shell.ShowCalendar();
                        _ = shell.Calendar.JoinFromInviteTokenAsync(Uri.UnescapeDataString(pair[1]));
                        return;
                    }
                }

                // chat?id=
                foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length != 2) continue;
                    if (!pair[0].Equals("id", StringComparison.OrdinalIgnoreCase)
                        && !pair[0].Equals("conversationId", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Guid.TryParse(Uri.UnescapeDataString(pair[1]), out var chatId))
                    {
                        _ = shell.OpenChatFromDeepLinkAsync(chatId);
                        return;
                    }
                }
            }
        }
        catch
        {
            // Deep-link handoff is best-effort.
        }
    }

    /// <summary>After leaving the only organization, return to create/join setup while staying signed in.</summary>
    private async Task LeftOrganizationAsync()
    {
        _deepLinkWatch?.Cancel();
        _deepLinkWatch?.Dispose();
        _deepLinkWatch = null;

        if (_shell is { } shell)
        {
            _shell = null;
            await shell.ShutdownAsync();
        }

        await EnterAsync();
    }

    public async Task SignOutAsync(string? message = null)
    {
        _deepLinkWatch?.Cancel();
        _deepLinkWatch?.Dispose();
        _deepLinkWatch = null;

        if (_shell is { } shell)
        {
            _shell = null;
            await shell.ShutdownAsync();
        }

        _auth.SignOut();
        var login = NewLogin();
        login.ErrorMessage = message;
        Current = login;
    }

    /// <summary>Ask before quitting so LiveKit/WebView can shut down cleanly.</summary>
    public Task<bool> ConfirmExitAsync()
    {
        var inCall = _shell?.Calls.HasMedia == true || _shell?.Calendar.IsInMeeting == true;
        var message = inCall
            ? "You are still in a call or meeting. Quit MeetJoe? Any open media will end first so the app can close safely."
            : "Quit MeetJoe? Open media sessions will end first so the app can close safely.";

        return _platform.ConfirmAsync("Quit MeetJoe?", message, "Quit", "Cancel");
    }

    /// <summary>Hang up media and detach hubs before the process exits (avoids WKWebView crash on quit).</summary>
    public async Task PrepareForExitAsync()
    {
        if (_shell is not { } shell) return;

        try
        {
            if (shell.Calls.HangUpCommand.CanExecute(null))
                await shell.Calls.HangUpCommand.ExecuteAsync(null);
        }
        catch { /* best-effort */ }

        try
        {
            if (shell.Calendar.LeaveCommand.CanExecute(null))
                await shell.Calendar.LeaveCommand.ExecuteAsync(null);
        }
        catch { /* best-effort */ }

        try { await shell.ShutdownAsync(); }
        catch { /* best-effort */ }
    }

    private LoginViewModel NewLogin() => new(_auth, _api, EnterAsync);
}
