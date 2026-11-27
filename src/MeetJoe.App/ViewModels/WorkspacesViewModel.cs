using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

public partial class ChannelViewModel(ChannelDto channel, WorkspaceViewModel workspace) : ObservableObject
{
    public Guid Id { get; } = channel.Id;
    public string Name { get; } = channel.Name;
    public string? Description { get; } = channel.Description;
    public ChannelKind Kind { get; } = channel.Kind;
    public bool IsPrivate { get; } = channel.Kind == ChannelKind.Private;
    public int MemberCount { get; } = channel.MemberCount;
    public WorkspaceViewModel Workspace { get; } = workspace;

    [ObservableProperty] private bool _isSelected;
}

public partial class WorkspaceViewModel : ObservableObject
{
    public WorkspaceViewModel(WorkspaceDto workspace)
    {
        Id = workspace.Id;
        Name = workspace.Name;
        Description = workspace.Description;
        IsOwner = workspace.Role == WorkspaceRole.Owner;
        MemberCount = workspace.MemberCount;
        foreach (var channel in workspace.Channels) Channels.Add(new ChannelViewModel(channel, this));
    }

    public Guid Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public bool IsOwner { get; }
    public int MemberCount { get; }
    public ObservableCollection<ChannelViewModel> Channels { get; } = [];

    [ObservableProperty] private bool _isAddingChannel;
    [ObservableProperty] private string _newChannelName = string.Empty;
    [ObservableProperty] private bool _newChannelIsPrivate;
    [ObservableProperty] private string? _channelError;
}

public sealed record WorkspaceMemberViewModel(PersonViewModel Person, string RoleText);

/// <summary>Teams and their channels: the tree, the open channel, and membership.</summary>
public partial class WorkspacesViewModel : ViewModelBase
{
    private readonly SessionContext _context;
    private bool _restoringSelection;

