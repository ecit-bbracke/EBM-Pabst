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
    private readonly IFullTextSearchStore? _ftsStore;
    private readonly IDocumentRepository? _documentRepository;
    private readonly ILanguageDetector _languageDetector;
    private readonly ILogger<DocumentProcessor>? _logger;

    // Constructor to support the exact Unit Test signature from prompt
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService)
        : this(textExtractor, chunkingService, null, null, null, null, null, null)
    {
    }

    // Constructor for dependency injection without language detector specified
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService,
        IEmbeddingService? embeddingService,
        IVectorStore? vectorStore,
        IDocumentRepository? documentRepository)
        : this(textExtractor, chunkingService, embeddingService, vectorStore, documentRepository, null, null, null)
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
        ILogger<DocumentProcessor>? logger = null,
        IFullTextSearchStore? ftsStore = null)
    {
        _textExtractor = textExtractor ?? throw new ArgumentNullException(nameof(textExtractor));
        _chunkingService = chunkingService ?? throw new ArgumentNullException(nameof(chunkingService));
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _documentRepository = documentRepository;
        _languageDetector = languageDetector ?? new DefaultLanguageDetector();
        _logger = logger;
        _ftsStore = ftsStore;
    }

    public async Task<Document> ProcessPdfAsync(Stream pdfStream, string fileName, string? documentId = null)
    {
        var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
        if (!Directory.Exists(uploadsDir))
        {
            Directory.CreateDirectory(uploadsDir);
        }

        var (articleId, sourceDocId) = ArticleDocumentNameParser.Parse(fileName);

        // 1. Look for existing document with same ID, SourceDocumentId, or FileName to overwrite
        Document? existingDoc = null;
        if (_documentRepository != null)
        {
            try
            {
                var allDocs = await _documentRepository.GetAllDocumentsAsync();
                existingDoc = allDocs.FirstOrDefault(d =>
                    (!string.IsNullOrWhiteSpace(documentId) && string.Equals(d.Id, documentId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(sourceDocId) && string.Equals(d.SourceDocumentId, sourceDocId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(sourceDocId) && string.Equals(d.Id, sourceDocId, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to query repository for existing documents during upload.");
            }
        }

        if (existingDoc == null && _vectorStore != null)
        {
            try
            {
                var vectorDocs = await _vectorStore.GetDocumentsAsync();
                existingDoc = vectorDocs.FirstOrDefault(d =>
                    (!string.IsNullOrWhiteSpace(documentId) && string.Equals(d.Id, documentId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(sourceDocId) && string.Equals(d.SourceDocumentId, sourceDocId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(sourceDocId) && string.Equals(d.Id, sourceDocId, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to query vector store for existing documents during upload.");
            }
        }

        var targetDocumentId = documentId
            ?? existingDoc?.Id
            ?? (!string.IsNullOrWhiteSpace(sourceDocId) ? sourceDocId : Guid.NewGuid().ToString());

        // 2. Remove old physical file if overwriting
        if (existingDoc != null && !string.IsNullOrWhiteSpace(existingDoc.FilePath))
        {
            try
            {
                var oldDiskName = Path.GetFileName(Uri.UnescapeDataString(existingDoc.FilePath));
                var oldDiskPath = Path.Combine(uploadsDir, oldDiskName);
                if (File.Exists(oldDiskPath))
                {
                    File.Delete(oldDiskPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not delete old file {FilePath} during document overwrite.", existingDoc.FilePath);
            }
        }

        // 3. Delete old chunks from vector store and FTS store if overwriting
        if (existingDoc != null)
        {
            if (_vectorStore != null)
            {
                try
                {
                    await _vectorStore.DeleteDocumentChunksAsync(existingDoc.Id);
                    if (!string.Equals(existingDoc.Id, targetDocumentId, StringComparison.OrdinalIgnoreCase))
                    {
                        await _vectorStore.DeleteDocumentChunksAsync(targetDocumentId);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete old chunks for document {DocumentId} during overwrite.", existingDoc.Id);
                }
            }

            if (_ftsStore != null)
            {
                try
                {
                    await _ftsStore.DeleteChunksByDocumentIdAsync(existingDoc.Id);
                    if (!string.Equals(existingDoc.Id, targetDocumentId, StringComparison.OrdinalIgnoreCase))
                    {
                        await _ftsStore.DeleteChunksByDocumentIdAsync(targetDocumentId);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete old FTS5 chunks for document {DocumentId} during overwrite.", existingDoc.Id);
                }
            }
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
            Id: targetDocumentId,
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

            // Extract product model information for rich catalog queries
            var ebmParser = new EbmProductCodeParser();
            var extractedProducts = ebmParser.ExtractProductsFromText(fileName);
            if (extractedProducts.Count == 0 && !string.IsNullOrWhiteSpace(extractedText))
            {
                extractedProducts = ebmParser.ExtractProductsFromText(extractedText.Substring(0, Math.Min(extractedText.Length, 3000)));
            }
            var bestProduct = extractedProducts.FirstOrDefault();
            var productCode = bestProduct?.CleanCode;
            var productCategory = bestProduct?.FanFamily.ToString();

            if (!string.IsNullOrWhiteSpace(productCode) || !string.IsNullOrWhiteSpace(productCategory))
            {
                document = document with
                {
                    ProductCode = productCode,
                    ProductCategory = productCategory
                };
            }

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                var message = $"Dokumentet '{fileName}' indeholder ingen digital tekst (f.eks. indscannet billede eller tegning uden OCR-lag) og kan ikke indekseres til AI-søgning.";
                _logger?.LogWarning("[DocumentProcessor] File '{FileName}' contains no extractable text. Setting status to Failed.", fileName);

                document = document with
                {
                    Status = DocumentStatus.Failed,
                    ErrorMessage = message
                };

                if (_documentRepository != null)
                {
                    await _documentRepository.UpdateDocumentStatusAsync(targetDocumentId, DocumentStatus.Failed, message);
                }

                return document;
            }

            // Detect language
            var languageResult = await _languageDetector.DetectLanguageAsync(extractedText, fileName);
            _logger?.LogInformation(
                "[DocumentProcessor] Language detection for '{FileName}': '{Language}' (IsEnglish: {IsEnglish}, Confidence: {Confidence:P1}).",
                fileName, languageResult.Language, languageResult.IsEnglish, languageResult.Confidence);

            if (languageResult.IsEnglish)
            {
                var chunks = _chunkingService.ChunkText(extractedText, targetDocumentId);
                var chunksWithMetaList = new List<DocumentChunk>();

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
                    chunksWithMetaList.Add(chunkWithDocumentMetadata);

                    if (_embeddingService != null && _vectorStore != null)
                    {
                        var embedding = await _embeddingService.GenerateEmbeddingAsync(chunk.Text);
                        await _vectorStore.AddChunkAsync(chunkWithDocumentMetadata, embedding);
                    }
                }

                if (_ftsStore != null && chunksWithMetaList.Count > 0)
                {
                    try
                    {
                        await _ftsStore.AddChunksAsync(chunksWithMetaList);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to index chunks into SQLite FTS5 for document {DocumentId}.", targetDocumentId);
                    }
                }

                document = document with 
                { 
                    Status = DocumentStatus.Processed,
                    Language = languageResult.Language
                };

                if (_documentRepository != null)
                {
                    await _documentRepository.UpdateDocumentStatusAsync(targetDocumentId, DocumentStatus.Processed, language: languageResult.Language, productCode: productCode, productCategory: productCategory);
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
                    ErrorMessage = message,
                    ProductCode = productCode,
                    ProductCategory = productCategory
                };

                if (_documentRepository != null)
                {
                    await _documentRepository.UpdateDocumentStatusAsync(targetDocumentId, DocumentStatus.Skipped, errorMessage: message, language: languageResult.Language, productCode: productCode, productCategory: productCategory);
                }
            }
        }
        catch (Exception ex)
        {
            document = document with { Status = DocumentStatus.Failed, ErrorMessage = ex.Message };
            if (_documentRepository != null)
            {
                await _documentRepository.UpdateDocumentStatusAsync(targetDocumentId, DocumentStatus.Failed, ex.Message);
            }
            throw;
        }

        return document;
    }
}
