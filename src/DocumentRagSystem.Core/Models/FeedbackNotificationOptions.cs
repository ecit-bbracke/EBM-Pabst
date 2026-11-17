namespace DocumentRagSystem.Core.Models;

/// <summary>
/// Configuration options for email dispatch upon user feedback submission.
/// </summary>
public sealed class FeedbackNotificationOptions
{
    public const string SectionName = "FeedbackNotification";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Recipient email address or comma-separated list of recipient addresses.
    /// Default recipient requested by user: bbracke@ecit.com
    /// </summary>
    public string RecipientEmail { get; set; } = "bbracke@ecit.com";

    /// <summary>
    /// SMTP connection options, default configured for SendGrid.
    /// </summary>
    public SmtpOptions Smtp { get; set; } = new();
}

/// <summary>
/// Standard SMTP server connection options (SendGrid compatible).
/// </summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "smtp.sendgrid.net";

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string UserName { get; set; } = "apikey";

    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = "xjs@ecitsoftware.com";

    public string FromName { get; set; } = "ebm-papst Portal Feedback";
}