    public WorkspacesViewModel(SessionContext context)
    {
        _context = context;
        context.Realtime.MessageReceived += OnMessageReceived;
        context.Realtime.MessageUpdated += OnMessageUpdated;
        context.Realtime.Typing += OnTyping;
        context.Realtime.WorkspacesChanged += OnWorkspacesChanged;
        context.Realtime.ReadReceipt += OnReadReceipt;
    }

    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];
    public ObservableCollection<WorkspaceMemberViewModel> Members { get; } = [];
    public ObservableCollection<ChannelMemberDto> ChannelMembers { get; } = [];

    /// <summary>Organization members who are not yet in the open workspace.</summary>
    public ObservableCollection<PersonViewModel> Addable { get; } = [];
    public ObservableCollection<PersonViewModel> ChannelAddable { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWorkspaces))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private MessageThreadViewModel? _thread;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanAddMembers), nameof(ShowEmptyState))]
    private ChannelViewModel? _selectedChannel;

    [ObservableProperty] private bool _isMembersPanelOpen;
    [ObservableProperty] private string? _membersError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isCreatingWorkspace;
    [ObservableProperty] private string _newWorkspaceName = string.Empty;
    [ObservableProperty] private string _newWorkspaceDescription = string.Empty;
    [ObservableProperty] private string? _createError;

    public bool HasSelection => SelectedChannel is not null;
    public bool ShowEmptyState => SelectedChannel is null && !IsCreatingWorkspace;
    public bool HasNoWorkspaces => !IsLoading && Workspaces.Count == 0;
    public bool CanAddMembers => SelectedChannel?.Workspace.IsOwner == true;

    public void Detach()
    {
        _context.Realtime.MessageReceived -= OnMessageReceived;
        _context.Realtime.MessageUpdated -= OnMessageUpdated;
        _context.Realtime.ReadReceipt -= OnReadReceipt;
        _context.Realtime.Typing -= OnTyping;
        _context.Realtime.WorkspacesChanged -= OnWorkspacesChanged;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var workspaces = await _context.Api.GetWorkspacesAsync(_context.Organization.Id);
            var selectedId = SelectedChannel?.Id;

            Workspaces.Clear();
            foreach (var dto in workspaces) Workspaces.Add(new WorkspaceViewModel(dto));

            // The channel view models were rebuilt; point the selection at the new instance
            // without reopening the thread the user is reading.
            var match = selectedId is { } id
                ? Workspaces.SelectMany(w => w.Channels).FirstOrDefault(c => c.Id == id)
                : null;
            if (match is not null)
            {
                _restoringSelection = true;
                SelectedChannel = match;
                _restoringSelection = false;
                if (IsMembersPanelOpen) await LoadMembersAsync();
            }
            else if (selectedId is not null)
            {
                SelectedChannel = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoWorkspaces));
        }
    }

    [RelayCommand]
    private void SelectChannel(ChannelViewModel channel) => SelectedChannel = channel;

    partial void OnSelectedChannelChanged(ChannelViewModel? oldValue, ChannelViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (_restoringSelection) return;

        IsMembersPanelOpen = false;
        if (newValue is null)
        {
            Thread = null;
            return;
        }

        IsCreatingWorkspace = false;
        var thread = new MessageThreadViewModel(
            _context, null, newValue.Id, newValue.Name, $"{newValue.Workspace.Name} · {newValue.Workspace.MemberCount} members");
        Thread = thread;
        _ = OpenChannelAsync(newValue, thread);
    }

    private async Task OpenChannelAsync(ChannelViewModel channel, MessageThreadViewModel thread)
    {
        try
        {
            var workspaceMembers = await _context.Api.GetWorkspaceMembersAsync(channel.Workspace.Id);
            var peers = workspaceMembers.Select(m => _context.People.Get(m.UserId, m.DisplayName)).ToList();
            if (channel.IsPrivate)
            {
                var channelMembers = await _context.Api.GetChannelMembersAsync(channel.Id);
                peers = channelMembers.Select(m => _context.People.Get(m.UserId, m.DisplayName)).ToList();
            }

            var cursors = await _context.Api.GetChannelReadCursorsAsync(channel.Id);
            thread.ConfigureSeenBy(peers, cursors);
            await thread.LoadAsync();
            await _context.Api.MarkChannelReadAsync(channel.Id);
            thread.ApplyReadReceipt(_context.People.MyId, DateTimeOffset.UtcNow, _context.People.Me.DisplayName);
        }
        catch (Exception)
        {
            await thread.LoadAsync();
        }
    }

    [RelayCommand]
    private void BeginCreateWorkspace()
    {
        NewWorkspaceName = string.Empty;
        NewWorkspaceDescription = string.Empty;
        CreateError = null;
        SelectedChannel = null;
        IsCreatingWorkspace = true;
    }

    [RelayCommand]
    private void CancelCreateWorkspace() => IsCreatingWorkspace = false;

    [RelayCommand]
    private async Task CreateWorkspaceAsync()
    {
        CreateError = null;
        try
        {
            var created = await _context.Api.CreateWorkspaceAsync(
                _context.Organization.Id, new CreateWorkspaceRequest(NewWorkspaceName, NewWorkspaceDescription));
            IsCreatingWorkspace = false;
            await LoadAsync();
            SelectedChannel = Workspaces.FirstOrDefault(w => w.Id == created.Id)?.Channels.FirstOrDefault();
        }
        catch (Exception ex)
        {
            CreateError = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private void BeginAddChannel(WorkspaceViewModel workspace)
    {
        workspace.NewChannelName = string.Empty;
        workspace.NewChannelIsPrivate = false;
        workspace.ChannelError = null;
        workspace.IsAddingChannel = true;
    }

    [RelayCommand]
    private void CancelAddChannel(WorkspaceViewModel workspace) => workspace.IsAddingChannel = false;

    [RelayCommand]
    private async Task AddChannelAsync(WorkspaceViewModel workspace)
    {
        workspace.ChannelError = null;
        try
        {
            var kind = workspace.NewChannelIsPrivate ? ChannelKind.Private : ChannelKind.Standard;
            var created = await _context.Api.CreateChannelAsync(
                workspace.Id, new CreateChannelRequest(workspace.NewChannelName, null, kind));
            await LoadAsync();
            SelectedChannel = Workspaces.SelectMany(w => w.Channels).FirstOrDefault(c => c.Id == created.Id);
        }
        catch (Exception ex)
        {
            workspace.ChannelError = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task ToggleMembersPanelAsync()
    {
        IsMembersPanelOpen = !IsMembersPanelOpen;
        if (IsMembersPanelOpen) await LoadMembersAsync();
    }

    [RelayCommand]
    private async Task AddMemberAsync(PersonViewModel person)
    {
        if (SelectedChannel is not { } channel) return;
        MembersError = null;
        try
        {
            await _context.Api.AddWorkspaceMemberAsync(channel.Workspace.Id, person.Id);
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            MembersError = Describe.Error(ex);
        }
    }

    private async Task LoadMembersAsync()
    {
        if (SelectedChannel is not { } channel) return;
        MembersError = null;
        try
        {
            var members = await _context.Api.GetWorkspaceMembersAsync(channel.Workspace.Id);
            Members.Clear();
            foreach (var member in members)
            {
                var person = _context.People.Get(member.UserId, member.DisplayName);
                person.Status = member.Status;
                Members.Add(new WorkspaceMemberViewModel(person, member.Role == WorkspaceRole.Owner ? "Owner" : "Member"));
            }

            var memberIds = members.Select(m => m.UserId).ToHashSet();
            Addable.Clear();
            foreach (var person in _context.People.People.Where(p => !memberIds.Contains(p.Id))) Addable.Add(person);

            ChannelMembers.Clear();
            ChannelAddable.Clear();
            if (channel.IsPrivate)
            {
                var channelMembers = await _context.Api.GetChannelMembersAsync(channel.Id);
                foreach (var member in channelMembers) ChannelMembers.Add(member);
                var channelIds = channelMembers.Select(m => m.UserId).ToHashSet();
                foreach (var member in members.Where(m => !channelIds.Contains(m.UserId)))
                    ChannelAddable.Add(_context.People.Get(member.UserId, member.DisplayName));
            }
        }
        catch (Exception ex)
        {
            MembersError = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task AddChannelMemberAsync(PersonViewModel person)
    {
        if (SelectedChannel is not { IsPrivate: true } channel) return;
        MembersError = null;
        try
        {
            await _context.Api.AddChannelMemberAsync(channel.Id, person.Id);
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            MembersError = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task RemoveChannelMemberAsync(ChannelMemberDto member)
    {
        if (SelectedChannel is not { IsPrivate: true } channel) return;
        MembersError = null;
        try
        {
            await _context.Api.RemoveChannelMemberAsync(channel.Id, member.UserId);
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            MembersError = Describe.Error(ex);
        }
    }

    private void OnWorkspacesChanged() => _ = LoadAsync();

    private void OnTyping(TypingDto typing) => Thread?.ApplyTyping(typing);

    private void OnMessageReceived(MessageDto message)
    {
        if (message.ChannelId is null) return;
        if (Thread is not { } thread || !thread.Matches(message)) return;
        thread.Append(message);
        if (message.Sender.Id != _context.People.MyId && SelectedChannel?.Id == message.ChannelId)
        {
            _ = MarkChannelReadQuietAsync(message.ChannelId.Value);
        }
    }

    private void OnMessageUpdated(MessageDto message)
    {
        if (message.ChannelId is null) return;
        if (Thread is { } thread && thread.Matches(message)) thread.ApplyUpdate(message);
    }

    private void OnReadReceipt(ReadReceiptDto receipt)
    {
        if (receipt.ChannelId is not { } channelId) return;
        if (SelectedChannel?.Id != channelId || Thread is null) return;
        var name = _context.People.People.FirstOrDefault(p => p.Id == receipt.UserId)?.DisplayName;
        Thread.ApplyReadReceipt(receipt.UserId, receipt.ReadAt, name);
    }

    private async Task MarkChannelReadQuietAsync(Guid channelId)
    {
        try
        {
            await _context.Api.MarkChannelReadAsync(channelId);
            Thread?.ApplyReadReceipt(_context.People.MyId, DateTimeOffset.UtcNow, _context.People.Me.DisplayName);
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }
}
