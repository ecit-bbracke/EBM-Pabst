using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.WebApi.HostedServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class ConversationStateStoreTests : IDisposable
{
    private readonly string _testTempDir;

    public ConversationStateStoreTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "rag_test_conv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch
        {
            // Ignore temp dir cleanup errors
        }
    }

    [Fact]
    public async Task InMemoryConversationStateStore_ShouldTrackAndRemoveSpecificConstraint()
    {
        // Arrange
        var store = new InMemoryConversationStateStore();
        var state = new ConversationState(
            ConversationId: "conv-remove-c",
            Topic: "fan selection",
            ActiveEntities: new List<ConversationEntity> { new("fan", "A6E450") },
            ActiveConstraints: new List<ConversationConstraint>
            {
                new("voltage", "supports", "230", "V", 1),
                new("diameter", "<=", "450", "mm", 1)
            },
            TurnCount: 1
        );

        await store.SaveStateAsync("conv-remove-c", state);

        // Act
        await store.RemoveConstraintAsync("conv-remove-c", "voltage");
        var updated = await store.GetStateAsync("conv-remove-c");

        // Assert
        Assert.NotNull(updated);
        Assert.Single(updated.ActiveConstraints);
        Assert.Equal("diameter", updated.ActiveConstraints[0].Attribute);
    }

    [Fact]
    public async Task InMemoryConversationStateStore_ShouldEvictExpiredStates()
    {
        // Arrange
        var store = new InMemoryConversationStateStore();
        var state = new ConversationState(
            ConversationId: "conv-expire-1",
            Topic: "general",
            ActiveEntities: new List<ConversationEntity>(),
            ActiveConstraints: new List<ConversationConstraint>(),
            TurnCount: 1
        );

        await store.SaveStateAsync("conv-expire-1", state);

        // Act - Call ClearExpiredStates with a zero TTL to simulate expiry
        await store.ClearExpiredStatesAsync(TimeSpan.Zero);
        var cleared = await store.GetStateAsync("conv-expire-1");

        // Assert
        Assert.Null(cleared);
    }

    [Fact]
    public async Task FileConversationStateStore_ShouldPersistAndRehydrateAcrossInstances()
    {
        // Arrange
        var store1 = new FileConversationStateStore(_testTempDir);
        var state = new ConversationState(
            ConversationId: "conv-file-1",
            Topic: "controller search",
            ActiveEntities: new List<ConversationEntity> { new("controller", "Model X") },
            ActiveConstraints: new List<ConversationConstraint> { new("protocol", "supports", "Modbus", null, 1) },
            CandidateSet: new CandidateSet("controller", new List<string> { "Model X", "Model Y" }, 1),
            TurnCount: 2
        );

        // Act - Save with store 1
        await store1.SaveStateAsync("conv-file-1", state);

        // Act - Load with store 2 (simulating server restart)
        var store2 = new FileConversationStateStore(_testTempDir);
        var rehydrated = await store2.GetStateAsync("conv-file-1");

        // Assert
        Assert.NotNull(rehydrated);
        Assert.Equal("conv-file-1", rehydrated.ConversationId);
        Assert.Equal("controller search", rehydrated.Topic);
        Assert.Single(rehydrated.ActiveEntities);
        Assert.Equal("Model X", rehydrated.ActiveEntities[0].Name);
        Assert.Equal(2, rehydrated.TurnCount);
        Assert.NotNull(rehydrated.CandidateSet);
        Assert.Equal(2, rehydrated.CandidateSet.Members.Count);
    }

    [Fact]
    public async Task FileConversationStateStore_ShouldRemoveSpecificConstraintAndClear()
    {
        // Arrange
        var store = new FileConversationStateStore(_testTempDir);
        var state = new ConversationState(
            ConversationId: "conv-file-remove",
            Topic: "fan selection",
            ActiveEntities: new List<ConversationEntity>(),
            ActiveConstraints: new List<ConversationConstraint>
            {
                new("voltage", "equals", "24", "VDC", 1),
                new("ip_rating", "equals", "IP67", null, 1)
            },
            TurnCount: 1
        );

        await store.SaveStateAsync("conv-file-remove", state);

        // Act - Remove constraint
        await store.RemoveConstraintAsync("conv-file-remove", "voltage");
        var updated = await store.GetStateAsync("conv-file-remove");

        // Assert
        Assert.NotNull(updated);
        Assert.Single(updated.ActiveConstraints);
        Assert.Equal("ip_rating", updated.ActiveConstraints[0].Attribute);

        // Act - Clear state
        await store.ClearStateAsync("conv-file-remove");
        var cleared = await store.GetStateAsync("conv-file-remove");
        Assert.Null(cleared);
    }

    [Fact]
    public async Task FileConversationStateStore_ShouldClearExpiredFiles()
    {
        // Arrange
        var store = new FileConversationStateStore(_testTempDir);
        var state = new ConversationState(
            ConversationId: "conv-file-expire",
            Topic: "test",
            ActiveEntities: new List<ConversationEntity>(),
            ActiveConstraints: new List<ConversationConstraint>(),
            TurnCount: 1
        );

        await store.SaveStateAsync("conv-file-expire", state);

        // Act - Clear with negative/zero TTL
        await store.ClearExpiredStatesAsync(TimeSpan.Zero);
        var cleared = await store.GetStateAsync("conv-file-expire");

        // Assert
        Assert.Null(cleared);
    }
}
