using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    private readonly string _uploadsDirectory;

    // Constructor to support the exact Unit Test signature from prompt
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService)
        : this(textExtractor, chunkingService, null, null, null, null, null, null, null)
    {
    }

    // Constructor for dependency injection without language detector specified
    public DocumentProcessor(
        ITextExtractor textExtractor,
        IChunkingService chunkingService,
        IEmbeddingService? embeddingService,
        IVectorStore? vectorStore,
        IDocumentRepository? documentRepository)
        : this(textExtractor, chunkingService, embeddingService, vectorStore, documentRepository, null, null, null, null)
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
        IFullTextSearchStore? ftsStore = null,
        string? uploadsDirectory = null)
    {
        _textExtractor = textExtractor ?? throw new ArgumentNullException(nameof(textExtractor));
        _chunkingService = chunkingService ?? throw new ArgumentNullException(nameof(chunkingService));
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _documentRepository = documentRepository;
        _languageDetector = languageDetector ?? new DefaultLanguageDetector();
        _logger = logger;
        _ftsStore = ftsStore;
        _uploadsDirectory = !string.IsNullOrWhiteSpace(uploadsDirectory)
            ? uploadsDirectory
            : Path.Combine(AppContext.BaseDirectory, "uploads");
    }

    public async Task<Document> ProcessPdfAsync(Stream pdfStream, string fileName, string? documentId = null, CancellationToken cancellationToken = default)
    {
        var uploadsDir = _uploadsDirectory;
        if (!Directory.Exists(uploadsDir))
        {
            Directory.CreateDirectory(uploadsDir);
        }

        var (articleId, sourceDocId) = ArticleDocumentNameParser.Parse(fileName);

        // 1. Look for existing document with same ID, SourceDocumentId, or FileName to overwrite using fast indexed queries
        Document? existingDoc = null;
        if (_documentRepository != null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(documentId))
                {
                    existingDoc = await _documentRepository.GetDocumentByIdAsync(documentId);
                }

                if (existingDoc == null && !string.IsNullOrWhiteSpace(sourceDocId))
                {
                    existingDoc = await _documentRepository.GetDocumentBySourceDocIdAsync(sourceDocId);
                }

                if (existingDoc == null && !string.IsNullOrWhiteSpace(fileName))
                {
                    existingDoc = await _documentRepository.GetDocumentByFileNameAsync(fileName);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to query repository for existing documents during upload.");
            }
        }
        else if (_vectorStore != null)
        {
            try
            {
                var vectorDocs = await _vectorStore.GetDocumentsAsync(100);
                existingDoc = vectorDocs?.FirstOrDefault(d =>
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

        bool isOverwritingCompletedDoc = existingDoc != null && (existingDoc.Status == DocumentStatus.Processed || existingDoc.Status == DocumentStatus.Skipped);

        var safeFileName = Path.GetFileName(fileName);
        var uniqueFileName = $"{Guid.NewGuid()}_{safeFileName}";
        var filePath = Path.Combine(uploadsDir, uniqueFileName);
        var documentUrl = $"/uploads/{Uri.EscapeDataString(uniqueFileName)}";

        // Copy stream to staged file first
        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
            await pdfStream.CopyToAsync(fileStream, cancellationToken);
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

        if (_documentRepository != null && !isOverwritingCompletedDoc)
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

                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch { }

                var failedDoc = document with
                {
                    Status = DocumentStatus.Failed,
                    ErrorMessage = message
                };

                if (_documentRepository != null && !isOverwritingCompletedDoc)
                {
                    await _documentRepository.UpdateDocumentStatusAsync(targetDocumentId, DocumentStatus.Failed, message);
                }

                return failedDoc;
            }

            // Detect language
            var languageResult = await _languageDetector.DetectLanguageAsync(extractedText, fileName);
            _logger?.LogInformation(
                "[DocumentProcessor] Language detection for '{FileName}': '{Language}' (IsEnglish: {IsEnglish}, Confidence: {Confidence:P1}).",
                fileName, languageResult.Language, languageResult.IsEnglish, languageResult.Confidence);

            if (languageResult.IsEnglish)
            {
                var rawChunks = _chunkingService.ChunkText(extractedText, targetDocumentId).ToList();
                var chunksWithMetaList = rawChunks.Select(chunk => chunk with
                {
                    FileName = document.FileName,
                    OriginalFileName = document.FileName,
                    FilePath = document.FilePath,
                    UploadedAt = document.UploadedAt,
                    ArticleId = document.ArticleId,
                    SourceDocumentId = document.SourceDocumentId
                }).ToList();

                if (_embeddingService != null && _vectorStore != null && chunksWithMetaList.Count > 0)
                {
                    // Generate embeddings in batch via batchEmbedContents
                    var texts = chunksWithMetaList.Select(c => c.Text).ToList();
                    IList<float[]>? embeddings = null;
                    try
                    {
                        embeddings = await _embeddingService.GenerateEmbeddingsAsync(texts, cancellationToken);
                    }
                    catch
                    {
                        // Fallback to sequential GenerateEmbeddingAsync
                    }

                    if (embeddings == null || embeddings.Count == 0 || embeddings.Any(e => e == null || e.Length == 0))
                    {
                        var fallbackList = new List<float[]>(texts.Count);
                        foreach (var text in texts)
                        {
                            var emb = await _embeddingService.GenerateEmbeddingAsync(text, cancellationToken);
                            if (emb == null || emb.Length == 0)
                            {
                                emb = await _embeddingService.GenerateEmbeddingAsync(text);
                            }
                            fallbackList.Add((emb != null && emb.Length > 0) ? emb : new float[768]);
                        }
                        embeddings = fallbackList;
                    }

                    var chunksWithEmbeddings = new List<(DocumentChunk Chunk, float[] Embedding)>(chunksWithMetaList.Count);
                    for (int i = 0; i < chunksWithMetaList.Count && i < embeddings.Count; i++)
                    {
                        chunksWithEmbeddings.Add((chunksWithMetaList[i], embeddings[i]));
                    }

                    // Snapshot existing vectors before replacement to ensure rollback restoration on failure
                    List<(DocumentChunk Chunk, float[] Embedding)>? existingVectorSnapshot = null;
                    if (isOverwritingCompletedDoc && _vectorStore != null && _embeddingService != null)
                    {
                        var oldChunks = (await _vectorStore.GetChunksByDocumentAndIndicesAsync(existingDoc!.Id, Enumerable.Range(0, 1000), cancellationToken))?.ToList();
                        if (oldChunks != null && oldChunks.Count > 0)
                        {
                            var oldTexts = oldChunks.Select(c => c.Text).ToList();
                            var oldEmbeddings = await _embeddingService.GenerateEmbeddingsAsync(oldTexts, cancellationToken);
                            if (oldEmbeddings == null || oldEmbeddings.Count != oldChunks.Count)
                            {
                                throw new InvalidOperationException($"Cannot safely replace document '{existingDoc.Id}': failed to capture backup snapshot of existing vectors before replacement.");
                            }
                            existingVectorSnapshot = oldChunks.Zip(oldEmbeddings, (c, e) => (c, e)).ToList();
                        }
                    }

                    try
                    {
                        // Clear old chunks before inserting updated chunks
                        if (existingDoc != null && _vectorStore != null)
                        {
                            try { await _vectorStore.DeleteDocumentChunksAsync(existingDoc.Id, cancellationToken); } catch { }
                        }

                        // Upsert all points into vector store in a single batch
                        if (_vectorStore != null)
                        {
                            await _vectorStore.AddChunksAsync(chunksWithEmbeddings, cancellationToken);
                        }
                    }
                    catch (Exception)
                    {
                        // Restore the original index on failure so vectors are never lost
                        if (existingVectorSnapshot != null && existingVectorSnapshot.Count > 0 && _vectorStore != null)
                        {
                            try
                            {
                                await _vectorStore.AddChunksAsync(existingVectorSnapshot, CancellationToken.None);
                            }
                            catch { }
                        }
                        throw;
                    }
                }

                if (_ftsStore != null && chunksWithMetaList.Count > 0)
                {
                    if (existingDoc != null)
                    {
                        try { await _ftsStore.DeleteChunksByDocumentIdAsync(existingDoc.Id, cancellationToken); } catch { }
                    }

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
                    if (existingDoc != null)
                    {
                        await _documentRepository.AddDocumentAsync(document);
                    }
                    await _documentRepository.UpdateDocumentStatusAsync(targetDocumentId, DocumentStatus.Processed, language: languageResult.Language, productCode: productCode, productCategory: productCategory);
                }
            }
            else
            {
                var message = $"Document saved to disk, but embedding was skipped because detected language is '{languageResult.Language}' (only English documents are embedded).";
                _logger?.LogInformation(
                    "[DocumentProcessor] File '{FileName}' was saved to '{FilePath}', but skipped from vector database because language is '{Language}'.",
                    fileName, filePath, languageResult.Language);

                // Finding 3: Clear old vector and FTS chunks when committing a non-English replacement
                if (existingDoc != null)
                {
                    if (_vectorStore != null)
                    {
                        try { await _vectorStore.DeleteDocumentChunksAsync(existingDoc.Id, cancellationToken); } catch { }
                    }
                    if (_ftsStore != null)
                    {
                        try { await _ftsStore.DeleteChunksByDocumentIdAsync(existingDoc.Id, cancellationToken); } catch { }
                    }
                }

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
                    if (existingDoc != null)
                    {
                        await _documentRepository.AddDocumentAsync(document);
                    }
                    await _documentRepository.UpdateDocumentStatusAsync(targetDocumentId, DocumentStatus.Skipped, errorMessage: message, language: languageResult.Language, productCode: productCode, productCategory: productCategory);
                }
            }

            // Safe cleanup: Now that overwrite has succeeded, clean up previous physical file
            if (existingDoc != null && !string.IsNullOrWhiteSpace(existingDoc.FilePath) && !string.Equals(existingDoc.FilePath, documentUrl, StringComparison.OrdinalIgnoreCase))
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
                    _logger?.LogWarning(ex, "Could not delete old file {FilePath} after successful document overwrite.", existingDoc.FilePath);
                }
            }

            // Invalidate query response cache on successful document index update
            TechnicalRagOrchestrator.InvalidateQueryCache();
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch { }

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
