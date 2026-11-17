using System;

namespace DocumentRagSystem.Infrastructure.Data;

public class FeedbackEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string? UserEmail { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? SearchPrompts { get; set; }
    public string? AssistantAnswer { get; set; }
    public string? ConversationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
