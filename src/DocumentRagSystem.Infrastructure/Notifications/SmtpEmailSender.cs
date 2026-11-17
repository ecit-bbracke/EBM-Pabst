using System;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentRagSystem.Infrastructure.Notifications;

/// <summary>
/// Dispatches email messages via standard SMTP (SendGrid relay).
/// Modelled after the proven implementation in the Mercedes synchronization project.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private static readonly char[] RecipientSeparators = [',', ';'];

    private readonly FeedbackNotificationOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(
        IOptions<FeedbackNotificationOptions> options,
        ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    public async Task SendEmailAsync(
        string recipient,
        string subject,
        string body,
        bool isHtml = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var sanitizedSubject = SanitizeSubject(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedSubject, nameof(subject));

        var smtp = _options.Smtp;
        var fromAddress = !string.IsNullOrWhiteSpace(smtp.FromAddress)
            ? smtp.FromAddress.Trim()
            : "xjs@ecitsoftware.com";

        var fromName = !string.IsNullOrWhiteSpace(smtp.FromName)
            ? smtp.FromName.Trim()
            : "ebm-papst Portal Feedback";

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = sanitizedSubject,
            Body = body,
            IsBodyHtml = isHtml,
        };

        var recipients = recipient.Split(
            RecipientSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var r in recipients)
        {
            message.To.Add(r);
        }

        var host = !string.IsNullOrWhiteSpace(smtp.Host) ? smtp.Host.Trim() : "smtp.sendgrid.net";
        var port = smtp.Port > 0 ? smtp.Port : 587;

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = smtp.EnableSsl,
        };

        // Determine password/key: use configured password, or check environment variable SENDGRID_API_KEY
        var apiKey = !string.IsNullOrWhiteSpace(smtp.Password)
            ? smtp.Password.Trim()
            : (Environment.GetEnvironmentVariable("SENDGRID_API_KEY") ?? string.Empty).Trim();

        var userName = !string.IsNullOrWhiteSpace(smtp.UserName)
            ? smtp.UserName.Trim()
            : "apikey";

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.Credentials = new NetworkCredential(userName, apiKey);
        }
        else
        {
            _logger.LogWarning("[SmtpEmailSender] No SMTP password or SENDGRID_API_KEY configured. Email dispatch may fail if relay requires authentication.");
        }

        _logger.LogDebug(
            "Dispatching email to {Recipient} via SMTP {Host}:{Port} (SSL={EnableSsl}) with subject: {Subject}",
            recipient, host, port, smtp.EnableSsl, sanitizedSubject);

        await client.SendMailAsync(message, ct);

        _logger.LogInformation(
            "Email successfully dispatched to {Recipient} with subject: {Subject}",
            recipient, sanitizedSubject);
    }

    private static string SanitizeSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return string.Empty;
        }

        return string.Join(" ", subject.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
