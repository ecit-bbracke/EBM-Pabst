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
        mockVectorStore.Verify(x => x.AddChunksAsync(It.Is<IEnumerable<(DocumentChunk Chunk, float[] Embedding)>>(list =>
            list.Any(item => item.Chunk.OriginalFileName == "fan_datasheet_en.pdf" && item.Chunk.FileName == "fan_datasheet_en.pdf"))), Times.Once);
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

        mockVectorStore.Verify(x => x.AddChunksAsync(
            It.Is<IEnumerable<(DocumentChunk Chunk, float[] Embedding)>>(list =>
                list.Any(c => c.Chunk.ArticleId == "9293512011" && c.Chunk.SourceDocumentId == "3258_J_2H3PU-011_PDB_EN"))), Times.Once);
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
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("existing_fan.pdf"))
            .ReturnsAsync(existingDoc);
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

        // 3. New chunks should be inserted in batch
        mockVectorStore.Verify(x => x.AddChunksAsync(It.IsAny<IEnumerable<(DocumentChunk Chunk, float[] Embedding)>>()), Times.Once);

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

    [Fact]
    public async Task ProcessPdfAsync_UsesIndexedQueries_AndNeverCallsGetAllDocumentsAsync()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Test content");

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("new_fan.pdf"))
            .ReturnsAsync((Document?)null);
        mockRepo.Setup(x => x.GetAllDocumentsAsync())
            .Throws(new InvalidOperationException("GetAllDocumentsAsync must NEVER be called during upload! Full table scan forbidden."));
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
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "new_fan.pdf");

        // Assert
        Assert.NotNull(result);
        mockRepo.Verify(x => x.GetDocumentByFileNameAsync("new_fan.pdf"), Times.Once);
        mockRepo.Verify(x => x.GetAllDocumentsAsync(), Times.Never);

        // Clean up file
        var diskName = Path.GetFileName(Uri.UnescapeDataString(result.FilePath));
        var diskPath = Path.Combine(AppContext.BaseDirectory, "uploads", diskName);
        try { File.Delete(diskPath); } catch { }
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenMultipleChunks_GeneratesEmbeddingsInBatch_AndUpsertsBatchToVectorStore()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Technical specifications for ebm-papst fan. Nominal voltage 24V. Airflow 500 m3/h.");

        // Create 15 chunks
        var chunks = new List<DocumentChunk>();
        for (int i = 0; i < 15; i++)
        {
            chunks.Add(new DocumentChunk($"c_{i}", "doc-1", $"chunk text {i}", i));
        }

        var mockChunkingService = new Mock<IChunkingService>();
        mockChunkingService.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(chunks);

        var mockEmbeddingService = new Mock<IEmbeddingService>();
        mockEmbeddingService.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<IList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<string> texts, CancellationToken _) =>
            {
                return texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList();
            });

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.AddChunksAsync(It.IsAny<IEnumerable<(DocumentChunk Chunk, float[] Embedding)>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync(It.IsAny<string>()))
            .ReturnsAsync((Document?)null);
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
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "fan_with_many_chunks.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DocumentStatus.Processed, result.Status);

        // 1. All 15 chunks must be passed to GenerateEmbeddingsAsync in a single batch call
        mockEmbeddingService.Verify(x => x.GenerateEmbeddingsAsync(It.Is<IList<string>>(l => l.Count == 15), It.IsAny<CancellationToken>()), Times.Once);

        // 2. All 15 chunks must be upserted to vector store in a single batch call
        mockVectorStore.Verify(x => x.AddChunksAsync(It.Is<IEnumerable<(DocumentChunk Chunk, float[] Embedding)>>(l => l.Count() == 15), It.IsAny<CancellationToken>()), Times.Once);

        // Clean up file
        var diskName = Path.GetFileName(Uri.UnescapeDataString(result.FilePath));
        var diskPath = Path.Combine(AppContext.BaseDirectory, "uploads", diskName);
        try { File.Delete(diskPath); } catch { }
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenReplacementExtractionFails_PreservesExistingDocumentMetadata()
    {
        // Arrange: Existing document exists
        var existingDoc = new Document(
            Id: "existing-doc-99",
            FileName: "good_fan.pdf",
            FilePath: "/uploads/good_fan.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-1),
            Status: DocumentStatus.Processed,
            Language: "en"
        );

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync(string.Empty); // Text extraction returns empty!

        var mockChunkingService = new Mock<IChunkingService>();
        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("good_fan.pdf"))
            .ReturnsAsync(existingDoc);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunkingService.Object,
            null,
            null,
            mockRepo.Object
        );

        // Act
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2 }), "good_fan.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DocumentStatus.Failed, result.Status);

        // Crucial: The repository record for the existing document must NOT be marked Failed or overwritten with staged path
        mockRepo.Verify(x => x.UpdateDocumentStatusAsync(existingDoc.Id, DocumentStatus.Failed, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mockRepo.Verify(x => x.AddDocumentAsync(It.Is<Document>(d => d.Status == DocumentStatus.Processing)), Times.Never);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenNonEnglishReplacement_DeletesOldVectorAndFtsChunks()
    {
        // Arrange: Existing English document replaced with Danish version
        var existingDoc = new Document(
            Id: "doc-to-replace",
            FileName: "fan_to_replace.pdf",
            FilePath: "/uploads/fan_to_replace.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-1),
            Status: DocumentStatus.Processed,
            Language: "en"
        );

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Dette er en dansk brugermanual for ventilatoren.");

        var mockChunkingService = new Mock<IChunkingService>();
        var mockLanguageDetector = new Mock<ILanguageDetector>();
        mockLanguageDetector.Setup(x => x.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new LanguageDetectionResult("da", false, 0.95));

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("fan_to_replace.pdf"))
            .ReturnsAsync(existingDoc);

        var mockVectorStore = new Mock<IVectorStore>();
        var mockFtsStore = new Mock<IFullTextSearchStore>();

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunkingService.Object,
            null,
            mockVectorStore.Object,
            mockRepo.Object,
            mockLanguageDetector.Object,
            null,
            mockFtsStore.Object
        );

        // Act
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2 }), "fan_to_replace.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DocumentStatus.Skipped, result.Status);

        // Both vector chunks and FTS chunks from previous document must be deleted
        mockVectorStore.Verify(x => x.DeleteDocumentChunksAsync("doc-to-replace", It.IsAny<CancellationToken>()), Times.Once);
        mockFtsStore.Verify(x => x.DeleteChunksByDocumentIdAsync("doc-to-replace", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenPendingPlaceholderFailsTextExtraction_TransitionsStatusToFailedInRepository()
    {
        // Arrange: Background upload created a Pending placeholder
        var pendingDoc = new Document(
            Id: "pending-bg-doc-123",
            FileName: "corrupt_upload.pdf",
            FilePath: "Queued/Background",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Pending
        );

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync(string.Empty); // Corrupted / empty PDF

        var mockChunkingService = new Mock<IChunkingService>();
        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByIdAsync("pending-bg-doc-123"))
            .ReturnsAsync(pendingDoc);
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("corrupt_upload.pdf"))
            .ReturnsAsync(pendingDoc);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunkingService.Object,
            null,
            null,
            mockRepo.Object
        );

        // Act
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2 }), "corrupt_upload.pdf", "pending-bg-doc-123");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DocumentStatus.Failed, result.Status);

        // Must transition the pending placeholder to Failed in the repository
        mockRepo.Verify(x => x.UpdateDocumentStatusAsync(
            "pending-bg-doc-123",
            DocumentStatus.Failed,
            It.Is<string>(msg => msg.Contains("indeholder ingen digital tekst")),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        ), Times.Once);
    }

    [Fact]
    public async Task ProcessPdfAsync_ConcurrentUploads_TargetingSameDocument_AreSerializedByLock()
    {
        // Arrange
        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Sample technical text for serialization verification");

        var mockChunker = new Mock<IChunkingService>();
        mockChunker.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk> { new("c1", "doc-same", "Sample text", 0) });

        var mockRepo = new Mock<IDocumentRepository>();
        int activeExecutions = 0;
        int maxConcurrency = 0;

        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("concurrent_doc.pdf"))
            .Returns(async () =>
            {
                var current = Interlocked.Increment(ref activeExecutions);
                lock (mockRepo)
                {
                    if (current > maxConcurrency) maxConcurrency = current;
                }
                await Task.Delay(50);
                Interlocked.Decrement(ref activeExecutions);
                return null;
            });

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunker.Object,
            null,
            null,
            mockRepo.Object
        );

        // Act: Start two concurrent uploads targeting the exact same document
        var task1 = processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1 }), "concurrent_doc.pdf");
        var task2 = processor.ProcessPdfAsync(new MemoryStream(new byte[] { 2 }), "concurrent_doc.pdf");

        await Task.WhenAll(task1, task2);

        // Assert: Concurrency for the same document key was strictly serialized (maxConcurrency == 1)
        Assert.Equal(1, maxConcurrency);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenExistingDocumentFoundInsideLock_ReusesCanonicalIdInsteadOfPreallocatedId()
    {
        // Arrange
        var existingDoc = new Document(
            Id: "canonical-doc-id-999",
            FileName: "concurrent_target.pdf",
            FilePath: "/uploads/old_target.pdf",
            UploadedAt: DateTime.UtcNow.AddHours(-1),
            Status: DocumentStatus.Processed
        );

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Sample technical text for document");

        var mockChunker = new Mock<IChunkingService>();
        mockChunker.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk> { new("c1", "canonical-doc-id-999", "Sample text", 0) });

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float[]> { new float[] { 0.1f, 0.2f } });

        var mockVector = new Mock<IVectorStore>();
        mockVector.Setup(x => x.GetChunksByDocumentAndIndicesAsync("canonical-doc-id-999", It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk>());

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("concurrent_target.pdf"))
            .ReturnsAsync(existingDoc);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunker.Object,
            mockEmbedding.Object,
            mockVector.Object,
            mockRepo.Object
        );

        // Act: Endpoint passed a preallocated new GUID "preallocated-ephemeral-id"
        var result = await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2 }), "concurrent_target.pdf", "preallocated-ephemeral-id");

        // Assert: It must reuse the canonical ID "canonical-doc-id-999" and NOT create a new record with "preallocated-ephemeral-id"
        Assert.NotNull(result);
        Assert.Equal("canonical-doc-id-999", result.Id);
        mockRepo.Verify(x => x.UpdateDocumentStatusAsync("canonical-doc-id-999", DocumentStatus.Processed, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        mockRepo.Verify(x => x.AddDocumentAsync(It.Is<Document>(d => d.Id == "preallocated-ephemeral-id")), Times.Never);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenReplacementFails_PreservesValidOriginalDocumentStatus()
    {
        // Arrange: Existing document is Processed and valid
        var existingDoc = new Document(
            Id: "valid-original-doc-777",
            FileName: "valid_fan.pdf",
            FilePath: "/uploads/valid_fan.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-2),
            Status: DocumentStatus.Processed
        );

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("New replacement content");

        var mockChunker = new Mock<IChunkingService>();
        mockChunker.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk> { new("c1", "valid-original-doc-777", "New text", 0) });

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float[]> { new float[] { 0.1f, 0.2f } });

        var mockVector = new Mock<IVectorStore>();
        mockVector.Setup(x => x.GetChunksByDocumentAndIndicesAsync("valid-original-doc-777", It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk>());
        // Vector store fails during replacement upsert
        mockVector.Setup(x => x.AddChunksAsync(It.IsAny<IEnumerable<(DocumentChunk, float[])>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Qdrant connection dropped during upsert"));

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("valid_fan.pdf"))
            .ReturnsAsync(existingDoc);
        mockRepo.Setup(x => x.GetDocumentByIdAsync("valid-original-doc-777"))
            .ReturnsAsync(existingDoc);

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunker.Object,
            mockEmbedding.Object,
            mockVector.Object,
            mockRepo.Object
        );

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1, 2 }), "valid_fan.pdf", "valid-original-doc-777");
        });

        // The valid original document must NOT be marked Failed in the repository!
        mockRepo.Verify(x => x.UpdateDocumentStatusAsync(
            "valid-original-doc-777",
            DocumentStatus.Failed,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        ), Times.Never);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenDocumentExceeds1000Chunks_CapturesAndRestoresAllChunksOnFailure()
    {
        // Arrange: Document with 1,005 chunks
        var existingDoc = new Document(
            Id: "large-doc-1005",
            FileName: "large_manual.pdf",
            FilePath: "/uploads/large_manual.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-1),
            Status: DocumentStatus.Processed
        );

        var originalChunks = Enumerable.Range(0, 1005)
            .Select(i => new DocumentChunk($"c_{i}", "large-doc-1005", $"Content of chunk {i}", i))
            .ToList();

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("New content for large document");

        var mockChunker = new Mock<IChunkingService>();
        mockChunker.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk> { new("new_1", "large-doc-1005", "New content", 0) });

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<string> texts, CancellationToken _) => texts.Select(_ => new float[] { 0.1f }).ToList());

        var mockVector = new Mock<IVectorStore>();
        mockVector.Setup(x => x.GetAllChunksByDocumentIdAsync("large-doc-1005", It.IsAny<CancellationToken>()))
            .ReturnsAsync(originalChunks);

        IEnumerable<(DocumentChunk Chunk, float[] Embedding)>? restoredSnapshot = null;
        mockVector.Setup(x => x.AddChunksAsync(It.IsAny<IEnumerable<(DocumentChunk, float[])>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<(DocumentChunk Chunk, float[] Embedding)>, CancellationToken>((chunks, token) =>
            {
                var list = chunks.ToList();
                // If it's the replacement upsert (1 chunk), fail it!
                if (list.Count == 1)
                {
                    throw new InvalidOperationException("Simulated insertion failure");
                }
                // Otherwise it's the rollback restoration:
                restoredSnapshot = list;
                return Task.CompletedTask;
            });

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("large_manual.pdf")).ReturnsAsync(existingDoc);
        mockRepo.Setup(x => x.GetDocumentByIdAsync("large-doc-1005")).ReturnsAsync(existingDoc);

        var processor = new DocumentProcessor(mockTextExtractor.Object, mockChunker.Object, mockEmbedding.Object, mockVector.Object, mockRepo.Object);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1 }), "large_manual.pdf", "large-doc-1005");
        });

        // Verification: Rollback restored ALL 1,005 chunks (not just 1,000)!
        Assert.NotNull(restoredSnapshot);
        Assert.Equal(1005, restoredSnapshot!.Count());
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenMetadataUpdateFailsAfterVectorUpsert_RestoresOriginalVectors()
    {
        // Arrange
        var existingDoc = new Document(
            Id: "doc-meta-fail",
            FileName: "meta_fail.pdf",
            FilePath: "/uploads/meta_fail.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-1),
            Status: DocumentStatus.Processed
        );

        var originalChunks = new List<DocumentChunk>
        {
            new("orig_1", "doc-meta-fail", "Original text 1", 0)
        };

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("New replacement content");

        var mockChunker = new Mock<IChunkingService>();
        mockChunker.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk> { new("new_1", "doc-meta-fail", "New replacement text", 0) });

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<string> texts, CancellationToken _) => texts.Select(_ => new float[] { 0.5f }).ToList());

        var mockVector = new Mock<IVectorStore>();
        mockVector.Setup(x => x.GetAllChunksByDocumentIdAsync("doc-meta-fail", It.IsAny<CancellationToken>()))
            .ReturnsAsync(originalChunks);

        bool vectorRestored = false;
        mockVector.Setup(x => x.AddChunksAsync(It.IsAny<IEnumerable<(DocumentChunk, float[])>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<(DocumentChunk Chunk, float[] Embedding)>, CancellationToken>((chunks, token) =>
            {
                var list = chunks.ToList();
                if (list.Any(c => c.Chunk.Id == "orig_1"))
                {
                    vectorRestored = true;
                }
                return Task.CompletedTask;
            });

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("meta_fail.pdf")).ReturnsAsync(existingDoc);
        mockRepo.Setup(x => x.GetDocumentByIdAsync("doc-meta-fail")).ReturnsAsync(existingDoc);
        // Metadata update throws exception!
        mockRepo.Setup(x => x.UpdateDocumentStatusAsync("doc-meta-fail", DocumentStatus.Processed, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("SQLite metadata write failed"));

        var processor = new DocumentProcessor(mockTextExtractor.Object, mockChunker.Object, mockEmbedding.Object, mockVector.Object, mockRepo.Object);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1 }), "meta_fail.pdf", "doc-meta-fail");
        });

        // Verification: Extended rollback executed and restored the original vectors and original metadata!
        Assert.True(vectorRestored);
        mockVector.Verify(x => x.DeleteDocumentChunksAsync("doc-meta-fail", It.IsAny<CancellationToken>()), Times.AtLeast(2)); // Deleted before upsert and deleted before restoration
        mockRepo.Verify(x => x.AddDocumentAsync(existingDoc), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenNonEnglishReplacementMetadataWriteFails_RestoresOriginalVectorsAndMetadata()
    {
        // Arrange
        var existingDoc = new Document(
            Id: "doc-non-english-fail",
            FileName: "danish_fail.pdf",
            FilePath: "/uploads/orig_english.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-2),
            Status: DocumentStatus.Processed
        );

        var originalChunks = new List<DocumentChunk>
        {
            new("orig_chunk_da", "doc-non-english-fail", "Original English text", 0)
        };

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Dette er en dansk tekst til dokumentet");

        var mockChunker = new Mock<IChunkingService>();
        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float[]> { new float[] { 0.7f } });

        var mockVector = new Mock<IVectorStore>();
        mockVector.Setup(x => x.GetAllChunksByDocumentIdAsync("doc-non-english-fail", It.IsAny<CancellationToken>()))
            .ReturnsAsync(originalChunks);

        bool vectorRestored = false;
        mockVector.Setup(x => x.AddChunksAsync(It.IsAny<IEnumerable<(DocumentChunk, float[])>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<(DocumentChunk Chunk, float[] Embedding)>, CancellationToken>((chunks, token) =>
            {
                var list = chunks.ToList();
                if (list.Any(c => c.Chunk.Id == "orig_chunk_da"))
                {
                    vectorRestored = true;
                }
                return Task.CompletedTask;
            });

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("danish_fail.pdf")).ReturnsAsync(existingDoc);
        mockRepo.Setup(x => x.GetDocumentByIdAsync("doc-non-english-fail")).ReturnsAsync(existingDoc);
        // Metadata write throws exception in non-English branch!
        mockRepo.Setup(x => x.AddDocumentAsync(It.Is<Document>(d => d.Status == DocumentStatus.Skipped)))
            .ThrowsAsync(new InvalidOperationException("Disk full on SQLite metadata write"));

        var mockLanguageDetector = new Mock<ILanguageDetector>();
        mockLanguageDetector.Setup(x => x.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new LanguageDetectionResult("da", false, 0.99));

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunker.Object,
            mockEmbedding.Object,
            mockVector.Object,
            mockRepo.Object,
            mockLanguageDetector.Object
        );

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1 }), "danish_fail.pdf", "doc-non-english-fail");
        });

        // Verification: The original vectors and original metadata are restored!
        Assert.True(vectorRestored);
        mockRepo.Verify(x => x.AddDocumentAsync(existingDoc), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenNonEnglishReplacementIndexDeletionFails_ThrowsExceptionAndAborts()
    {
        // Arrange
        var uploadsDir = Path.Combine(Path.GetTempPath(), $"uploads_{Guid.NewGuid():N}");
        Directory.CreateDirectory(uploadsDir);
        var originalPdfPath = Path.Combine(uploadsDir, "orig_dan_target.pdf");
        await File.WriteAllTextAsync(originalPdfPath, "%PDF-1.4 original english pdf");

        try
        {
            var existingDoc = new Document(
                Id: "doc-del-fail",
                FileName: "danish_del_fail.pdf",
                FilePath: $"/uploads/{Path.GetFileName(originalPdfPath)}",
                UploadedAt: DateTime.UtcNow.AddDays(-2),
                Status: DocumentStatus.Processed
            );

            var originalChunks = new List<DocumentChunk>
            {
                new("orig_chunk_1", "doc-del-fail", "Original English text", 0)
            };

            var mockTextExtractor = new Mock<ITextExtractor>();
            mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
                .ReturnsAsync("Dette er en dansk tekst");

            var mockChunker = new Mock<IChunkingService>();
            var mockEmbedding = new Mock<IEmbeddingService>();
            mockEmbedding.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<float[]> { new float[] { 0.9f } });

            var mockVector = new Mock<IVectorStore>();
            mockVector.Setup(x => x.GetAllChunksByDocumentIdAsync("doc-del-fail", It.IsAny<CancellationToken>()))
                .ReturnsAsync(originalChunks);
            // Qdrant fails during index deletion!
            mockVector.Setup(x => x.DeleteDocumentChunksAsync("doc-del-fail", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Qdrant connection dropped during deletion"));

            var mockRepo = new Mock<IDocumentRepository>();
            mockRepo.Setup(x => x.GetDocumentByFileNameAsync("danish_del_fail.pdf")).ReturnsAsync(existingDoc);
            mockRepo.Setup(x => x.GetDocumentByIdAsync("doc-del-fail")).ReturnsAsync(existingDoc);

            var mockLanguageDetector = new Mock<ILanguageDetector>();
            mockLanguageDetector.Setup(x => x.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new LanguageDetectionResult("da", false, 0.99));

            var processor = new DocumentProcessor(
                mockTextExtractor.Object,
                mockChunker.Object,
                mockEmbedding.Object,
                mockVector.Object,
                mockRepo.Object,
                mockLanguageDetector.Object,
                uploadsDirectory: uploadsDir
            );

            // Act & Assert: Must NOT swallow the index deletion failure!
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1 }), "danish_del_fail.pdf", "doc-del-fail");
            });

            // The original physical PDF must NOT be deleted!
            Assert.True(File.Exists(originalPdfPath));

            // The document must NOT be updated to Skipped in the repository!
            mockRepo.Verify(x => x.UpdateDocumentStatusAsync("doc-del-fail", DocumentStatus.Skipped, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(uploadsDir))
            {
                try { Directory.Delete(uploadsDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ProcessPdfAsync_WhenSkippedDocumentReplacedAndMetadataFails_ClearsNewlyWrittenVectorsAndFtsChunks()
    {
        // Arrange: Existing document was Skipped (empty vector index)
        var existingDoc = new Document(
            Id: "doc-skipped-orig",
            FileName: "danish_was_skipped.pdf",
            FilePath: "/uploads/danish_was_skipped.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-3),
            Status: DocumentStatus.Skipped
        );

        var mockTextExtractor = new Mock<ITextExtractor>();
        mockTextExtractor.Setup(x => x.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync("Now replacing with valid English content for technical specifications");

        var mockChunker = new Mock<IChunkingService>();
        mockChunker.Setup(x => x.ChunkText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new List<DocumentChunk> { new("chunk_new_1", "doc-skipped-orig", "English text", 0) });

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float[]> { new float[] { 0.4f, 0.5f } });

        var mockVector = new Mock<IVectorStore>();
        // Original skipped document had NO chunks in vector store!
        mockVector.Setup(x => x.GetAllChunksByDocumentIdAsync("doc-skipped-orig", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk>());

        int deleteCalls = 0;
        mockVector.Setup(x => x.DeleteDocumentChunksAsync("doc-skipped-orig", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                deleteCalls++;
                return Task.CompletedTask;
            });

        var mockRepo = new Mock<IDocumentRepository>();
        mockRepo.Setup(x => x.GetDocumentByFileNameAsync("danish_was_skipped.pdf")).ReturnsAsync(existingDoc);
        mockRepo.Setup(x => x.GetDocumentByIdAsync("doc-skipped-orig")).ReturnsAsync(existingDoc);
        // Metadata write throws exception after vector insertion!
        mockRepo.Setup(x => x.AddDocumentAsync(It.Is<Document>(d => d.Status == DocumentStatus.Processed)))
            .ThrowsAsync(new InvalidOperationException("SQLite constraint failure on metadata write"));

        var mockLanguageDetector = new Mock<ILanguageDetector>();
        mockLanguageDetector.Setup(x => x.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new LanguageDetectionResult("en", true, 0.99));

        var processor = new DocumentProcessor(
            mockTextExtractor.Object,
            mockChunker.Object,
            mockEmbedding.Object,
            mockVector.Object,
            mockRepo.Object,
            mockLanguageDetector.Object
        );

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await processor.ProcessPdfAsync(new MemoryStream(new byte[] { 1 }), "danish_was_skipped.pdf", "doc-skipped-orig");
        });

        // Verification: Even though original snapshot was empty, DeleteDocumentChunksAsync was called on rollback to clear the newly written vectors!
        Assert.True(deleteCalls >= 1);
        mockRepo.Verify(x => x.AddDocumentAsync(existingDoc), Times.AtLeastOnce);
    }
}
