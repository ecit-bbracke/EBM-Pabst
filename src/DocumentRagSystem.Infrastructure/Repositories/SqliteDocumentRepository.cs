using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentRagSystem.Infrastructure.Repositories;

public class SqliteDocumentRepository : IDocumentRepository
{
    private readonly IDbContextFactory<DocumentDbContext> _contextFactory;
    private static readonly ConcurrentDictionary<string, bool> _initializedDatabases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _initLock = new();

    public SqliteDocumentRepository(IDbContextFactory<DocumentDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        EnsureDatabaseInitialized();
    }

    private void EnsureDatabaseInitialized()
    {
        string connStr;
        try
        {
            using var probeContext = _contextFactory.CreateDbContext();
            connStr = probeContext.Database.GetConnectionString() ?? "default";
        }
        catch
        {
            connStr = "default";
        }

        if (_initializedDatabases.ContainsKey(connStr)) return;

        lock (_initLock)
        {
            if (_initializedDatabases.ContainsKey(connStr)) return;

            try
            {
                using var context = _contextFactory.CreateDbContext();
                context.Database.EnsureCreated();
                context.Database.ExecuteSqlRaw("""
                    CREATE TABLE IF NOT EXISTS "Documents" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_Documents" PRIMARY KEY,
                        "FileName" TEXT NOT NULL,
                        "FilePath" TEXT NOT NULL,
                        "UploadedAt" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "ErrorMessage" TEXT NULL,
                        "Language" TEXT NULL,
                        "ArticleId" TEXT NULL,
                        "SourceDocumentId" TEXT NULL,
                        "ProductCode" TEXT NULL,
                        "ProductCategory" TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS "IX_Documents_FileName" ON "Documents" ("FileName");
                    CREATE INDEX IF NOT EXISTS "IX_Documents_ArticleId" ON "Documents" ("ArticleId");
                    CREATE INDEX IF NOT EXISTS "IX_Documents_Language" ON "Documents" ("Language");
                """);

                // Safely migrate existing databases where table already existed without new columns
                try
                {
                    var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var command = context.Database.GetDbConnection().CreateCommand())
                    {
                        context.Database.OpenConnection();
                        command.CommandText = "PRAGMA table_info(\"Documents\");";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            existingColumns.Add(reader.GetString(1));
                        }
                    }

                    if (!existingColumns.Contains("ProductCode"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""ProductCode"" TEXT NULL;");
                    }
                    if (!existingColumns.Contains("ProductCategory"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""ProductCategory"" TEXT NULL;");
                    }
                    context.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Documents_ProductCode"" ON ""Documents"" (""ProductCode"");");
                }
                catch { }

                _initializedDatabases[connStr] = true;
            }
            catch
            {
                // Ignore if created concurrently or retry on next call
            }
        }
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

    public async Task UpdateDocumentStatusAsync(string id, DocumentStatus status, string? errorMessage = null, string? language = null, string? productCode = null, string? productCategory = null)
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
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                entity.ProductCode = productCode;
            }
            if (!string.IsNullOrWhiteSpace(productCategory))
            {
                entity.ProductCategory = productCategory;
            }
            await context.SaveChangesAsync();
        }
    }

    public async Task BatchUpdateMetadataAsync(IEnumerable<(string Id, string? ProductCode, string? ProductCategory)> updates)
    {
        if (updates == null) return;
        var list = updates.ToList();
        if (list.Count == 0) return;

        EnsureDatabaseInitialized();
        await using var context = await _contextFactory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        using var command = connection.CreateCommand();
        command.Transaction = transaction as System.Data.Common.DbTransaction;
        command.CommandText = @"
            UPDATE Documents 
            SET ProductCode = $code, ProductCategory = $cat 
            WHERE Id = $id";

        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        command.Parameters.Add(idParam);

        var codeParam = command.CreateParameter();
        codeParam.ParameterName = "$code";
        command.Parameters.Add(codeParam);

        var catParam = command.CreateParameter();
        catParam.ParameterName = "$cat";
        command.Parameters.Add(catParam);

        foreach (var (id, code, cat) in list)
        {
            idParam.Value = id;
            codeParam.Value = (object?)code ?? DBNull.Value;
            catParam.Value = (object?)cat ?? DBNull.Value;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
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
        EnsureDatabaseInitialized();
        await using var context = await _contextFactory.CreateDbContextAsync();
        var entities = await context.Documents
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        return entities.Select(ToModel).ToList();
    }

    public async Task<(IEnumerable<Document> Items, int TotalCount)> GetPagedDocumentsAsync(int page, int pageSize, string? search, string? category = null)
    {
        EnsureDatabaseInitialized();
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Documents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(d => d.ProductCategory == category);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(d =>
                EF.Functions.Like(d.FileName, term) ||
                EF.Functions.Like(d.ArticleId, term) ||
                EF.Functions.Like(d.ProductCode, term) ||
                EF.Functions.Like(d.Id, term) ||
                EF.Functions.Like(d.SourceDocumentId, term)
            );
        }

        var totalCount = await query.CountAsync();
        var entities = await query
            .OrderByDescending(d => d.UploadedAt)
            .Skip((Math.Max(1, page) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (entities.Select(ToModel).ToList(), totalCount);
    }

    public async Task<IEnumerable<Document>> GetDocumentsByArticleIdsAsync(IEnumerable<string> articleIds)
    {
        EnsureDatabaseInitialized();
        if (articleIds == null) return Enumerable.Empty<Document>();
        var idList = articleIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (idList.Count == 0) return Enumerable.Empty<Document>();

        await using var context = await _contextFactory.CreateDbContextAsync();
        var entities = await context.Documents
            .Where(d => d.ArticleId != null && idList.Contains(d.ArticleId))
            .ToListAsync();

        return entities.Select(ToModel).ToList();
    }

    public async Task<IEnumerable<Document>> GetDanishDocumentsAsync()
    {
        EnsureDatabaseInitialized();
        await using var context = await _contextFactory.CreateDbContextAsync();
        var entities = await context.Documents
            .Where(d => d.Language == "da" ||
                        (d.FileName != null && (
                            d.FileName.Contains("_DA_") ||
                            d.FileName.Contains("DATA_SHEET_DA") ||
                            d.FileName.Contains("-DA-") ||
                            d.FileName.Contains("_DA.") ||
                            d.FileName.Contains("-DA."))))
            .ToListAsync();

        return entities.Select(ToModel).ToList();
    }

    public async Task<Document?> GetDocumentByFileNameAsync(string fileName)
    {
        EnsureDatabaseInitialized();
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var entity = await context.Documents
            .FirstOrDefaultAsync(d => d.FileName == fileName);

        return entity != null ? ToModel(entity) : null;
    }

    public async Task<Document?> GetDocumentBySourceDocIdAsync(string sourceDocId)
    {
        EnsureDatabaseInitialized();
        if (string.IsNullOrWhiteSpace(sourceDocId)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var entity = await context.Documents
            .FirstOrDefaultAsync(d => d.SourceDocumentId == sourceDocId || d.Id == sourceDocId);

        return entity != null ? ToModel(entity) : null;
    }

    public async Task DeleteDocumentAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var entity = await context.Documents.FindAsync(id);
        if (entity != null)
        {
            context.Documents.Remove(entity);
            await context.SaveChangesAsync();
        }
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
        SourceDocumentId = doc.SourceDocumentId,
        ProductCode = doc.ProductCode,
        ProductCategory = doc.ProductCategory
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
            SourceDocumentId: e.SourceDocumentId,
            ProductCode: e.ProductCode,
            ProductCategory: e.ProductCategory
        );
    }
}
