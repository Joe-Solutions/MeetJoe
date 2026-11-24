using System.Security.Claims;
using MeetJoe.Application.Common;
using MeetJoe.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;

namespace MeetJoe.Api.Common;

internal static class ClaimsPrincipalExtensions
{
    private const string Subject = "sub";

    public static string? GetSubject(this ClaimsPrincipal? user) =>
        user?.FindFirst(Subject)?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public static Guid GetUserId(this ClaimsPrincipal? user) =>
        Guid.TryParse(user.GetSubject(), out var id)
            ? id
            : throw new InvalidOperationException("The authenticated principal has no user id.");
}

/// <summary>Routes SignalR "send to user" calls by the token subject.</summary>
internal sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.GetSubject();
}

/// <summary>Turns expected failures into problem-details responses; anything else stays a 500.</summary>
internal sealed class AppExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, code, detail) = exception switch
        {
            AppException app => (app.StatusCode, app.Code, app.Message),
            DomainException domain => (StatusCodes.Status400BadRequest, "validation", domain.Message),
            BadHttpRequestException bad => (bad.StatusCode, "bad_request", "The request could not be read."),
            _ => (0, string.Empty, string.Empty),
        };
        if (status == 0) return false;

        http.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = ReasonPhrases.GetReasonPhrase(status),
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }
}

/// <summary>Surfaces expected failures to hub callers; SignalR hides every other exception message.</summary>
internal sealed class HubErrorFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (AppException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (DomainException ex)
        {
            throw new HubException(ex.Message);
        }
    }
}
