using System.Collections.Generic;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IDocumentRepository
{
    Task AddDocumentAsync(Document document);
    Task UpdateDocumentStatusAsync(string id, DocumentStatus status, string? errorMessage = null);
    Task<Document?> GetDocumentByIdAsync(string id);
    Task<IEnumerable<Document>> GetAllDocumentsAsync();
}
