using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Core.Services;

public class DanishDocumentResolver : IDanishDocumentResolver
{
    private readonly IDocumentRepository? _repository;
    private readonly ILanguageDetector _languageDetector;
    private readonly ITextExtractor? _textExtractor;
    private readonly IEbmProductCodeParser _codeParser;
    private readonly List<string> _searchDirectories;
    private readonly ILogger<DanishDocumentResolver>? _logger;

    private static readonly ConcurrentDictionary<string, string> FileLanguageCache = new(StringComparer.OrdinalIgnoreCase);

    public DanishDocumentResolver(
        IDocumentRepository? repository = null,
        ILanguageDetector? languageDetector = null,
        ITextExtractor? textExtractor = null,
        IEbmProductCodeParser? codeParser = null,
        IEnumerable<string>? searchDirectories = null,
        ILogger<DanishDocumentResolver>? logger = null)
    {
        _repository = repository;
        _languageDetector = languageDetector ?? new DefaultLanguageDetector();
        _textExtractor = textExtractor;
        _codeParser = codeParser ?? new EbmProductCodeParser();
        _searchDirectories = searchDirectories != null ? searchDirectories.ToList() : new List<string>();
        _logger = logger;
    }

    public async Task<IEnumerable<Document>> FindDanishCompanionDocumentsAsync(
        IEnumerable<DocumentChunk> referencedChunks,
        CancellationToken cancellationToken = default)
    {
        if (referencedChunks == null)
            return Enumerable.Empty<Document>();

        var validChunks = referencedChunks
            .Where(c => c != null && (!string.IsNullOrWhiteSpace(c.FileName) || !string.IsNullOrWhiteSpace(c.DocumentId)))
            .ToList();

        if (validChunks.Count == 0)
            return Enumerable.Empty<Document>();

        var allAvailableDocs = await GetAllAvailableDocumentsAsync(cancellationToken);
        if (allAvailableDocs.Count == 0)
            return Enumerable.Empty<Document>();

        // Identify Danish candidate documents based on content language detection
        var danishDocs = new List<Document>();
        foreach (var doc in allAvailableDocs)
        {
            if (await IsDanishDocumentAsync(doc, cancellationToken))
            {
                danishDocs.Add(doc);
            }
        }

        if (danishDocs.Count == 0)
            return Enumerable.Empty<Document>();

        var matchedDanishDocs = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);

        // Group chunks by their source document
        var chunkGroups = validChunks
            .GroupBy(c => c.FileName ?? c.DocumentId, StringComparer.OrdinalIgnoreCase);

        foreach (var group in chunkGroups)
        {
            var sampleChunk = group.First();
            var sourceFileName = sampleChunk.FileName ?? string.Empty;
            var sourceArticleId = sampleChunk.ArticleId;

            // If chunk did not have ArticleId pre-populated, parse it from filename
            if (string.IsNullOrWhiteSpace(sourceArticleId) && !string.IsNullOrWhiteSpace(sourceFileName))
            {
                var (parsedArt, _) = ArticleDocumentNameParser.Parse(sourceFileName);
                sourceArticleId = parsedArt;
            }

            // Extract any product codes from source filename or chunk text as secondary match
            var sourceProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(sourceFileName))
            {
                foreach (var info in _codeParser.ExtractProductsFromText(sourceFileName))
                {
                    sourceProductCodes.Add(info.CleanCode);
                }
            }

            foreach (var chunk in group)
            {
                if (!string.IsNullOrWhiteSpace(chunk.Text) && sourceProductCodes.Count == 0)
                {
                    foreach (var info in _codeParser.ExtractProductsFromText(chunk.Text))
                    {
                        sourceProductCodes.Add(info.CleanCode);
                    }
                }
            }

