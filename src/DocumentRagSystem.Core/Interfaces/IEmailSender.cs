using System.Threading;
using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

/// <summary>
/// Abstraction for dispatching email messages (e.g. via SendGrid SMTP).
/// </summary>
public interface IEmailSender
{
    Task SendEmailAsync(
        string recipient,
        string subject,
        string body,
        bool isHtml = true,
        CancellationToken ct = default);
}
