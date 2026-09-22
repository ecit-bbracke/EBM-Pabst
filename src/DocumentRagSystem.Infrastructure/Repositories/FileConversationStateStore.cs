using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Repositories;

public class FileConversationStateStore : IConversationStateStore
{
    private readonly string _storageDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public FileConversationStateStore(string? storageDirectory = null)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(AppContext.BaseDirectory, ".conversations");
        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }
    }

    private string GetFilePath(string conversationId)
    {
        var sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_storageDirectory, $"{sanitized}.json");
    }

    public async Task<ConversationState?> GetStateAsync(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return null;

        var path = GetFilePath(conversationId);
        if (!File.Exists(path))
            return null;

        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var state = JsonSerializer.Deserialize<ConversationState>(json, _jsonOptions);
            // Touch file write time to update last accessed time
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return state;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveStateAsync(string conversationId, ConversationState state)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new ArgumentNullException(nameof(conversationId));
        if (state == null)
            throw new ArgumentNullException(nameof(state));

        var path = GetFilePath(conversationId);
        var json = JsonSerializer.Serialize(state, _jsonOptions);

        await _lock.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(path, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearStateAsync(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        var path = GetFilePath(conversationId);
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveConstraintAsync(string conversationId, string attribute)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(attribute))
            return;

        var state = await GetStateAsync(conversationId);
        if (state != null)
        {
            var updatedConstraints = state.ActiveConstraints
                .Where(c => !string.Equals(c.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var updatedState = state with { ActiveConstraints = updatedConstraints };
            await SaveStateAsync(conversationId, updatedState);
        }
    }

    public async Task ClearExpiredStatesAsync(TimeSpan ttl)
    {
        if (!Directory.Exists(_storageDirectory))
            return;

        var cutoff = DateTime.UtcNow - ttl;

        await _lock.WaitAsync();
        try
        {
            var files = Directory.GetFiles(_storageDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignore single file cleanup errors
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
