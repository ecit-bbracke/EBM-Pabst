using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentRagSystem.Infrastructure.Notifications;

public sealed class FeedbackNotificationService : IFeedbackNotificationService
{
    private readonly FeedbackNotificationOptions _options;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<FeedbackNotificationService> _logger;

    public FeedbackNotificationService(
        IOptions<FeedbackNotificationOptions> options,
        IEmailSender emailSender,
        ILogger<FeedbackNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(emailSender);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task SendFeedbackNotificationAsync(UserFeedback feedback, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        if (!_options.Enabled)
        {
            _logger.LogInformation("[FeedbackNotification] Email notifications are disabled in configuration.");
            return;
        }

        var recipient = !string.IsNullOrWhiteSpace(_options.RecipientEmail)
            ? _options.RecipientEmail.Trim()
            : "bbracke@ecit.com";

        var userDisplay = !string.IsNullOrWhiteSpace(feedback.UserEmail)
            ? feedback.UserEmail
            : (!string.IsNullOrWhiteSpace(feedback.UserId) ? feedback.UserId : "Anonym bruger");

        var subject = $"[ebm-papst Portal Feedback] Ny feedback fra {userDisplay}";

        var bodyHtml = BuildHtmlBody(feedback, userDisplay);

        try
        {
            await _emailSender.SendEmailAsync(recipient, subject, bodyHtml, isHtml: true, ct);
            _logger.LogInformation("[FeedbackNotification] Notification email dispatched for feedback {FeedbackId} to {Recipient}.",
                feedback.Id, recipient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FeedbackNotification] Failed to send feedback notification email for feedback {FeedbackId} to {Recipient}: {Error}",
                feedback.Id, recipient, ex.Message);
        }
    }

    private static string BuildHtmlBody(UserFeedback feedback, string userDisplay)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="da">
            <head>
              <meta charset="utf-8" />
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; color: #1e293b; background-color: #f8fafc; margin: 0; padding: 20px; }
                .card { max-width: 680px; margin: 0 auto; background: #ffffff; border: 1px solid #e2e8f0; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.05); }
                .header { background: #0c4a6e; color: #ffffff; padding: 20px 24px; }
                .header h2 { margin: 0; font-size: 18px; font-weight: 700; letter-spacing: -0.01em; }
                .header p { margin: 4px 0 0; font-size: 12px; opacity: 0.85; }
                .content { padding: 24px; }
                table.meta { width: 100%; border-collapse: collapse; margin-bottom: 20px; font-size: 13px; }
                table.meta th, table.meta td { padding: 8px 12px; text-align: left; border-bottom: 1px solid #f1f5f9; }
                table.meta th { width: 32%; color: #64748b; font-weight: 600; background-color: #f8fafc; }
                table.meta td { color: #0f172a; font-weight: 500; }
                .section-title { font-size: 12px; font-weight: 700; text-transform: uppercase; color: #475569; letter-spacing: 0.05em; margin: 20px 0 6px; }
                .comment-box { background-color: #fef3c7; border: 1px solid #fde68a; border-left: 4px solid #f59e0b; padding: 14px 16px; border-radius: 6px; font-size: 14px; line-height: 1.5; color: #78350f; font-weight: 500; white-space: pre-wrap; }
                .code-box { background-color: #f1f5f9; border: 1px solid #e2e8f0; padding: 12px 14px; border-radius: 6px; font-family: Consolas, Monaco, monospace; font-size: 12px; line-height: 1.5; color: #334155; white-space: pre-wrap; max-height: 240px; overflow-y: auto; }
                .answer-box { background-color: #f8fafc; border: 1px solid #e2e8f0; padding: 14px 16px; border-radius: 6px; font-size: 13px; line-height: 1.6; color: #1e293b; max-height: 320px; overflow-y: auto; }
                .footer { background: #f8fafc; padding: 14px 24px; border-top: 1px solid #e2e8f0; font-size: 11px; color: #94a3b8; text-align: center; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="header">
                  <h2>ebm-papst Portal &bull; Ny Brugerfeedback</h2>
                  <p>Automatisk videresendt til support og udviklingsteamet</p>
                </div>
                <div class="content">
            """);

        sb.Append("<table class=\"meta\">");
        sb.Append($"<tr><th>Tidspunkt (UTC)</th><td>{feedback.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC</td></tr>");
        sb.Append($"<tr><th>Bruger-ID (Session)</th><td><code>{WebUtility.HtmlEncode(feedback.UserId)}</code></td></tr>");

        if (!string.IsNullOrWhiteSpace(feedback.UserEmail))
        {
            sb.Append($"<tr><th>Login-Email</th><td><a href=\"mailto:{WebUtility.HtmlEncode(feedback.UserEmail)}\">{WebUtility.HtmlEncode(feedback.UserEmail)}</a></td></tr>");
        }

        if (!string.IsNullOrWhiteSpace(feedback.ContactEmail))
        {
            sb.Append($"<tr><th>Valgfri Kontakt-Email</th><td><strong><a href=\"mailto:{WebUtility.HtmlEncode(feedback.ContactEmail)}\">{WebUtility.HtmlEncode(feedback.ContactEmail)}</a></strong></td></tr>");
        }

        if (!string.IsNullOrWhiteSpace(feedback.ConversationId))
        {
            sb.Append($"<tr><th>Samtale-ID</th><td><code>{WebUtility.HtmlEncode(feedback.ConversationId)}</code></td></tr>");
        }

        sb.Append("</table>");

        sb.Append("<div class=\"section-title\">Brugerens Kommentar:</div>");
        sb.Append($"<div class=\"comment-box\">{WebUtility.HtmlEncode(feedback.Comment)}</div>");

        if (!string.IsNullOrWhiteSpace(feedback.SearchPrompts))
        {
            sb.Append("<div class=\"section-title\">Søgeprompter der udløste svaret:</div>");
            sb.Append($"<div class=\"code-box\">{WebUtility.HtmlEncode(feedback.SearchPrompts)}</div>");
        }

        if (!string.IsNullOrWhiteSpace(feedback.AssistantAnswer))
        {
            sb.Append("<div class=\"section-title\">AI Assistentens Svar:</div>");
            sb.Append($"<div class=\"answer-box\">{WebUtility.HtmlEncode(feedback.AssistantAnswer)}</div>");
        }

        sb.Append("""
                </div>
                <div class="footer">
                  ebm-papst Document RAG Portal &bull; Genereret automatisk via SendGrid SMTP relay
                </div>
              </div>
            </body>
            </html>
            """);

        return sb.ToString();
    }
}
