using System;
using System.IO;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class DocumentProcessorTests
{
    [Fact]
    public async Task ProcessPdfAsync_ExtractsTextAndChunks()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Sample text from PDF");

        var processor = new DocumentProcessor(mockTextExtractor.Object, Mock.Of<IChunkingService>());

        // Act
        var result = await processor.ProcessPdfAsync(new MemoryStream(), "test.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test.pdf", result.FileName);
    }
}
