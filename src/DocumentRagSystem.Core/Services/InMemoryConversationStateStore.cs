using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public class InMemoryConversationStateStore : IConversationStateStore
{
    private record StateEntry(ConversationState State, DateTime LastAccessedUtc);

    private readonly ConcurrentDictionary<string, StateEntry> _states = new(StringComparer.OrdinalIgnoreCase);

    public Task<ConversationState?> GetStateAsync(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return Task.FromResult<ConversationState?>(null);

        if (_states.TryGetValue(conversationId, out var entry))
        {
            // Update last accessed time
            _states[conversationId] = entry with { LastAccessedUtc = DateTime.UtcNow };
            return Task.FromResult<ConversationState?>(entry.State);
        }

        return Task.FromResult<ConversationState?>(null);
    }

    public Task SaveStateAsync(string conversationId, ConversationState state)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new ArgumentNullException(nameof(conversationId));
        if (state == null)
            throw new ArgumentNullException(nameof(state));

        _states[conversationId] = new StateEntry(state, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task ClearStateAsync(string conversationId)
    {
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            _states.TryRemove(conversationId, out _);
        }
        return Task.CompletedTask;
    }

    public Task RemoveConstraintAsync(string conversationId, string attribute)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(attribute))
            return Task.CompletedTask;

        if (_states.TryGetValue(conversationId, out var entry))
        {
            var updatedConstraints = entry.State.ActiveConstraints
                .Where(c => !string.Equals(c.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var updatedState = entry.State with { ActiveConstraints = updatedConstraints };
            _states[conversationId] = new StateEntry(updatedState, DateTime.UtcNow);
        }

        return Task.CompletedTask;
    }

    public Task ClearExpiredStatesAsync(TimeSpan ttl)
    {
        var cutoff = DateTime.UtcNow - ttl;
        foreach (var kvp in _states)
        {
            if (kvp.Value.LastAccessedUtc < cutoff)
            {
                _states.TryRemove(kvp.Key, out _);
            }
        }
        return Task.CompletedTask;
    }
}
