using System.Security.Claims;
using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using MeetJoe.Domain;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MeetJoe.Api.Auth;

internal static class AuthEndpoints
{
    private const string InvalidCredentials = "The email or password is incorrect.";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/register", RegisterAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthSetup.RateLimitPolicy);

        app.MapPost(AuthConstants.TokenEndpoint, ExchangeAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthSetup.RateLimitPolicy)
            .DisableAntiforgery();
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request, UserManager<User> users, TimeProvider time)
    {
        var user = User.Create(request.Email, request.DisplayName, time.GetUtcNow());
        var result = await users.CreateAsync(user, request.Password ?? string.Empty);
        if (!result.Succeeded)
        {
            // The user name is the email, so Identity reports a taken address twice.
            var messages = result.Errors.Where(e => e.Code != "DuplicateUserName").Select(e => e.Description);
            throw AppException.Validation(string.Join(" ", messages));
        }

        return Results.Created("/api/me", new UserSummaryDto(user.Id, user.DisplayName));
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http, UserManager<User> users)
    {
        var request = http.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsPasswordGrantType())
        {
            var user = await users.FindByEmailAsync(request.Username ?? string.Empty);
            if (user is null) return Reject(InvalidCredentials);
            if (await users.IsLockedOutAsync(user))
                return Reject("Too many failed sign-in attempts. Try again in a few minutes.");

            if (!await users.CheckPasswordAsync(user, request.Password ?? string.Empty))
            {
                await users.AccessFailedAsync(user);
                return Reject(InvalidCredentials);
            }

            await users.ResetAccessFailedCountAsync(user);
            return SignIn(user, request.GetScopes());
        }

        if (request.IsRefreshTokenGrantType())
        {
            var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var subject = result.Principal?.GetClaim(Claims.Subject);
            var user = subject is null ? null : await users.FindByIdAsync(subject);
            if (user is null || await users.IsLockedOutAsync(user))
                return Reject("The session is no longer valid. Sign in again.");

            return SignIn(user, result.Principal!.GetScopes());
        }

        return Reject("The specified grant type is not supported.", Errors.UnsupportedGrantType);
    }

    private static IResult SignIn(User user, IEnumerable<string> requestedScopes)
    {
        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Name, user.DisplayName)
            .SetClaim(Claims.Email, user.Email);

        // offline_access is what makes OpenIddict issue a refresh token.
        identity.SetScopes(requestedScopes.Intersect([Scopes.OfflineAccess]));
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Reject(string description, string error = Errors.InvalidGrant)
    {
        var properties = new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        });
        return Results.Forbid(properties, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }
}
