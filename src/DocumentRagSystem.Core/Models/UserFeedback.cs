using System;
using System.Text.Json.Serialization;

namespace DocumentRagSystem.Core.Models;

public record UserFeedback(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("user_email")] string? UserEmail,
    [property: JsonPropertyName("comment")] string Comment,
    [property: JsonPropertyName("contact_email")] string? ContactEmail,
    [property: JsonPropertyName("search_prompts")] string? SearchPrompts,
    [property: JsonPropertyName("assistant_answer")] string? AssistantAnswer,
    [property: JsonPropertyName("conversation_id")] string? ConversationId,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt
);
