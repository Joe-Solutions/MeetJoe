using MeetJoe.Application.Common;
using MeetJoe.Domain;
using MeetJoe.Infrastructure.Email;
using MeetJoe.Infrastructure.Identity;
using MeetJoe.Infrastructure.Meetings;
using MeetJoe.Infrastructure.Persistence;
using MeetJoe.Infrastructure.Presence;
using MeetJoe.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MeetJoe.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = configuration.GetConnectionString("Postgres")
                       ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
        services.AddDbContext<AppDbContext>(options => AppDbContext.Configure(options, postgres));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services
            .AddIdentityCore<User>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppDbContext>();

        // Presence lives in Redis when it is configured, otherwise in process memory.
        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis))
        {
            services.AddSingleton<IPresenceStore, InMemoryPresenceStore>();
        }
        else
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(redis);
                options.AbortOnConnectFail = false; // Keep retrying instead of failing API startup.
                return ConnectionMultiplexer.Connect(options);
            });
            services.AddSingleton<RedisPresenceStore>();
            services.AddSingleton<IPresenceStore>(sp => sp.GetRequiredService<RedisPresenceStore>());
        }

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.AddSingleton<IFileStorage, MinioFileStorage>();

        services.Configure<LiveKitOptions>(configuration.GetSection(LiveKitOptions.Section));
        services.AddSingleton<IMeetingTokenIssuer, LiveKitTokenIssuer>();

        services.Configure<PublicUrlsOptions>(configuration.GetSection(PublicUrlsOptions.Section));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IGuestAccountService, GuestAccountService>();

        return services;
    }
}
