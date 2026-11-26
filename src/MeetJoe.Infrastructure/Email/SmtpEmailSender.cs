using System.Net;
using System.Net.Mail;
using MeetJoe.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetJoe.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>When false, messages are logged instead of sent (local/dev default).</summary>
    public bool Enabled { get; set; }

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "noreply@meetjoe.local";
    public string FromName { get; set; } = "MeetJoe";
}

public sealed class SmtpEmailSender(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Host))
        {
            logger.LogInformation(
                "Email (dry-run) to {To}: {Subject}\n{Body}",
                toEmail, subject, htmlBody);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(toEmail);

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrWhiteSpace(_options.Username))
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);

        // SmtpClient has no CancellationToken overload on all TFMs; honor cancel before send.
        ct.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, ct);
    }
}
