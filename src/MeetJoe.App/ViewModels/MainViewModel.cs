using System.Reflection;
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

    public string VersionText { get; } = FormatVersion();

    static string FormatVersion()
    {
        var asm = typeof(LoginViewModel).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var version = informational.Split('+', 2)[0];
        var file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? asm.GetName().Version?.ToString()
            ?? version;
        var build = Version.TryParse(file, out var parsed) ? parsed.Revision.ToString() : file;
        if (build is "-1") build = file;
        return $"Version {version}  ·  Build {build}";
    }

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
    private ShellViewModel? _shell;

    public MainViewModel(
        AppConfig config,
        IAuthService auth,
        IMeetJoeApi api,
        IRealtimeClient realtime,
        IPlatformServices platform,
        ICallTonePlayer? callTones = null)
    {
        _config = config;
        _auth = auth;
        _api = api;
        _realtime = realtime;
        _platform = platform;
        _callTones = callTones ?? new NullCallTonePlayer();
        _current = NewLogin();

        auth.SessionExpired += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = SignOutAsync(
            "Your session has ended. Sign in again."));
    }

    [ObservableProperty] private ViewModelBase _current;

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
        people.Get(me.Id, me.DisplayName);

        var shell = new ShellViewModel(
            new SessionContext(_api, _realtime, _platform, _config, organization, people, _callTones),
            me, _config, () => SignOutAsync(), LeftOrganizationAsync);
        _shell = shell;
        Current = shell;
        await shell.InitializeAsync();
    }

    /// <summary>After leaving the only organization, return to create/join setup while staying signed in.</summary>
    private async Task LeftOrganizationAsync()
    {
        if (_shell is { } shell)
        {
            _shell = null;
            await shell.ShutdownAsync();
        }

        await EnterAsync();
    }

    public async Task SignOutAsync(string? message = null)
    {
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

    private LoginViewModel NewLogin() => new(_auth, _api, EnterAsync);
}
