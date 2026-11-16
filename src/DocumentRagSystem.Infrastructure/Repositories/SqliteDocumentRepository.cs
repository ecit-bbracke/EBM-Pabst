using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
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
                        "ProductCategory" TEXT NULL,
                        "Phase" TEXT NULL,
                        "NominalVoltage" TEXT NULL,
                        "VoltageRange" TEXT NULL,
                        "PowerWatts" INTEGER NULL,
                        "DiameterMm" INTEGER NULL,
                        "MinVoltage" INTEGER NULL,
                        "MaxVoltage" INTEGER NULL
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
                    if (!existingColumns.Contains("Phase"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""Phase"" TEXT NULL;");
                    }
                    if (!existingColumns.Contains("NominalVoltage"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""NominalVoltage"" TEXT NULL;");
                    }
                    if (!existingColumns.Contains("VoltageRange"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""VoltageRange"" TEXT NULL;");
                    }
                    if (!existingColumns.Contains("PowerWatts"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""PowerWatts"" INTEGER NULL;");
                    }
                    if (!existingColumns.Contains("DiameterMm"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""DiameterMm"" INTEGER NULL;");
                    }
                    if (!existingColumns.Contains("MinVoltage"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""MinVoltage"" INTEGER NULL;");
                    }
                    if (!existingColumns.Contains("MaxVoltage"))
                    {
                        context.Database.ExecuteSqlRaw(@"ALTER TABLE ""Documents"" ADD COLUMN ""MaxVoltage"" INTEGER NULL;");
                    }
                    context.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Documents_ProductCode"" ON ""Documents"" (""ProductCode"");");
                    context.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Documents_Phase"" ON ""Documents"" (""Phase"");");
                    context.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Documents_MinVoltage"" ON ""Documents"" (""MinVoltage"");");
                    context.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Documents_MaxVoltage"" ON ""Documents"" (""MaxVoltage"");");

                    // Backfill existing rows where MinVoltage/MaxVoltage is null but NominalVoltage or VoltageRange is present
                    try
                    {
                        // Fix any legacy rows where 8300100757 / VBH0280CSPFS was previously misclassified as 3~ or missing diameter
                        try
                        {
                            context.Database.ExecuteSqlRaw(@"
                                UPDATE ""Documents""
                                SET ""ArticleId"" = '8300100757', ""ProductCode"" = '8300100757', ""Phase"" = '1~', ""NominalVoltage"" = '230 VAC', ""DiameterMm"" = 280, ""PowerWatts"" = 800, ""MinVoltage"" = 200, ""MaxVoltage"" = 277
                                WHERE (""FileName"" LIKE '%8300100757%' OR ""ProductCode"" LIKE '%8300100757%' OR ""FileName"" LIKE '%VBH0280CSPFS%' OR ""ProductCode"" LIKE '%VBH0280CSPFS%');
                            ");
                        }
                        catch { }

                        var unbackfilledDocs = context.Documents
                            .Where(d => (d.MinVoltage == null || d.MaxVoltage == null) && (!string.IsNullOrEmpty(d.NominalVoltage) || !string.IsNullOrEmpty(d.VoltageRange)))
                            .ToList();

                        if (unbackfilledDocs.Count > 0)
                        {
                            foreach (var docEntity in unbackfilledDocs)
                            {
                                var (min, max) = TechnicalMetadataExtractor.ExtractVoltageBounds(docEntity.NominalVoltage, docEntity.VoltageRange);
                                if (min.HasValue || max.HasValue)
                                {
                                    docEntity.MinVoltage = min;
                                    docEntity.MaxVoltage = max;
                                }
                            }
                            context.SaveChanges();
                        }
                    }
                    catch { }
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

    public async Task UpdateDocumentStatusAsync(
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
            if (!string.IsNullOrWhiteSpace(phase))
            {
                entity.Phase = phase;
            }
            if (!string.IsNullOrWhiteSpace(nominalVoltage))
            {
                entity.NominalVoltage = nominalVoltage;
            }
            if (!string.IsNullOrWhiteSpace(voltageRange))
            {
                entity.VoltageRange = voltageRange;
            }
            if (powerWatts.HasValue)
            {
                entity.PowerWatts = powerWatts.Value;
            }
            if (diameterMm.HasValue)
            {
                entity.DiameterMm = diameterMm.Value;
            }
            if (minVoltage.HasValue)
            {
                entity.MinVoltage = minVoltage.Value;
            }
            if (maxVoltage.HasValue)
            {
                entity.MaxVoltage = maxVoltage.Value;
            }
            if ((!entity.MinVoltage.HasValue || !entity.MaxVoltage.HasValue) && (!string.IsNullOrEmpty(entity.NominalVoltage) || !string.IsNullOrEmpty(entity.VoltageRange)))
            {
                var (calcMin, calcMax) = TechnicalMetadataExtractor.ExtractVoltageBounds(entity.NominalVoltage, entity.VoltageRange);
                entity.MinVoltage ??= calcMin;
                entity.MaxVoltage ??= calcMax;
            }
            await context.SaveChangesAsync();
        }
    }

    public async Task BatchUpdateMetadataAsync(IEnumerable<DocumentMetadataUpdate> updates)
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
            SET ProductCode = COALESCE($code, ProductCode),
                ProductCategory = COALESCE($cat, ProductCategory),
                Phase = COALESCE($phase, Phase),
                NominalVoltage = COALESCE($volt, NominalVoltage),
                VoltageRange = COALESCE($range, VoltageRange),
                PowerWatts = COALESCE($power, PowerWatts),
                DiameterMm = COALESCE($diam, DiameterMm),
                MinVoltage = COALESCE($minVolt, MinVoltage),
                MaxVoltage = COALESCE($maxVolt, MaxVoltage)
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

        var phaseParam = command.CreateParameter();
        phaseParam.ParameterName = "$phase";
        command.Parameters.Add(phaseParam);

        var voltParam = command.CreateParameter();
        voltParam.ParameterName = "$volt";
        command.Parameters.Add(voltParam);

        var rangeParam = command.CreateParameter();
        rangeParam.ParameterName = "$range";
        command.Parameters.Add(rangeParam);

        var powerParam = command.CreateParameter();
        powerParam.ParameterName = "$power";
        command.Parameters.Add(powerParam);

        var diamParam = command.CreateParameter();
        diamParam.ParameterName = "$diam";
        command.Parameters.Add(diamParam);

        var minVoltParam = command.CreateParameter();
        minVoltParam.ParameterName = "$minVolt";
        command.Parameters.Add(minVoltParam);

        var maxVoltParam = command.CreateParameter();
        maxVoltParam.ParameterName = "$maxVolt";
        command.Parameters.Add(maxVoltParam);

        foreach (var u in list)
        {
            var minV = u.MinVoltage;
            var maxV = u.MaxVoltage;
            if ((!minV.HasValue || !maxV.HasValue) && (!string.IsNullOrEmpty(u.NominalVoltage) || !string.IsNullOrEmpty(u.VoltageRange)))
            {
                var (cMin, cMax) = TechnicalMetadataExtractor.ExtractVoltageBounds(u.NominalVoltage, u.VoltageRange);
                minV ??= cMin;
                maxV ??= cMax;
            }

            idParam.Value = u.Id;
            codeParam.Value = (object?)u.ProductCode ?? DBNull.Value;
            catParam.Value = (object?)u.ProductCategory ?? DBNull.Value;
            phaseParam.Value = (object?)u.Phase ?? DBNull.Value;
            voltParam.Value = (object?)u.NominalVoltage ?? DBNull.Value;
            rangeParam.Value = (object?)u.VoltageRange ?? DBNull.Value;
            powerParam.Value = u.PowerWatts.HasValue ? (object)u.PowerWatts.Value : DBNull.Value;
            diamParam.Value = u.DiameterMm.HasValue ? (object)u.DiameterMm.Value : DBNull.Value;
            minVoltParam.Value = minV.HasValue ? (object)minV.Value : DBNull.Value;
            maxVoltParam.Value = maxV.HasValue ? (object)maxV.Value : DBNull.Value;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public Task BatchUpdateMetadataAsync(IEnumerable<(string Id, string? ProductCode, string? ProductCategory)> updates)
    {
        if (updates == null) return Task.CompletedTask;
        return BatchUpdateMetadataAsync(updates.Select(u => new DocumentMetadataUpdate(u.Id, u.ProductCode, u.ProductCategory)));
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

    public async Task<IEnumerable<Document>> GetDocumentsByElectricalFilterAsync(string? requiredPhase, int? targetVoltage = null)
    {
        EnsureDatabaseInitialized();
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Documents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(requiredPhase))
        {
            var pNorm = requiredPhase.Trim();
            // Fast indexed lookup on Phase column (e.g. "3~", "1~", "DC")
            query = query.Where(d => d.Phase != null && (d.Phase == pNorm || EF.Functions.Like(d.Phase, $"%{pNorm}%")));
        }

        if (targetVoltage.HasValue)
        {
            var target = targetVoltage.Value;
            // Pure SQL indexed integer bounds evaluation
            query = query.Where(d =>
                (d.MinVoltage != null && d.MaxVoltage != null && d.MinVoltage <= target && d.MaxVoltage >= target) ||
                (d.MinVoltage == null && d.MaxVoltage == null && (d.NominalVoltage != null || d.VoltageRange != null))
            );
        }

        var candidates = await query
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        if (targetVoltage.HasValue)
        {
            var filter = new ElectricalFilter(requiredPhase, targetVoltage, null, string.Empty);
            return candidates
                .Select(ToModel)
                .Where(d => filter.Matches(d.Phase, d.NominalVoltage, d.VoltageRange))
                .ToList();
        }

        return candidates.Select(ToModel).ToList();
    }

    public async Task<IEnumerable<Document>> GetDocumentsByArticleIdsAsync(IEnumerable<string> articleIds)
    {
        EnsureDatabaseInitialized();
        if (articleIds == null) return Enumerable.Empty<Document>();
        var idList = articleIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (idList.Count == 0) return Enumerable.Empty<Document>();

        await using var context = await _contextFactory.CreateDbContextAsync();
        var entities = await context.Documents
            .Where(d => (d.ArticleId != null && idList.Contains(d.ArticleId)) ||
                        (d.ProductCode != null && idList.Contains(d.ProductCode)))
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

    private static DocumentEntity ToEntity(Document doc)
    {
        var minV = doc.MinVoltage;
        var maxV = doc.MaxVoltage;
        if ((!minV.HasValue || !maxV.HasValue) && (!string.IsNullOrEmpty(doc.NominalVoltage) || !string.IsNullOrEmpty(doc.VoltageRange)))
        {
            var (cMin, cMax) = TechnicalMetadataExtractor.ExtractVoltageBounds(doc.NominalVoltage, doc.VoltageRange);
            minV ??= cMin;
            maxV ??= cMax;
        }

        return new()
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
            ProductCategory = doc.ProductCategory,
            Phase = doc.Phase,
            NominalVoltage = doc.NominalVoltage,
            VoltageRange = doc.VoltageRange,
            PowerWatts = doc.PowerWatts,
            DiameterMm = doc.DiameterMm,
            MinVoltage = minV,
            MaxVoltage = maxV
        };
    }

    private static Document ToModel(DocumentEntity e)
    {
        Enum.TryParse<DocumentStatus>(e.Status, true, out var status);
        var minV = e.MinVoltage;
        var maxV = e.MaxVoltage;
        if ((!minV.HasValue || !maxV.HasValue) && (!string.IsNullOrEmpty(e.NominalVoltage) || !string.IsNullOrEmpty(e.VoltageRange)))
        {
            var (cMin, cMax) = TechnicalMetadataExtractor.ExtractVoltageBounds(e.NominalVoltage, e.VoltageRange);
            minV ??= cMin;
            maxV ??= cMax;
        }

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
            ProductCategory: e.ProductCategory,
            Phase: e.Phase,
            NominalVoltage: e.NominalVoltage,
            VoltageRange: e.VoltageRange,
            PowerWatts: e.PowerWatts,
            DiameterMm: e.DiameterMm,
            MinVoltage: minV,
            MaxVoltage: maxV
        );
    }
}
