using System;
using System.IO;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public class DocumentProcessor : IDocumentProcessor
{
    private readonly ITextExtractor _textExtractor;
    private readonly IChunkingService _chunkingService;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IVectorStore? _vectorStore;
    private readonly IDocumentRepository? _documentRepository;

    // Constructor to support the exact Unit Test signature from prompt
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService)
        : this(textExtractor, chunkingService, null, null, null)
    {
    }

    // Full constructor for dependency injection
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService,
        IEmbeddingService? embeddingService,
        IVectorStore? vectorStore,
        IDocumentRepository? documentRepository)
    {
        _textExtractor = textExtractor ?? throw new ArgumentNullException(nameof(textExtractor));
        _chunkingService = chunkingService ?? throw new ArgumentNullException(nameof(chunkingService));
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _documentRepository = documentRepository;
    }

    public async Task<Document> ProcessPdfAsync(Stream pdfStream, string fileName)
    {
        var documentId = Guid.NewGuid().ToString();
        var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
        if (!Directory.Exists(uploadsDir))
        {
            Directory.CreateDirectory(uploadsDir);
        }

        var safeFileName = Path.GetFileName(fileName);
        var uniqueFileName = $"{Guid.NewGuid()}_{safeFileName}";
        var filePath = Path.Combine(uploadsDir, uniqueFileName);
        var documentUrl = $"/uploads/{Uri.EscapeDataString(uniqueFileName)}";

        // Copy stream to file
        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
            await pdfStream.CopyToAsync(fileStream);
        }

        var document = new Document(
            Id: documentId,
            FileName: fileName,
            FilePath: documentUrl,
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processing
        );

        if (_documentRepository != null)
        {
            await _documentRepository.AddDocumentAsync(document);
        }

        try
        {
            // Reset position for extraction
            using var readStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var extractedText = await _textExtractor.ExtractTextAsync(readStream);

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                throw new InvalidOperationException("Extracted text was empty.");
            }

            var chunks = _chunkingService.ChunkText(extractedText, documentId);

            if (_embeddingService != null && _vectorStore != null)
            {
                foreach (var chunk in chunks)
                {
                    var chunkWithDocumentMetadata = chunk with
                    {
                        FileName = document.FileName,
                        FilePath = document.FilePath,
                        UploadedAt = document.UploadedAt
                    };
                    var embedding = await _embeddingService.GenerateEmbeddingAsync(chunk.Text);
                    await _vectorStore.AddChunkAsync(chunkWithDocumentMetadata, embedding);
                }
            }

            document = document with { Status = DocumentStatus.Processed };
            if (_documentRepository != null)
            {
                await _documentRepository.UpdateDocumentStatusAsync(documentId, DocumentStatus.Processed);
            }
        }
        catch (Exception ex)
        {
            document = document with { Status = DocumentStatus.Failed, ErrorMessage = ex.Message };
            if (_documentRepository != null)
            {
                await _documentRepository.UpdateDocumentStatusAsync(documentId, DocumentStatus.Failed, ex.Message);
            }
            throw;
        }

        return document;
    }
}
