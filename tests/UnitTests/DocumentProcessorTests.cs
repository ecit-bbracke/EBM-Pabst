using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
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

    [Fact]
    public async Task ProcessPdfAsync_WhenEnglishDocument_EmbedsAndAddsToVectorStore()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Technical specifications for axial fan model. Nominal voltage is 24 VDC.");

        var mockChunkingService = new Mock<IChunkingService>();
        mockChunkingService.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk>
            {
                new("c1", "d1", "chunk 1", 0)
            });

        var mockEmbeddingService = new Mock<IEmbeddingService>();
        mockEmbeddingService.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.AddChunkAsync(It.IsAny<DocumentChunk>(), It.IsAny<float[]>()))
            .Returns(Task.CompletedTask);

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.AddDocumentAsync(It.IsAny<Document>()))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(x => x.UpdateDocumentStatusAsync(It.IsAny<string>(), It.IsAny<DocumentStatus>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunkingService.Object,
            mockEmbeddingService.Object,
            mockVectorStore.Object,
            mockRepo.Object
        );

        // Act
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "fan_datasheet_en.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DocumentStatus.Processed, result.Status);
        Assert.Equal("en", result.Language);
        mockEmbeddingService.Verify(x => x.GenerateEmbeddingAsync(It.IsAny<string>()), Times.Once);
        mockVectorStore.Verify(x => x.AddChunkAsync(It.Is<DocumentChunk>(c => c.OriginalFileName == "fan_datasheet_en.pdf" && c.FileName == "fan_datasheet_en.pdf"), It.IsAny<float[]>()), Times.Once);
        mockRepo.Verify(x => x.UpdateDocumentStatusAsync(result.Id, DocumentStatus.Processed, null, "en"), Times.Once);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenNonEnglishDocument_SavesFile_SkipsEmbeddingAndVectorStore()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Dette er et dansk datablad for en ventilator med mærkespænding på 400V og tekniske målinger.");

        var mockChunkingService = new Mock<IChunkingService>();
        var mockEmbeddingService = new Mock<IEmbeddingService>();
        var mockVectorStore = new Mock<IVectorStore>();
        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.AddDocumentAsync(It.IsAny<Document>()))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(x => x.UpdateDocumentStatusAsync(It.IsAny<string>(), It.IsAny<DocumentStatus>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunkingService.Object,
            mockEmbeddingService.Object,
            mockVectorStore.Object,
            mockRepo.Object
        );

        // Act
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "Data_sheet_DA_-_8300100049.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DocumentStatus.Skipped, result.Status);
        Assert.Equal("da", result.Language);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("da", result.ErrorMessage);

        // Verify the file was saved to disk
        var uniqueFileName = Path.GetFileName(Uri.UnescapeDataString(result.FilePath));
        var savedPath = Path.Combine(AppContext.BaseDirectory, "uploads", uniqueFileName);
        Assert.True(File.Exists(savedPath), $"Expected file {savedPath} to exist on disk.");

        // Clean up file
        try { File.Delete(savedPath); } catch { }

        // Verify embeddings and vector store were NEVER called
        mockEmbeddingService.Verify(x => x.GenerateEmbeddingAsync(It.IsAny<string>()), Times.Never);
        mockVectorStore.Verify(x => x.AddChunkAsync(It.IsAny<DocumentChunk>(), It.IsAny<float[]>()), Times.Never);
        mockChunkingService.Verify(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mockRepo.Verify(x => x.UpdateDocumentStatusAsync(result.Id, DocumentStatus.Skipped, It.IsAny<string>(), "da", It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenHyphenatedFileName_ExtractsArticleIdAndDocumentId()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Technical specifications for 9293512011 fan. Nominal voltage 24V.");

        var mockChunkingService = new Mock<IChunkingService>();
        mockChunkingService.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk>
            {
                new("c1", "d1", "chunk 1", 0)
            });

        var mockEmbeddingService = new Mock<IEmbeddingService>();
        mockEmbeddingService.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.AddChunkAsync(It.IsAny<DocumentChunk>(), It.IsAny<float[]>()))
            .Returns(Task.CompletedTask);

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.AddDocumentAsync(It.IsAny<Document>()))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(x => x.UpdateDocumentStatusAsync(It.IsAny<string>(), It.IsAny<DocumentStatus>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunkingService.Object,
            mockEmbeddingService.Object,
            mockVectorStore.Object,
            mockRepo.Object
        );

        // Act
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "Art_9293512011-Doc_3258_J_2H3PU-011_PDB_EN.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("9293512011", result.ArticleId);
        Assert.Equal("3258_J_2H3PU-011_PDB_EN", result.SourceDocumentId);

        mockVectorStore.Verify(x => x.AddChunkAsync(
            It.Is<DocumentChunk>(c => c.ArticleId == "9293512011" && c.SourceDocumentId == "3258_J_2H3PU-011_PDB_EN"),
            It.IsAny<float[]>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenDocumentAlreadyExists_OverwritesOldFile_DeletesOldChunks_AndReusesDocumentId()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Updated technical specifications for the fan model.");

        var mockChunkingService = new Mock<IChunkingService>();
        mockChunkingService.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk>
            {
                new("chunk-updated-1", "existing-doc-id", "Updated chunk text", 0)
            });

        var mockEmbeddingService = new Mock<IEmbeddingService>();
        mockEmbeddingService.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f });

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.DeleteDocumentChunksAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mockVectorStore.Setup(x => x.AddChunkAsync(It.IsAny<DocumentChunk>(), It.IsAny<float[]>()))
            .Returns(Task.CompletedTask);

        // Pre-create an old physical file in uploads
        var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
        Directory.CreateDirectory(uploadsDir);
        var oldFileName = $"{Guid.NewGuid():N}_existing_fan.pdf";
        var oldFilePath = Path.Combine(uploadsDir, oldFileName);
        await File.WriteAllTextAsync(oldFilePath, "%PDF-old content");

        var existingDoc = new Document(
            Id: "existing-doc-id",
            FileName: "existing_fan.pdf",
            FilePath: $"/uploads/{oldFileName}",
            UploadedAt: DateTime.UtcNow.AddDays(-1),
            Status: DocumentStatus.Processed,
            SourceDocumentId: "Doc_3258_J"
        );

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetAllDocumentsAsync())
            .ReturnsAsync(new List<Document> { existingDoc });
        mockRepo.Setup(x => x.AddDocumentAsync(It.IsAny<Document>()))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(x => x.UpdateDocumentStatusAsync(It.IsAny<string>(), It.IsAny<DocumentStatus>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunkingService.Object,
            mockEmbeddingService.Object,
            mockVectorStore.Object,
            mockRepo.Object
        );

        // Act - upload with same filename
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "existing_fan.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("existing-doc-id", result.Id);

        // 1. Old file on disk should be deleted
        Assert.False(File.Exists(oldFilePath), "Expected previous file on disk to be deleted during overwrite.");

        // 2. Old chunks in vector store should be deleted
        mockVectorStore.Verify(x => x.DeleteDocumentChunksAsync("existing-doc-id"), Times.Once);

        // 3. New chunks should be inserted
        mockVectorStore.Verify(x => x.AddChunkAsync(It.IsAny<DocumentChunk>(), It.IsAny<float[]>()), Times.Once);

        // 4. Repository should be updated
        mockRepo.Verify(x => x.UpdateDocumentStatusAsync("existing-doc-id", DocumentStatus.Processed, null, "en"), Times.Once);

        // Clean up new file
        var newDiskName = Path.GetFileName(Uri.UnescapeDataString(result.FilePath));
        var newDiskPath = Path.Combine(uploadsDir, newDiskName);
        try { File.Delete(newDiskPath); } catch { }
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenExtractedTextIsEmpty_DoesNotThrow_SetsStatusFailedWithDescriptiveMessage()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync(string.Empty); // Scanned / image-only PDF

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.AddDocumentAsync(It.IsAny<Document>()))
            .Returns(Task.CompletedTask);
        mockRepo.Setup(x => x.UpdateDocumentStatusAsync(It.IsAny<string>(), It.IsAny<DocumentStatus>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            Mock.Of<IChunkingService>(),
            null,
            null,
            mockRepo.Object
        );

        // Act
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "scanned_image_only.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DocumentStatus.Failed, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("ingen digital tekst", result.ErrorMessage);

        mockRepo.Verify(x => x.UpdateDocumentStatusAsync(
            result.Id,
            DocumentStatus.Failed,
            It.Is<string>(msg => msg.Contains("ingen digital tekst")),
            null), Times.Once);

        // Clean up file
        var diskName = Path.GetFileName(Uri.UnescapeDataString(result.FilePath));
        var diskPath = Path.Combine(AppContext.BaseDirectory, "uploads", diskName);
        try { File.Delete(diskPath); } catch { }
    }
}
