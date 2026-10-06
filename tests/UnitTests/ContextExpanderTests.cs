using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class ContextExpanderTests
{
    private readonly ContextExpander _expander = new();

    [Fact]
    public async Task ExpandContextAsync_WhenInitialChunksEmpty_ReturnsEmpty()
    {
        // Arrange
        var mockStore = new Mock<IVectorStore>();

        // Act
        var result = await _expander.ExpandContextAsync(new List<DocumentChunk>(), mockStore.Object);

        // Assert
        Assert.Empty(result);
        mockStore.Verify(x => x.GetChunksByDocumentAndIndicesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<int>>()), Times.Never);
    }

    [Fact]
    public async Task ExpandContextAsync_FetchesAdjacentNeighborsAndSortsByIndex()
    {
        // Arrange
        var mockStore = new Mock<IVectorStore>();
        var docId = "doc-100";
        var chunkMiddle = new DocumentChunk($"{docId}_c2", docId, "Middle chunk text", 2);

        var chunkPrev = new DocumentChunk($"{docId}_c1", docId, "Previous chunk text", 1);
        var chunkNext = new DocumentChunk($"{docId}_c3", docId, "Next chunk text", 3);

        mockStore
            .Setup(x => x.GetChunksByDocumentAndIndicesAsync(docId, It.Is<IEnumerable<int>>(indices => indices.Contains(1) && indices.Contains(3))))
            .ReturnsAsync(new List<DocumentChunk> { chunkNext, chunkPrev });

        // Act
        var result = await _expander.ExpandContextAsync(new List<DocumentChunk> { chunkMiddle }, mockStore.Object, windowSize: 1);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal(1, result[0].Index);
        Assert.Equal(2, result[1].Index);
        Assert.Equal(3, result[2].Index);
        Assert.Equal("Previous chunk text", result[0].Text);
        Assert.Equal("Middle chunk text", result[1].Text);
        Assert.Equal("Next chunk text", result[2].Text);
    }

    [Fact]
    public async Task ExpandContextAsync_DoesNotRequestNegativeIndicesForFirstChunk()
    {
        // Arrange
        var mockStore = new Mock<IVectorStore>();
        var docId = "doc-200";
        var chunkZero = new DocumentChunk($"{docId}_c0", docId, "First chunk text", 0);
        var chunkOne = new DocumentChunk($"{docId}_c1", docId, "Second chunk text", 1);

        mockStore
            .Setup(x => x.GetChunksByDocumentAndIndicesAsync(docId, It.Is<IEnumerable<int>>(indices => !indices.Any(i => i < 0) && indices.Contains(1))))
            .ReturnsAsync(new List<DocumentChunk> { chunkOne });

        // Act
        var result = await _expander.ExpandContextAsync(new List<DocumentChunk> { chunkZero }, mockStore.Object, windowSize: 1);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].Index);
        Assert.Equal(1, result[1].Index);
    }

    [Fact]
    public async Task ExpandContextAsync_WhenContiguousChunksPresent_OnlyRequestsOuterNeighbors()
    {
        // Arrange
        var mockStore = new Mock<IVectorStore>();
        var docId = "doc-300";
        var chunk1 = new DocumentChunk($"{docId}_c1", docId, "Text 1", 1);
        var chunk2 = new DocumentChunk($"{docId}_c2", docId, "Text 2", 2);

        var chunk0 = new DocumentChunk($"{docId}_c0", docId, "Text 0", 0);
        var chunk3 = new DocumentChunk($"{docId}_c3", docId, "Text 3", 3);

        mockStore
            .Setup(x => x.GetChunksByDocumentAndIndicesAsync(docId, It.Is<IEnumerable<int>>(indices =>
                indices.Contains(0) && indices.Contains(3) && !indices.Contains(1) && !indices.Contains(2))))
            .ReturnsAsync(new List<DocumentChunk> { chunk0, chunk3 });

        // Act
        var result = await _expander.ExpandContextAsync(new List<DocumentChunk> { chunk1, chunk2 }, mockStore.Object, windowSize: 1);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Select(c => c.Index).ToArray());
    }

    [Fact]
    public async Task ExpandContextAsync_ExpandsMultipleDocumentsIndependently()
    {
        // Arrange
        var mockStore = new Mock<IVectorStore>();
        var docA = "doc-A";
        var docB = "doc-B";

        var chunkA = new DocumentChunk($"{docA}_c5", docA, "Doc A chunk 5", 5);
        var chunkB = new DocumentChunk($"{docB}_c10", docB, "Doc B chunk 10", 10);

        var chunkA_neighbor = new DocumentChunk($"{docA}_c6", docA, "Doc A chunk 6", 6);
        var chunkB_neighbor = new DocumentChunk($"{docB}_c9", docB, "Doc B chunk 9", 9);

        mockStore
            .Setup(x => x.GetChunksByDocumentAndIndicesAsync(docA, It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new List<DocumentChunk> { chunkA_neighbor });

        mockStore
            .Setup(x => x.GetChunksByDocumentAndIndicesAsync(docB, It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new List<DocumentChunk> { chunkB_neighbor });

        // Act
        var result = await _expander.ExpandContextAsync(new List<DocumentChunk> { chunkA, chunkB }, mockStore.Object, windowSize: 1);

        // Assert
        Assert.Equal(4, result.Count);
        mockStore.Verify(x => x.GetChunksByDocumentAndIndicesAsync(docA, It.Is<IEnumerable<int>>(indices => indices.Contains(4) && indices.Contains(6))), Times.Once);
        mockStore.Verify(x => x.GetChunksByDocumentAndIndicesAsync(docB, It.Is<IEnumerable<int>>(indices => indices.Contains(9) && indices.Contains(11))), Times.Once);
    }
}
