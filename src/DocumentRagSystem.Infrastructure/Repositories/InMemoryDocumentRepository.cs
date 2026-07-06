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

    public Task UpdateDocumentStatusAsync(string id, DocumentStatus status, string? errorMessage = null)
    {
        if (_documents.TryGetValue(id, out var doc))
        {
            _documents[id] = doc with { Status = status, ErrorMessage = errorMessage };
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
}
