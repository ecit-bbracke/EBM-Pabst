using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Repositories;

public class InMemoryDocumentRepository : IDocumentRepository
{
    private readonly ConcurrentDictionary<string, Document> _documents = new();

    public Task AddDocumentAsync(Document document)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        _documents[document.Id] = document;
        return Task.CompletedTask;
    }

    public Task UpdateDocumentStatusAsync(string id, DocumentStatus status, string? errorMessage = null, string? language = null, string? productCode = null, string? productCategory = null)
    {
        if (_documents.TryGetValue(id, out var doc))
        {
            _documents[id] = doc with 
            { 
                Status = status, 
                ErrorMessage = errorMessage,
                Language = language ?? doc.Language,
                ProductCode = productCode ?? doc.ProductCode,
                ProductCategory = productCategory ?? doc.ProductCategory
            };
        }
        return Task.CompletedTask;
    }

    public Task BatchUpdateMetadataAsync(IEnumerable<(string Id, string? ProductCode, string? ProductCategory)> updates)
    {
        if (updates == null) return Task.CompletedTask;
        foreach (var (id, code, cat) in updates)
        {
            if (_documents.TryGetValue(id, out var doc))
            {
                _documents[id] = doc with { ProductCode = code, ProductCategory = cat };
            }
        }
        return Task.CompletedTask;
    }

    public Task<Document?> GetDocumentByIdAsync(string id)
    {
        _documents.TryGetValue(id, out var doc);
        return Task.FromResult(doc);
    }

    public Task<IEnumerable<Document>> GetAllDocumentsAsync()
    {
        return Task.FromResult<IEnumerable<Document>>(_documents.Values.OrderByDescending(d => d.UploadedAt).ToList());
    }

    public Task<(IEnumerable<Document> Items, int TotalCount)> GetPagedDocumentsAsync(int page, int pageSize, string? search, string? category = null)
    {
        var docs = _documents.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            docs = docs.Where(d => string.Equals(d.ProductCategory, category, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            docs = docs.Where(d =>
                (!string.IsNullOrWhiteSpace(d.FileName) && d.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.ArticleId) && d.ArticleId.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.ProductCode) && d.ProductCode.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.Id) && d.Id.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.SourceDocumentId) && d.SourceDocumentId.Contains(term, StringComparison.OrdinalIgnoreCase))
            );
        }

        var list = docs.ToList();
        var totalCount = list.Count;
        var items = list.OrderByDescending(d => d.UploadedAt)
            .Skip((Math.Max(1, page) - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(((IEnumerable<Document>)items, totalCount));
    }

    public Task<IEnumerable<Document>> GetDocumentsByArticleIdsAsync(IEnumerable<string> articleIds)
    {
        if (articleIds == null) return Task.FromResult<IEnumerable<Document>>(Array.Empty<Document>());
        var idSet = new HashSet<string>(articleIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
        if (idSet.Count == 0) return Task.FromResult<IEnumerable<Document>>(Array.Empty<Document>());

        var matches = _documents.Values
            .Where(d => !string.IsNullOrWhiteSpace(d.ArticleId) && idSet.Contains(d.ArticleId))
            .ToList();
        return Task.FromResult<IEnumerable<Document>>(matches);
    }

    public Task<IEnumerable<Document>> GetDanishDocumentsAsync()
    {
        var matches = _documents.Values
            .Where(d => string.Equals(d.Language, "da", StringComparison.OrdinalIgnoreCase) ||
                        (d.FileName != null && (
                            d.FileName.Contains("_DA_", StringComparison.OrdinalIgnoreCase) ||
                            d.FileName.Contains("DATA_SHEET_DA", StringComparison.OrdinalIgnoreCase) ||
                            d.FileName.Contains("-DA-", StringComparison.OrdinalIgnoreCase) ||
                            d.FileName.Contains("_DA.", StringComparison.OrdinalIgnoreCase) ||
                            d.FileName.Contains("-DA.", StringComparison.OrdinalIgnoreCase))))
            .ToList();
        return Task.FromResult<IEnumerable<Document>>(matches);
    }

    public Task<Document?> GetDocumentByFileNameAsync(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return Task.FromResult<Document?>(null);
        var doc = _documents.Values.FirstOrDefault(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(doc);
    }

    public Task<Document?> GetDocumentBySourceDocIdAsync(string sourceDocId)
    {
        if (string.IsNullOrWhiteSpace(sourceDocId)) return Task.FromResult<Document?>(null);
        var doc = _documents.Values.FirstOrDefault(d =>
            string.Equals(d.SourceDocumentId, sourceDocId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Id, sourceDocId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(doc);
    }

    public Task DeleteDocumentAsync(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            _documents.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }
}
