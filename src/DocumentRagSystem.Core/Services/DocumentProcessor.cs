using System;
using System.IO;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Core.Services;

public class DocumentProcessor : IDocumentProcessor
{
    private readonly ITextExtractor _textExtractor;
    private readonly IChunkingService _chunkingService;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IVectorStore? _vectorStore;
    private readonly IDocumentRepository? _documentRepository;
    private readonly ILanguageDetector _languageDetector;
    private readonly ILogger<DocumentProcessor>? _logger;

    // Constructor to support the exact Unit Test signature from prompt
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService)
        : this(textExtractor, chunkingService, null, null, null, null, null)
    {
    }

    // Constructor for dependency injection without language detector specified
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService,
        IEmbeddingService? embeddingService,
        IVectorStore? vectorStore,
        IDocumentRepository? documentRepository)
        : this(textExtractor, chunkingService, embeddingService, vectorStore, documentRepository, null, null)
    {
    }

    // Full constructor for dependency injection
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService,
        IEmbeddingService? embeddingService,
        IVectorStore? vectorStore,
        IDocumentRepository? documentRepository,
        ILanguageDetector? languageDetector,
        ILogger<DocumentProcessor>? logger = null)
    {
        _textExtractor = textExtractor ?? throw new ArgumentNullException(nameof(textExtractor));
        _chunkingService = chunkingService ?? throw new ArgumentNullException(nameof(chunkingService));
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _documentRepository = documentRepository;
        _languageDetector = languageDetector ?? new DefaultLanguageDetector();
        _logger = logger;
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

        var (articleId, sourceDocId) = ArticleDocumentNameParser.Parse(fileName);

        var document = new Document(
            Id: documentId,
            FileName: fileName,
            FilePath: documentUrl,
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processing,
            ArticleId: articleId,
            SourceDocumentId: sourceDocId
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

            // Detect language
            var languageResult = await _languageDetector.DetectLanguageAsync(extractedText, fileName);
            _logger?.LogInformation(
                "[DocumentProcessor] Language detection for '{FileName}': '{Language}' (IsEnglish: {IsEnglish}, Confidence: {Confidence:P1}).",
                fileName, languageResult.Language, languageResult.IsEnglish, languageResult.Confidence);

            if (languageResult.IsEnglish)
            {
                var chunks = _chunkingService.ChunkText(extractedText, documentId);

                if (_embeddingService != null && _vectorStore != null)
                {
                    foreach (var chunk in chunks)
                    {
                        var chunkWithDocumentMetadata = chunk with
                        {
                            FileName = document.FileName,
                            OriginalFileName = document.FileName,
                            FilePath = document.FilePath,
                            UploadedAt = document.UploadedAt,
                            ArticleId = document.ArticleId,
                            SourceDocumentId = document.SourceDocumentId
                        };
                        var embedding = await _embeddingService.GenerateEmbeddingAsync(chunk.Text);
                        await _vectorStore.AddChunkAsync(chunkWithDocumentMetadata, embedding);
                    }
                }

                document = document with 
                { 
                    Status = DocumentStatus.Processed,
                    Language = languageResult.Language
                };

                if (_documentRepository != null)
                {
                    await _documentRepository.UpdateDocumentStatusAsync(documentId, DocumentStatus.Processed, language: languageResult.Language);
                }
            }
            else
            {
                var message = $"Document saved to disk, but embedding was skipped because detected language is '{languageResult.Language}' (only English documents are embedded).";
                _logger?.LogInformation(
                    "[DocumentProcessor] File '{FileName}' was saved to '{FilePath}', but skipped from vector database because language is '{Language}'.",
                    fileName, filePath, languageResult.Language);

                document = document with
                {
                    Status = DocumentStatus.Skipped,
                    Language = languageResult.Language,
                    ErrorMessage = message
                };

                if (_documentRepository != null)
                {
                    await _documentRepository.UpdateDocumentStatusAsync(documentId, DocumentStatus.Skipped, errorMessage: message, language: languageResult.Language);
                }
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
