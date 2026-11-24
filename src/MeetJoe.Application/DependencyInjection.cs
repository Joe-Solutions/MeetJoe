using MeetJoe.Application.Activity;
using MeetJoe.Application.Calls;
using MeetJoe.Application.Chat;
using MeetJoe.Application.Files;
using MeetJoe.Application.Meetings;
using MeetJoe.Application.Organizations;
using MeetJoe.Application.Presence;
using MeetJoe.Application.Profiles;
using MeetJoe.Application.Search;
using MeetJoe.Application.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetJoe.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OrganizationService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<PresenceService>();
        services.AddScoped<ChatService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<FileService>();
        services.AddScoped<MeetingService>();
        services.AddScoped<CallService>();
        services.AddScoped<SearchService>();
        return services;
    }
}
