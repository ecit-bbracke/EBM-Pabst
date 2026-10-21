using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.OrdinalIgnoreCase);
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

    private SemaphoreSlim GetLock(string conversationId)
    {
        return _conversationLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
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

        var convLock = GetLock(conversationId);
        await convLock.WaitAsync();
        try
        {
            if (!File.Exists(path))
                return null;

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
            convLock.Release();
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

        var convLock = GetLock(conversationId);
        await convLock.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(path, json);
        }
        finally
        {
            convLock.Release();
        }
    }

    public async Task ClearStateAsync(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        var path = GetFilePath(conversationId);
        var convLock = GetLock(conversationId);
        await convLock.WaitAsync();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            _conversationLocks.TryRemove(conversationId, out _);
        }
        finally
        {
            convLock.Release();
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

        var files = Directory.GetFiles(_storageDirectory, "*.json");
        foreach (var file in files)
        {
            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (lastWrite < cutoff)
                {
                    var convId = Path.GetFileNameWithoutExtension(file);
                    var convLock = GetLock(convId);
                    await convLock.WaitAsync();
                    try
                    {
                        // Recheck expiration under the lock to prevent deleting fresh saves that completed while waiting
                        if (File.Exists(file) && File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                            _conversationLocks.TryRemove(convId, out _);
                        }
                    }
                    finally
                    {
                        convLock.Release();
                    }
                }
            }
            catch
            {
                // Ignore single file cleanup errors
            }
        }
    }
}
