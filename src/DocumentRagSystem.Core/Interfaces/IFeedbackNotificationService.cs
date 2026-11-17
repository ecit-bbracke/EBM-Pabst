using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

/// <summary>
/// Domain service responsible for dispatching email notifications when user feedback is submitted.
/// </summary>
public interface IFeedbackNotificationService
{
    Task SendFeedbackNotificationAsync(
        UserFeedback feedback,
        CancellationToken ct = default);
}
