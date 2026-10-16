using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IDocumentRepository
{
    Task AddDocumentAsync(Document document);
    Task UpdateDocumentStatusAsync(string id, DocumentStatus status, string? errorMessage = null, string? language = null, string? productCode = null, string? productCategory = null);
    Task BatchUpdateMetadataAsync(IEnumerable<(string Id, string? ProductCode, string? ProductCategory)> updates)
    {
        return Task.WhenAll(updates.Select(u => UpdateDocumentStatusAsync(u.Id, DocumentStatus.Processed, productCode: u.ProductCode, productCategory: u.ProductCategory)));
    }
    Task<Document?> GetDocumentByIdAsync(string id);
    Task<IEnumerable<Document>> GetAllDocumentsAsync();

    Task<(IEnumerable<Document> Items, int TotalCount)> GetPagedDocumentsAsync(int page, int pageSize, string? search, string? category = null)
    {
        return GetAllDocumentsAsync().ContinueWith(t =>
        {
            var docs = t.Result;
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
            var items = list.Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToList();
            return ((IEnumerable<Document>)items, totalCount);
        });
    }

    Task DeleteDocumentAsync(string id);

    Task<IEnumerable<Document>> GetDocumentsByArticleIdsAsync(IEnumerable<string> articleIds)
    {
        if (articleIds == null) return Task.FromResult<IEnumerable<Document>>(Array.Empty<Document>());
        var idSet = new HashSet<string>(articleIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
        if (idSet.Count == 0) return Task.FromResult<IEnumerable<Document>>(Array.Empty<Document>());

        return GetAllDocumentsAsync().ContinueWith(t =>
        {
            return (IEnumerable<Document>)t.Result.Where(d => !string.IsNullOrWhiteSpace(d.ArticleId) && idSet.Contains(d.ArticleId)).ToList();
        });
    }

    Task<IEnumerable<Document>> GetDanishDocumentsAsync()
    {
        return GetAllDocumentsAsync().ContinueWith(t =>
        {
            return (IEnumerable<Document>)t.Result.Where(d =>
                string.Equals(d.Language, "da", StringComparison.OrdinalIgnoreCase) ||
                (d.FileName != null && (
                    d.FileName.Contains("_DA_", StringComparison.OrdinalIgnoreCase) ||
                    d.FileName.Contains("DATA_SHEET_DA", StringComparison.OrdinalIgnoreCase) ||
                    d.FileName.Contains("-DA-", StringComparison.OrdinalIgnoreCase) ||
                    d.FileName.Contains("_DA.", StringComparison.OrdinalIgnoreCase) ||
                    d.FileName.Contains("-DA.", StringComparison.OrdinalIgnoreCase)
                ))
            ).ToList();
        });
    }

    Task<Document?> GetDocumentByFileNameAsync(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return Task.FromResult<Document?>(null);
        return GetAllDocumentsAsync().ContinueWith(t =>
        {
            return t.Result.FirstOrDefault(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        });
    }

    Task<Document?> GetDocumentBySourceDocIdAsync(string sourceDocId)
    {
        if (string.IsNullOrWhiteSpace(sourceDocId)) return Task.FromResult<Document?>(null);
        return GetAllDocumentsAsync().ContinueWith(t =>
        {
            return t.Result.FirstOrDefault(d =>
                string.Equals(d.SourceDocumentId, sourceDocId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Id, sourceDocId, StringComparison.OrdinalIgnoreCase));
        });
    }
}