            // Find matching Danish companion documents
            foreach (var danishDoc in danishDocs)
            {
                // Do not link a document to itself
                if (string.Equals(danishDoc.FileName, sourceFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(danishDoc.Id, sampleChunk.DocumentId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isMatch = false;

                // Match 1: Primary match by ArticleId (no language code needed in filename)
                if (!string.IsNullOrWhiteSpace(sourceArticleId) &&
                    !string.IsNullOrWhiteSpace(danishDoc.ArticleId) &&
                    string.Equals(sourceArticleId, danishDoc.ArticleId, StringComparison.OrdinalIgnoreCase))
                {
                    isMatch = true;
                }

                // Match 2: Secondary match by matching ebm product code
                if (!isMatch && sourceProductCodes.Count > 0 && !string.IsNullOrWhiteSpace(danishDoc.FileName))
                {
                    var danishProductCodes = _codeParser.ExtractProductsFromText(danishDoc.FileName);
                    if (danishProductCodes.Any(dp => sourceProductCodes.Contains(dp.CleanCode)))
                    {
                        isMatch = true;
                    }
                }

                if (isMatch)
                {
                    var docKey = NormalizeDocumentKey(danishDoc.FileName);
                    if (!matchedDanishDocs.TryGetValue(docKey, out var existing) || danishDoc.FileName.Length < existing.FileName.Length)
                    {
                        matchedDanishDocs[docKey] = danishDoc;
                        _logger?.LogInformation(
                            "[DanishDocumentResolver] Found Danish companion '{DanishFile}' for referenced document '{SourceFile}' by ArticleId '{ArticleId}'.",
                            danishDoc.FileName, sourceFileName, sourceArticleId ?? "N/A");
                    }
                }
            }
        }

        return matchedDanishDocs.Values.ToList();
    }

    private async Task<bool> IsDanishDocumentAsync(Document doc, CancellationToken cancellationToken)
    {
        // 1. Language recorded during upload via content language detection
        if (string.Equals(doc.Language, "da", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(doc.Language, "en", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. Check cache
        if (FileLanguageCache.TryGetValue(doc.FileName, out var cachedLang))
        {
            return string.Equals(cachedLang, "da", StringComparison.OrdinalIgnoreCase);
        }

        // 3. Fallback for unindexed disk files: detect language from file content if text extractor available
        if (_textExtractor != null && !string.IsNullOrWhiteSpace(doc.FilePath) && File.Exists(doc.FilePath))
        {
            try
            {
                using var stream = File.OpenRead(doc.FilePath);
                var extracted = await _textExtractor.ExtractTextAsync(stream);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    var detection = await _languageDetector.DetectLanguageAsync(extracted, doc.FileName);
                    FileLanguageCache[doc.FileName] = detection.Language;
                    return string.Equals(detection.Language, "da", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[DanishDocumentResolver] Failed to extract text to detect language for {File}.", doc.FileName);
            }
        }

        // 4. Legacy filename fallback for existing benchmark samples
        if (IsLegacyDanishFileName(doc.FileName))
        {
            FileLanguageCache[doc.FileName] = "da";
            return true;
        }

        return false;
    }

    private static bool IsLegacyDanishFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var fn = fileName.ToUpperInvariant();
        return fn.Contains("_DA_") ||
               fn.Contains("DATA_SHEET_DA") ||
               fn.Contains("-DA-") ||
               fn.Contains("_DA.") ||
               fn.Contains("-DA.");
    }

    private static string NormalizeDocumentKey(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var fn = Path.GetFileName(fileName);
        if (fn.Length > 37 && fn[36] == '_' && Guid.TryParse(fn.Substring(0, 36), out _))
        {
            return fn.Substring(37);
        }
        return fn;
    }

    private async Task<List<Document>> GetAllAvailableDocumentsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);

        // 1. From repository (uploaded files, including skipped non-English files with their detected language)
        if (_repository != null)
        {
            try
            {
                var repoDocs = await _repository.GetAllDocumentsAsync();
                foreach (var doc in repoDocs)
                {
                    if (!string.IsNullOrWhiteSpace(doc.FileName) && !result.ContainsKey(doc.FileName))
                    {
                        var artId = doc.ArticleId;
                        var srcDocId = doc.SourceDocumentId;
                        if (string.IsNullOrWhiteSpace(artId))
                        {
                            var (parsedArt, parsedDoc) = ArticleDocumentNameParser.Parse(doc.FileName);
                            artId = parsedArt;
                            srcDocId = parsedDoc;
                        }

                        result[doc.FileName] = doc with
                        {
                            ArticleId = artId,
                            SourceDocumentId = srcDocId
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[DanishDocumentResolver] Failed to load documents from repository.");
            }
        }

        // 2. From search directories (e.g. data/Generelt, uploads)
        foreach (var dir in _searchDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                var files = Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly);
                foreach (var filePath in files)
                {
                    var fileName = Path.GetFileName(filePath);
                    if (!result.ContainsKey(fileName))
                    {
                        var (artId, srcDocId) = ArticleDocumentNameParser.Parse(fileName);
                        result[fileName] = new Document(
                            Id: fileName,
                            FileName: fileName,
                            FilePath: filePath,
                            UploadedAt: File.GetCreationTimeUtc(filePath),
                            Status: DocumentStatus.Processed,
                            Language: null,
                            ArticleId: artId,
                            SourceDocumentId: srcDocId
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[DanishDocumentResolver] Failed to scan directory {Dir} for companion documents.", dir);
            }
        }

        return result.Values.ToList();
    }
}
