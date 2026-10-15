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
    private (DateTime CachedAt, List<Document> Docs)? _cachedDanishRepoDocs;
    private static readonly TimeSpan DanishCacheTtl = TimeSpan.FromMinutes(5);

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

        // Group chunks by their source document
        var chunkGroups = validChunks
            .GroupBy(c => c.FileName ?? c.DocumentId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sourceArticleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in chunkGroups)
        {
            var sampleChunk = group.First();
            var sourceFileName = sampleChunk.FileName ?? string.Empty;
            var artId = sampleChunk.ArticleId;
            if (string.IsNullOrWhiteSpace(artId) && !string.IsNullOrWhiteSpace(sourceFileName))
            {
                var (parsedArt, _) = ArticleDocumentNameParser.Parse(sourceFileName);
                artId = parsedArt;
            }
            if (!string.IsNullOrWhiteSpace(artId))
            {
                sourceArticleIds.Add(artId);
            }
        }

        // 1. Gather candidate documents targeted to the referenced chunks:
        //    a) Matching ArticleIds from repository (indexed fast lookup)
        //    b) Known Danish documents from repository (cached)
        //    c) Fallback to search directories if repository is empty/unconfigured
        var candidateDocs = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);

        if (_repository != null && sourceArticleIds.Count > 0)
        {
            var articleDocs = await GetRepositoryDocsByArticleIdsAsync(sourceArticleIds);
            foreach (var doc in articleDocs)
            {
                if (!string.IsNullOrWhiteSpace(doc.FileName))
                {
                    candidateDocs[doc.FileName] = EnsureArticleId(doc);
                }
            }
        }

        var danishCandidates = await GetDanishCandidatesAsync(cancellationToken);
        foreach (var doc in danishCandidates)
        {
            if (!string.IsNullOrWhiteSpace(doc.FileName) && !candidateDocs.ContainsKey(doc.FileName))
            {
                candidateDocs[doc.FileName] = EnsureArticleId(doc);
            }
        }

        // If candidates are empty, targeted scan on search directories
        if (candidateDocs.Count == 0 && _searchDirectories.Count > 0)
        {
            ScanSearchDirectoriesForTargets(candidateDocs, sourceArticleIds);
        }

        if (candidateDocs.Count == 0)
            return Enumerable.Empty<Document>();

        // Filter candidates to confirmed Danish documents
        var danishDocs = new List<Document>();
        foreach (var doc in candidateDocs.Values)
        {
            if (await IsDanishDocumentAsync(doc, cancellationToken))
            {
                danishDocs.Add(doc);
            }
        }

        if (danishDocs.Count == 0)
            return Enumerable.Empty<Document>();

        var matchedDanishDocs = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);

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

    private async Task<IEnumerable<Document>> GetRepositoryDocsByArticleIdsAsync(HashSet<string> articleIds)
    {
        if (_repository == null || articleIds.Count == 0) return Enumerable.Empty<Document>();

        try
        {
            var res = await _repository.GetDocumentsByArticleIdsAsync(articleIds);
            if (res != null && res.Any()) return res;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[DanishDocumentResolver] GetDocumentsByArticleIdsAsync failed, falling back to GetAllDocumentsAsync.");
        }

        // Resilient fallback for mocks or unspecialized repos
        try
        {
            var all = await _repository.GetAllDocumentsAsync();
            if (all != null)
            {
                return all.Where(d =>
                {
                    var artId = d.ArticleId;
                    if (string.IsNullOrWhiteSpace(artId) && !string.IsNullOrWhiteSpace(d.FileName))
                    {
                        (artId, _) = ArticleDocumentNameParser.Parse(d.FileName);
                    }
                    return !string.IsNullOrWhiteSpace(artId) && articleIds.Contains(artId);
                }).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[DanishDocumentResolver] Failed to load documents from repository by ArticleId.");
        }

        return Enumerable.Empty<Document>();
    }

    private async Task<List<Document>> GetDanishCandidatesAsync(CancellationToken cancellationToken)
    {
        if (_repository == null) return new List<Document>();

        if (_cachedDanishRepoDocs.HasValue && (DateTime.UtcNow - _cachedDanishRepoDocs.Value.CachedAt) < DanishCacheTtl)
        {
            return _cachedDanishRepoDocs.Value.Docs;
        }

        try
        {
            var danishDocs = await _repository.GetDanishDocumentsAsync();
            if (danishDocs != null && danishDocs.Any())
            {
                var list = danishDocs.ToList();
                _cachedDanishRepoDocs = (DateTime.UtcNow, list);
                return list;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[DanishDocumentResolver] GetDanishDocumentsAsync failed, falling back to GetAllDocumentsAsync.");
        }

        // Resilient fallback for mocks or repositories that only set up GetAllDocumentsAsync()
        try
        {
            var allDocs = await _repository.GetAllDocumentsAsync();
            if (allDocs != null)
            {
                var list = allDocs.Where(d =>
                    string.Equals(d.Language, "da", StringComparison.OrdinalIgnoreCase) ||
                    IsLegacyDanishFileName(d.FileName)).ToList();
                if (list.Count > 0)
                {
                    _cachedDanishRepoDocs = (DateTime.UtcNow, list);
                    return list;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[DanishDocumentResolver] Failed to load Danish documents from repository.");
        }

        return new List<Document>();
    }

    private void ScanSearchDirectoriesForTargets(Dictionary<string, Document> result, HashSet<string> targetArticleIds)
    {
        foreach (var dir in _searchDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                var patterns = new List<string> { "*_DA_*.pdf", "*DATA_SHEET_DA*.pdf", "*-DA-*.pdf" };
                foreach (var artId in targetArticleIds)
                {
                    patterns.Add($"*{artId}*.pdf");
                }

                foreach (var pattern in patterns.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var filePath in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
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
                                Language: IsLegacyDanishFileName(fileName) ? "da" : null,
                                ArticleId: artId,
                                SourceDocumentId: srcDocId
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[DanishDocumentResolver] Failed to scan directory {Dir} for companion documents.", dir);
            }
        }
    }

    private static Document EnsureArticleId(Document doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.ArticleId)) return doc;
        var (parsedArt, parsedDoc) = ArticleDocumentNameParser.Parse(doc.FileName);
        return doc with
        {
            ArticleId = parsedArt,
            SourceDocumentId = doc.SourceDocumentId ?? parsedDoc
        };
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

        // 3. Legacy filename fallback (fast check before any disk I/O)
        if (IsLegacyDanishFileName(doc.FileName))
        {
            FileLanguageCache[doc.FileName] = "da";
            return true;
        }

        // 4. Fallback for unindexed disk files: detect language from file content if text extractor available
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
}
