using System.Net;
using System.Text;
using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Meetings;

internal static class MeetingInviteMail
{
    public static (string Subject, string Html) Build(
        string meetingTitle,
        string hostName,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt,
        string? description,
        string webJoinUrl,
        string desktopDeepLink)
    {
        var when = FormatWhen(startsAt, endsAt);
        var subject = $"MeetJoe invitation: {meetingTitle}";
        var html = new StringBuilder()
            .Append("<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#0f172a;\">")
            .Append("<div style=\"max-width:560px;margin:0 auto;padding:24px;\">")
            .Append("<h1 style=\"font-size:22px;margin:0 0 8px;\">You're invited to a MeetJoe meeting</h1>")
            .Append("<p style=\"margin:0 0 16px;color:#475569;\">")
            .Append(WebUtility.HtmlEncode(hostName))
            .Append(" scheduled a meeting and invited you to join.</p>")
            .Append("<table style=\"width:100%;border-collapse:collapse;margin:0 0 20px;\">")
            .Append(Row("Meeting", meetingTitle))
            .Append(Row("When", when));
        if (!string.IsNullOrWhiteSpace(description))
            html.Append(Row("Details", description!));
        html
            .Append("</table>")
            .Append("<p style=\"margin:0 0 12px;\">")
            .Append("<a href=\"")
            .Append(WebUtility.HtmlEncode(desktopDeepLink))
            .Append("\" style=\"display:inline-block;background:#0d9488;color:#fff;text-decoration:none;padding:12px 18px;border-radius:8px;font-weight:600;\">")
            .Append("Open in MeetJoe</a></p>")
            .Append("<p style=\"margin:0 0 8px;color:#475569;font-size:14px;\">If the app is not installed, join in your browser:</p>")
            .Append("<p style=\"margin:0 0 20px;\"><a href=\"")
            .Append(WebUtility.HtmlEncode(webJoinUrl))
            .Append("\">")
            .Append(WebUtility.HtmlEncode(webJoinUrl))
            .Append("</a></p>")
            .Append("<p style=\"margin:0;color:#94a3b8;font-size:12px;\">You can use your microphone and camera as a guest in the web meeting.</p>")
            .Append("</div></body></html>");
        return (subject, html.ToString());
    }

    private static string Row(string label, string value) =>
        "<tr><td style=\"padding:6px 0;color:#64748b;width:88px;vertical-align:top;\">"
        + WebUtility.HtmlEncode(label)
        + "</td><td style=\"padding:6px 0;font-weight:600;\">"
        + WebUtility.HtmlEncode(value)
        + "</td></tr>";

    private static string FormatWhen(DateTimeOffset startsAt, DateTimeOffset? endsAt)
    {
        var localStart = startsAt.ToLocalTime();
        var startText = localStart.ToString("dddd, d MMM yyyy · h:mm tt");
        if (endsAt is not { } end) return startText;
        var localEnd = end.ToLocalTime();
        return localStart.Date == localEnd.Date
            ? $"{startText} – {localEnd:h:mm tt}"
            : $"{startText} – {localEnd:dddd, d MMM yyyy · h:mm tt}";
    }

    public static GuestMeetingInviteDto ToPublicDto(
        string token,
        Domain.Meeting meeting,
        string hostName,
        string? inviteeEmail,
        string? suggestedName,
        PublicUrlsOptions urls) =>
        new(
            token,
            meeting.Id,
            meeting.Title,
            meeting.Description,
            hostName,
            meeting.StartsAt,
            meeting.EndsAt,
            meeting.LobbyEnabled,
            meeting.IsActive,
            !meeting.IsActive,
            inviteeEmail,
            suggestedName,
            urls.JoinUrl(token),
            urls.DesktopDeepLink(token));
}
