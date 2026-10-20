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

    [Fact]
    public void ChunkText_WithFormFeedPageBreaks_ShouldPreservePhysicalPageNumbers_WhenIntermediatePagesAreEmpty()
    {
        // Arrange: Page 1 has text, Page 2 is a blank/scanned drawing without text, Page 3 has text
        var chunker = new ChunkingService();
        var text = "Page 1 content\f\fPage 3 content";
        var documentId = "doc-pdf-1";

        // Act
        var chunks = chunker.ChunkText(text, documentId).ToList();

        // Assert
        chunks.Should().HaveCount(2);

        // Chunk 0 corresponds to physical Page 1 (Index 0, PageNumber 1)
        chunks[0].Index.Should().Be(0);
        chunks[0].PageNumber.Should().Be(1);
        chunks[0].Id.Should().Be("doc-pdf-1_chunk_0");
        chunks[0].Text.Should().Be("Page 1 content");

        // Chunk 1 corresponds to physical Page 3 (Index 1, PageNumber 3)
        chunks[1].Index.Should().Be(1);
        chunks[1].PageNumber.Should().Be(3);
        chunks[1].Id.Should().Be("doc-pdf-1_chunk_1");
        chunks[1].Text.Should().Be("Page 3 content");
    }
}
