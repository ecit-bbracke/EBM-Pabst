using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DocumentRagSystem.WebApi.Data;

public class SqliteDocumentRepository : IDocumentRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

    public SqliteDocumentRepository(IDbContextFactory<ApplicationDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task AddDocumentAsync(Document document)
    {
        if (document == null) return;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var entity = ToEntity(document);

        var existing = await context.Documents.FindAsync(document.Id);
        if (existing == null)
        {
            context.Documents.Add(entity);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(entity);
        }

        await context.SaveChangesAsync();
    }

    public async Task UpdateDocumentStatusAsync(string id, DocumentStatus status, string? errorMessage = null, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var entity = await context.Documents.FindAsync(id);
        if (entity != null)
        {
            entity.Status = status.ToString();
            entity.ErrorMessage = errorMessage;
            if (!string.IsNullOrWhiteSpace(language))
            {
                entity.Language = language;
            }
            await context.SaveChangesAsync();
        }
    }

    public async Task<Document?> GetDocumentByIdAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var entity = await context.Documents.FindAsync(id);
        return entity != null ? ToModel(entity) : null;
    }

    public async Task<IEnumerable<Document>> GetAllDocumentsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var entities = await context.Documents
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        return entities.Select(ToModel).ToList();
    }

    private static DocumentEntity ToEntity(Document doc) => new()
    {
        Id = doc.Id,
        FileName = doc.FileName,
        FilePath = doc.FilePath,
        UploadedAt = doc.UploadedAt,
        Status = doc.Status.ToString(),
        ErrorMessage = doc.ErrorMessage,
        Language = doc.Language,
        ArticleId = doc.ArticleId,
        SourceDocumentId = doc.SourceDocumentId
    };

    private static Document ToModel(DocumentEntity e)
    {
        Enum.TryParse<DocumentStatus>(e.Status, true, out var status);
        return new Document(
            Id: e.Id,
            FileName: e.FileName,
            FilePath: e.FilePath,
            UploadedAt: e.UploadedAt,
            Status: status,
            ErrorMessage: e.ErrorMessage,
            Language: e.Language,
            ArticleId: e.ArticleId,
            SourceDocumentId: e.SourceDocumentId
        );
    }
}
