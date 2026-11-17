using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Repositories;

public class InMemoryFeedbackRepository : IFeedbackRepository
{
    private readonly ConcurrentDictionary<string, UserFeedback> _feedbacks = new(StringComparer.OrdinalIgnoreCase);

    public Task<UserFeedback> SaveFeedbackAsync(UserFeedback feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        var item = feedback with
        {
            Id = string.IsNullOrWhiteSpace(feedback.Id) ? Guid.NewGuid().ToString() : feedback.Id,
            CreatedAt = feedback.CreatedAt == default ? DateTime.UtcNow : feedback.CreatedAt
        };

        _feedbacks[item.Id] = item;
        return Task.FromResult(item);
    }

    public Task<IEnumerable<UserFeedback>> GetAllFeedbackAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var items = _feedbacks.Values
            .OrderByDescending(f => f.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToList();

        return Task.FromResult<IEnumerable<UserFeedback>>(items);
    }

    public Task<UserFeedback?> GetFeedbackByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult<UserFeedback?>(null);

        _feedbacks.TryGetValue(id, out var item);
        return Task.FromResult(item);
    }
}
