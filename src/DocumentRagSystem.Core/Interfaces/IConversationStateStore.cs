using System;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IConversationStateStore
{
    Task<ConversationState?> GetStateAsync(string conversationId);
    Task SaveStateAsync(string conversationId, ConversationState state);
    Task ClearStateAsync(string conversationId);
    Task RemoveConstraintAsync(string conversationId, string attribute);
    Task ClearExpiredStatesAsync(TimeSpan ttl);
}
