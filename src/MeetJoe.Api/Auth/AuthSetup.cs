using System.Security.Cryptography;
using System.Threading.RateLimiting;
using MeetJoe.Contracts;
using MeetJoe.Infrastructure.Persistence;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MeetJoe.Api.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Directory holding the token signing and encryption keys. Created on first run.</summary>
    public string KeysPath { get; set; } = ".data/keys";

    /// <summary>Refuse token requests over plain HTTP. Only switch off for local development.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Sign-in and sign-up attempts allowed per client address per minute.</summary>
    public int RateLimitPerMinute { get; set; } = 20;
}

internal static class AuthSetup
{
    public const string RateLimitPolicy = "auth";

    public static IServiceCollection AddMeetJoeAuth(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        var keysPath = Path.IsPathRooted(auth.KeysPath)
            ? auth.KeysPath
            : Path.Combine(environment.ContentRootPath, auth.KeysPath);

        services.AddOpenIddict()
            .AddCore(options => options
                .UseEntityFrameworkCore()
                .UseDbContext<AppDbContext>()
                .ReplaceDefaultEntities<Guid>())
            .AddServer(options =>
            {
                options.SetTokenEndpointUris(AuthConstants.TokenEndpoint);

                // First-party native clients collect credentials in their own sign-in screen.
                options.AllowPasswordFlow().AllowRefreshTokenFlow();

                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(30));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(14));

                options.AddSigningKey(TokenKeys.LoadOrCreate(keysPath, "signing"));
                options.AddEncryptionKey(TokenKeys.LoadOrCreate(keysPath, "encryption"));

                // Access tokens are signed JWTs; refresh tokens stay encrypted.
                options.DisableAccessTokenEncryption();

                var aspNetCore = options.UseAspNetCore().EnableTokenEndpointPassthrough();
                if (!auth.RequireHttps) aspNetCore.DisableTransportSecurityRequirement();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        services.AddAuthorization();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = auth.RateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }));
        });

        return services;
    }

    /// <summary>Registers the desktop app as a public OAuth client if it is not there yet.</summary>
    public static async Task SeedClientsAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var applications = services.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(AuthConstants.DesktopClientId, ct) is not null) return;

        await applications.CreateAsync(
            new OpenIddictApplicationDescriptor
            {
                ClientId = AuthConstants.DesktopClientId,
                ClientType = ClientTypes.Public,
                DisplayName = "MeetJoe Desktop",
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.Password,
                    Permissions.GrantTypes.RefreshToken,
                },
            },
            ct);
    }
}

/// <summary>
/// RSA keys persisted as PEM files so tokens stay valid across API restarts. In production, point
/// <see cref="AuthOptions.KeysPath"/> at a protected volume or replace this with a key vault.
/// </summary>
internal static class TokenKeys
{
    public static RsaSecurityKey LoadOrCreate(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.pem");

        // Owned by the returned key for the lifetime of the process.
        var rsa = RSA.Create(2048);
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var writer = new StreamWriter(path, options);
            writer.Write(rsa.ExportRSAPrivateKeyPem());
        }

        return new RsaSecurityKey(rsa);
    }
}
