using System;
using System.Collections.Generic;
using System.Net;
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
    private readonly IEmailTemplateService _templateService;

    public FeedbackNotificationService(
        IOptions<FeedbackNotificationOptions> options,
        IEmailSender emailSender,
        ILogger<FeedbackNotificationService> logger,
        IEmailTemplateService? templateService = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(emailSender);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _emailSender = emailSender;
        _logger = logger;
        _templateService = templateService ?? new EmailTemplateService();
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

        var parameters = new Dictionary<string, string?>
        {
            ["FeedbackId"] = feedback.Id,
            ["CreatedAt"] = $"{feedback.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC",
            ["UserId"] = feedback.UserId,
            ["UserEmail"] = feedback.UserEmail ?? string.Empty,
            ["ContactEmail"] = feedback.ContactEmail ?? string.Empty,
            ["ConversationId"] = feedback.ConversationId ?? string.Empty,
            ["Comment"] = feedback.Comment,
            ["SearchPrompts"] = feedback.SearchPrompts ?? string.Empty,
            ["AssistantAnswer"] = feedback.AssistantAnswer ?? string.Empty,
            ["Year"] = DateTime.UtcNow.Year.ToString()
        };

        var bodyHtml = await _templateService.RenderTemplateAsync("feedback-notification", parameters, ct);

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
}
