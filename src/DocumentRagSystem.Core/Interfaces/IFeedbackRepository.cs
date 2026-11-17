using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IFeedbackRepository
{
    Task<UserFeedback> SaveFeedbackAsync(UserFeedback feedback, CancellationToken cancellationToken = default);
    Task<IEnumerable<UserFeedback>> GetAllFeedbackAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<UserFeedback?> GetFeedbackByIdAsync(string id, CancellationToken cancellationToken = default);
}
