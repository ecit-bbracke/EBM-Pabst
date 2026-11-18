using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;

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

    public Task UpdateDocumentStatusAsync(
        string id,
        DocumentStatus status,
        string? errorMessage = null,
        string? language = null,
        string? productCode = null,
        string? productCategory = null,
        string? phase = null,
        string? nominalVoltage = null,
        string? voltageRange = null,
        int? powerWatts = null,
        int? diameterMm = null,
        int? minVoltage = null,
        int? maxVoltage = null)
    {
        if (_documents.TryGetValue(id, out var doc))
        {
            var minV = minVoltage ?? doc.MinVoltage;
            var maxV = maxVoltage ?? doc.MaxVoltage;
            var nom = nominalVoltage ?? doc.NominalVoltage;
            var range = voltageRange ?? doc.VoltageRange;
            if ((!minV.HasValue || !maxV.HasValue) && (!string.IsNullOrEmpty(nom) || !string.IsNullOrEmpty(range)))
            {
                var (cMin, cMax) = TechnicalMetadataExtractor.ExtractVoltageBounds(nom, range);
                minV ??= cMin;
                maxV ??= cMax;
            }

            _documents[id] = doc with
            {
                Status = status,
                ErrorMessage = errorMessage,
                Language = language ?? doc.Language,
                ProductCode = productCode ?? doc.ProductCode,
                ProductCategory = productCategory ?? doc.ProductCategory,
                Phase = phase ?? doc.Phase,
                NominalVoltage = nom,
                VoltageRange = range,
                PowerWatts = powerWatts ?? doc.PowerWatts,
                DiameterMm = diameterMm ?? doc.DiameterMm,
                MinVoltage = minV,
                MaxVoltage = maxV
            };
        }
        return Task.CompletedTask;
    }

    public Task BatchUpdateMetadataAsync(IEnumerable<DocumentMetadataUpdate> updates)
    {
        if (updates == null) return Task.CompletedTask;
        foreach (var u in updates)
        {
            if (_documents.TryGetValue(u.Id, out var doc))
            {
                var minV = u.MinVoltage ?? doc.MinVoltage;
                var maxV = u.MaxVoltage ?? doc.MaxVoltage;
                var nom = u.NominalVoltage ?? doc.NominalVoltage;
                var range = u.VoltageRange ?? doc.VoltageRange;
                if ((!minV.HasValue || !maxV.HasValue) && (!string.IsNullOrEmpty(nom) || !string.IsNullOrEmpty(range)))
                {
                    var (cMin, cMax) = TechnicalMetadataExtractor.ExtractVoltageBounds(nom, range);
                    minV ??= cMin;
                    maxV ??= cMax;
                }

                _documents[u.Id] = doc with
                {
                    ProductCode = u.ProductCode ?? doc.ProductCode,
                    ProductCategory = u.ProductCategory ?? doc.ProductCategory,
                    Phase = u.Phase ?? doc.Phase,
                    NominalVoltage = nom,
                    VoltageRange = range,
                    PowerWatts = u.PowerWatts ?? doc.PowerWatts,
                    DiameterMm = u.DiameterMm ?? doc.DiameterMm,
                    MinVoltage = minV,
                    MaxVoltage = maxV
                };
            }
        }
        return Task.CompletedTask;
    }

    public Task BatchUpdateMetadataAsync(IEnumerable<(string Id, string? ProductCode, string? ProductCategory)> updates)
    {
        if (updates == null) return Task.CompletedTask;
        return BatchUpdateMetadataAsync(updates.Select(u => new DocumentMetadataUpdate(u.Id, u.ProductCode, u.ProductCategory)));
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

    public Task<IEnumerable<Document>> GetDocumentsByElectricalFilterAsync(string? requiredPhase, int? targetVoltage = null)
    {
        var filter = new ElectricalFilter(requiredPhase, targetVoltage, null, string.Empty);
        var matches = _documents.Values
            .Where(d =>
            {
                if (targetVoltage.HasValue && d.MinVoltage.HasValue && d.MaxVoltage.HasValue)
                {
                    bool phaseMatch = string.IsNullOrWhiteSpace(requiredPhase) ||
                        string.Equals(ElectricalFilter.NormalizePhase(d.Phase), ElectricalFilter.NormalizePhase(requiredPhase), StringComparison.OrdinalIgnoreCase);
                    return phaseMatch && targetVoltage.Value >= d.MinVoltage.Value && targetVoltage.Value <= d.MaxVoltage.Value;
                }
                return filter.Matches(d.Phase, d.NominalVoltage, d.VoltageRange);
            })
            .OrderByDescending(d => d.UploadedAt)
            .ToList();
        return Task.FromResult<IEnumerable<Document>>(matches);
    }

    public Task<(IEnumerable<Document> Items, int TotalCount)> GetPagedDocumentsAsync(
        int page, 
        int pageSize, 
        string? search, 
        string? category = null,
        string? sortBy = null,
        string? sortDir = null)
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

        var isDesc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        IEnumerable<Document> ordered = docs;
        if (string.Equals(sortBy, "name", StringComparison.OrdinalIgnoreCase) || string.Equals(sortBy, "fileName", StringComparison.OrdinalIgnoreCase))
        {
            ordered = isDesc ? docs.OrderByDescending(d => d.FileName) : docs.OrderBy(d => d.FileName);
        }
        else if (string.Equals(sortBy, "status", StringComparison.OrdinalIgnoreCase))
        {
            ordered = isDesc ? docs.OrderByDescending(d => d.Status) : docs.OrderBy(d => d.Status);
        }
        else if (string.Equals(sortBy, "date", StringComparison.OrdinalIgnoreCase) || string.Equals(sortBy, "uploadedAt", StringComparison.OrdinalIgnoreCase))
        {
            ordered = isDesc ? docs.OrderByDescending(d => d.UploadedAt) : docs.OrderBy(d => d.UploadedAt);
        }
        else
        {
            ordered = docs.OrderByDescending(d => d.UploadedAt);
        }

        var list = ordered.ToList();
        var totalCount = list.Count;
        var items = list
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
