using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentRagSystem.Infrastructure.Repositories;

public class SqliteFeedbackRepository : IFeedbackRepository
{
    private readonly IDbContextFactory<DocumentDbContext> _contextFactory;
    private static bool _initialized = false;
    private static readonly object _initLock = new();

    public SqliteFeedbackRepository(IDbContextFactory<DocumentDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    private void EnsureDatabaseInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            using var context = _contextFactory.CreateDbContext();
            context.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""Feedbacks"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Feedbacks"" PRIMARY KEY,
                    ""UserId"" TEXT NOT NULL,
                    ""UserEmail"" TEXT NULL,
                    ""Comment"" TEXT NOT NULL,
                    ""ContactEmail"" TEXT NULL,
                    ""SearchPrompts"" TEXT NULL,
                    ""AssistantAnswer"" TEXT NULL,
                    ""ConversationId"" TEXT NULL,
                    ""CreatedAt"" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_Feedbacks_UserId"" ON ""Feedbacks"" (""UserId"");
                CREATE INDEX IF NOT EXISTS ""IX_Feedbacks_CreatedAt"" ON ""Feedbacks"" (""CreatedAt"");
            ");

            _initialized = true;
        }
    }

    public async Task<UserFeedback> SaveFeedbackAsync(UserFeedback feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        EnsureDatabaseInitialized();

        var entity = new FeedbackEntity
        {
            Id = string.IsNullOrWhiteSpace(feedback.Id) ? Guid.NewGuid().ToString() : feedback.Id,
            UserId = feedback.UserId,
            UserEmail = feedback.UserEmail,
            Comment = feedback.Comment,
            ContactEmail = feedback.ContactEmail,
            SearchPrompts = feedback.SearchPrompts,
            AssistantAnswer = feedback.AssistantAnswer,
            ConversationId = feedback.ConversationId,
            CreatedAt = feedback.CreatedAt == default ? DateTime.UtcNow : feedback.CreatedAt
        };

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Feedbacks.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        return ToModel(entity);
    }

    public async Task<IEnumerable<UserFeedback>> GetAllFeedbackAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        EnsureDatabaseInitialized();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.Feedbacks
            .AsNoTracking()
            .OrderByDescending(f => f.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken);

        return entities.Select(ToModel);
    }

    public async Task<UserFeedback?> GetFeedbackByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureDatabaseInitialized();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Feedbacks
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        return entity != null ? ToModel(entity) : null;
    }

    public async Task<bool> DeleteFeedbackAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        EnsureDatabaseInitialized();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Feedbacks.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (entity == null) return false;

        context.Feedbacks.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static UserFeedback ToModel(FeedbackEntity entity)
    {
        return new UserFeedback(
            Id: entity.Id,
            UserId: entity.UserId,
            UserEmail: entity.UserEmail,
            Comment: entity.Comment,
            ContactEmail: entity.ContactEmail,
            SearchPrompts: entity.SearchPrompts,
            AssistantAnswer: entity.AssistantAnswer,
            ConversationId: entity.ConversationId,
            CreatedAt: entity.CreatedAt
        );
    }
}
