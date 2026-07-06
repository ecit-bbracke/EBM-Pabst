using System.Linq;
using DocumentRagSystem.Core.Services;
using FluentAssertions;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class ChunkingTests
{
    [Fact]
    public void ChunkText_ShouldSplitText_WhenTextExceedsMaxSize()
    {
        // Arrange
        var chunker = new ChunkingService(maxChunkSize: 50, chunkOverlap: 10);
        var text = "This is a very long text that should definitely exceed fifty characters and get chunked properly.";
        var documentId = "doc1";

        // Act
        var chunks = chunker.ChunkText(text, documentId).ToList();

        // Assert
        chunks.Should().NotBeEmpty();
        chunks.All(c => c.DocumentId == documentId).Should().BeTrue();
        chunks.All(c => c.Text.Length <= 50).Should().BeTrue();
        
        // Check indexes are sequential
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Index.Should().Be(i);
            chunks[i].Id.Should().Be($"{documentId}_chunk_{i}");
        }
    }

    [Fact]
    public void ChunkText_ShouldNormalizeWhitespace_BeforeChunking()
    {
        // Arrange
        var chunker = new ChunkingService(maxChunkSize: 100, chunkOverlap: 20);
        var text = "Line 1\r\n   Line 2   \n\n Line 3";
        
        // Act
        var chunks = chunker.ChunkText(text, "doc1").ToList();

        // Assert
        chunks.Should().HaveCount(1);
        chunks.First().Text.Should().Be("Line 1 Line 2 Line 3");
    }
}
