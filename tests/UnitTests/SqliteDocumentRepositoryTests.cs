using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Data;
using DocumentRagSystem.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class SqliteDocumentRepositoryTests : IDisposable
{
    private readonly string _dbFileName;
    private readonly IDbContextFactory<DocumentDbContext> _factory;

    public SqliteDocumentRepositoryTests()
    {
        _dbFileName = $"test_repo_{Guid.NewGuid():N}.db";
        var options = new DbContextOptionsBuilder<DocumentDbContext>()
            .UseSqlite($"Data Source={_dbFileName}")
            .Options;

        var mockFactory = new TestDbContextFactory(options);
        _factory = mockFactory;

        using var ctx = mockFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbFileName))
            {
                File.Delete(_dbFileName);
            }
        }
        catch { }
    }

    [Fact]
    public async Task AddAndGetDocumentAsync_PersistsDocument()
    {
        var repo = new SqliteDocumentRepository(_factory);
        var doc = new Document(
            Id: "doc-1",
            FileName: "Art_123-Doc_456.pdf",
            FilePath: "/uploads/doc1.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processing,
            ArticleId: "123",
            SourceDocumentId: "456"
        );

        await repo.AddDocumentAsync(doc);
        var retrieved = await repo.GetDocumentByIdAsync("doc-1");

        Assert.NotNull(retrieved);
        Assert.Equal("Art_123-Doc_456.pdf", retrieved.FileName);
        Assert.Equal(DocumentStatus.Processing, retrieved.Status);
        Assert.Equal("123", retrieved.ArticleId);
        Assert.Equal("456", retrieved.SourceDocumentId);
    }

    [Fact]
    public async Task UpdateDocumentStatusAsync_UpdatesStatusAndLanguage()
    {
        var repo = new SqliteDocumentRepository(_factory);
        var doc = new Document("doc-2", "test.pdf", "/uploads/test.pdf", DateTime.UtcNow, DocumentStatus.Pending);
        await repo.AddDocumentAsync(doc);

        await repo.UpdateDocumentStatusAsync("doc-2", DocumentStatus.Skipped, "Skipped test", "da");
        var updated = await repo.GetDocumentByIdAsync("doc-2");

        Assert.NotNull(updated);
        Assert.Equal(DocumentStatus.Skipped, updated.Status);
        Assert.Equal("Skipped test", updated.ErrorMessage);
        Assert.Equal("da", updated.Language);
    }

    [Fact]
    public async Task GetAllDocumentsAsync_ReturnsAllOrderedByUploadedAt()
    {
        var repo = new SqliteDocumentRepository(_factory);
        var doc1 = new Document("d1", "file1.pdf", "/uploads/f1.pdf", DateTime.UtcNow.AddMinutes(-5));
        var doc2 = new Document("d2", "file2.pdf", "/uploads/f2.pdf", DateTime.UtcNow);

        await repo.AddDocumentAsync(doc1);
        await repo.AddDocumentAsync(doc2);

        var all = (await repo.GetAllDocumentsAsync()).ToList();

        Assert.Equal(2, all.Count);
        Assert.Equal("d2", all[0].Id);
        Assert.Equal("d1", all[1].Id);
    }

    [Fact]
    public async Task DeleteDocumentAsync_RemovesDocumentFromDatabase()
    {
        var repo = new SqliteDocumentRepository(_factory);
        var doc = new Document("delete-id", "to_delete.pdf", "/uploads/to_delete.pdf", DateTime.UtcNow);
        await repo.AddDocumentAsync(doc);

        var beforeDelete = await repo.GetDocumentByIdAsync("delete-id");
        Assert.NotNull(beforeDelete);

        await repo.DeleteDocumentAsync("delete-id");

        var afterDelete = await repo.GetDocumentByIdAsync("delete-id");
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task GetPagedDocumentsAsync_OnLegacyDatabaseWithoutProductCode_MigratesAndSucceeds()
    {
        // Arrange: Create a database with the legacy schema (without ProductCode / ProductCategory)
        var legacyDb = $"legacy_{Guid.NewGuid():N}.db";
        try
        {
            var options = new DbContextOptionsBuilder<DocumentDbContext>()
                .UseSqlite($"Data Source={legacyDb}")
                .Options;

            var factory = new TestDbContextFactory(options);
            using (var ctx = factory.CreateDbContext())
            {
                ctx.Database.ExecuteSqlRaw("""
                    CREATE TABLE "Documents" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_Documents" PRIMARY KEY,
                        "FileName" TEXT NOT NULL,
                        "FilePath" TEXT NOT NULL,
                        "UploadedAt" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "ErrorMessage" TEXT NULL,
                        "Language" TEXT NULL,
                        "ArticleId" TEXT NULL,
                        "SourceDocumentId" TEXT NULL
                    );
                    INSERT INTO "Documents" ("Id", "FileName", "FilePath", "UploadedAt", "Status")
                    VALUES ('doc-legacy-1', 'test.pdf', '/path', '2026-01-01', 'Processed');
                """);
            }

            // Act: Instantiate repository and call GetPagedDocumentsAsync
            var repo = new SqliteDocumentRepository(factory);
            var (items, total) = await repo.GetPagedDocumentsAsync(1, 10, null);

            // Assert: Must not throw 'no such column: d.ProductCategory'
            Assert.Equal(1, total);
            Assert.Single(items);
            Assert.Equal("doc-legacy-1", items.First().Id);
        }
        finally
        {
            try { File.Delete(legacyDb); } catch { }
        }
    }

    [Fact]
    public async Task MigrateLocalWebApiIdentityDbIfPresent()
    {
        var webApiDb = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "DocumentRagSystem.WebApi", "identity.db"));
        if (File.Exists(webApiDb))
        {
            var options = new DbContextOptionsBuilder<DocumentDbContext>()
                .UseSqlite($"Data Source={webApiDb}")
                .Options;
            var factory = new TestDbContextFactory(options);
            var repo = new SqliteDocumentRepository(factory);
            var (items, total) = await repo.GetPagedDocumentsAsync(1, 5, null);
            Assert.NotNull(items);
        }
    }

    [Fact]
    public async Task GetDocumentsByElectricalFilterAsync_FiltersByPhaseAndVoltageViaSqlite()
    {
        // GIVEN a repository containing documents with different phases and voltages:
        var repo = new SqliteDocumentRepository(_factory);

        var doc3Phase = new Document(
            Id: "doc-3phase",
            FileName: "VBH0560_3Phase.pdf",
            FilePath: "/uploads/vbh0560.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed,
            Phase: "3~",
            NominalVoltage: "200 VAC",
            VoltageRange: "200 .. 240 VAC"
        );

        var doc1Phase = new Document(
            Id: "doc-1phase",
            FileName: "VBH0280_1Phase.pdf",
            FilePath: "/uploads/vbh0280.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed,
            Phase: "1~",
            NominalVoltage: "230 VAC",
            VoltageRange: "200 .. 277 VAC"
        );

        await repo.AddDocumentAsync(doc3Phase);
        await repo.AddDocumentAsync(doc1Phase);

        // WHEN querying for 3~ phase matching 230 VAC:
        var results3Phase = await repo.GetDocumentsByElectricalFilterAsync("3~", 230);

        // THEN only doc3Phase matches (because 200..240 covers 230 and phase is 3~):
        var list3 = results3Phase.ToList();
        Assert.Single(list3);
        Assert.Equal("doc-3phase", list3[0].Id);

        // WHEN querying for 1~ phase matching 230 VAC:
        var results1Phase = await repo.GetDocumentsByElectricalFilterAsync("1~", 230);

        // THEN only doc1Phase matches:
        var list1 = results1Phase.ToList();
        Assert.Single(list1);
        Assert.Equal("doc-1phase", list1[0].Id);
    }

    [Fact]
    public async Task EnsureDatabaseInitialized_AutomaticallyBackfillsMinAndMaxVoltageForExistingDocuments()
    {
        // GIVEN a repository where a document was inserted with nominal voltage / range:
        var repo = new SqliteDocumentRepository(_factory);

        var docLegacy = new Document(
            Id: "doc-legacy",
            FileName: "LegacyDoc.pdf",
            FilePath: "/uploads/legacy.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed,
            Phase: "3~",
            NominalVoltage: "230 VAC",
            VoltageRange: "200 .. 240 VAC"
        );

        await repo.AddDocumentAsync(docLegacy);

        // WHEN retrieving the document after initialization:
        var retrieved = await repo.GetDocumentByIdAsync("doc-legacy");

        // THEN MinVoltage and MaxVoltage are automatically backfilled (200 and 240):
        Assert.NotNull(retrieved);
        Assert.Equal(200, retrieved.MinVoltage);
        Assert.Equal(240, retrieved.MaxVoltage);

        // AND an electrical query for 230V correctly hits the document via pure bounds:
        var matches = await repo.GetDocumentsByElectricalFilterAsync("3~", 230);
        Assert.Contains(matches, d => d.Id == "doc-legacy");
    }

    [Fact]
    public async Task EnsureDatabaseInitialized_WithPreExistingUnmigratedDatabase_BackfillsExistingRecords()
    {
        // GIVEN an old SQLite database file with rows inserted without MinVoltage/MaxVoltage:
        var legacyDbName = $"legacy_pre_migration_{Guid.NewGuid():N}.db";
        try
        {
            var connStr = $"Data Source={legacyDbName}";
            var options = new DbContextOptionsBuilder<DocumentDbContext>()
                .UseSqlite(connStr)
                .Options;
            var factory = new TestDbContextFactory(options);

            // Create initial table without MinVoltage/MaxVoltage columns using raw SQL
            using (var ctx = factory.CreateDbContext())
            {
                ctx.Database.OpenConnection();
                using var cmd = ctx.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE ""Documents"" (
                        ""Id"" TEXT NOT NULL PRIMARY KEY,
                        ""FileName"" TEXT NOT NULL,
                        ""FilePath"" TEXT NOT NULL,
                        ""UploadedAt"" TEXT NOT NULL,
                        ""Status"" TEXT NOT NULL,
                        ""ErrorMessage"" TEXT NULL,
                        ""Language"" TEXT NULL,
                        ""ArticleId"" TEXT NULL,
                        ""SourceDocumentId"" TEXT NULL,
                        ""ProductCode"" TEXT NULL,
                        ""ProductCategory"" TEXT NULL,
                        ""Phase"" TEXT NULL,
                        ""NominalVoltage"" TEXT NULL,
                        ""VoltageRange"" TEXT NULL,
                        ""PowerWatts"" INTEGER NULL,
                        ""DiameterMm"" INTEGER NULL
                    );
                    INSERT INTO ""Documents"" (""Id"", ""FileName"", ""FilePath"", ""UploadedAt"", ""Status"", ""Phase"", ""NominalVoltage"", ""VoltageRange"")
                    VALUES ('doc-existing-old', 'OldFan.pdf', '/path', '2026-01-01', 'Processed', '1~', '230 VAC', '200 .. 277 VAC');
                ";
                cmd.ExecuteNonQuery();
            }

            // WHEN initializing the repository against this existing database:
            var repo = new SqliteDocumentRepository(factory);
            var retrieved = await repo.GetDocumentByIdAsync("doc-existing-old");

            // THEN the migration added the columns and automatically backfilled existing records:
            Assert.NotNull(retrieved);
            Assert.Equal(200, retrieved.MinVoltage);
            Assert.Equal(277, retrieved.MaxVoltage);

            // AND querying for 230V finds the backfilled existing document via pure SQL:
            var matches = await repo.GetDocumentsByElectricalFilterAsync("1~", 230);
            Assert.Contains(matches, d => d.Id == "doc-existing-old");
        }
        finally
        {
            try { if (File.Exists(legacyDbName)) File.Delete(legacyDbName); } catch { }
        }
    }

    [Fact]
    public async Task getPagedDocumentsAsync_withSortingParameters_shouldSortDatabaseWideAcrossAllRecords()
    {
        // GIVEN a repository containing documents with distinct file names and upload dates:
        var repo = new SqliteDocumentRepository(_factory);

        var docA = new Document(
            Id: "doc-alpha",
            FileName: "Alpha_Brochure.pdf",
            FilePath: "/uploads/alpha.pdf",
            UploadedAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            Status: DocumentStatus.Processed
        );

        var docZ = new Document(
            Id: "doc-zeta",
            FileName: "Zeta_Datasheet.pdf",
            FilePath: "/uploads/zeta.pdf",
            UploadedAt: new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            Status: DocumentStatus.Processed
        );

        var docM = new Document(
            Id: "doc-mike",
            FileName: "Mike_Manual.pdf",
            FilePath: "/uploads/mike.pdf",
            UploadedAt: new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc),
            Status: DocumentStatus.Pending
        );

        await repo.AddDocumentAsync(docA);
        await repo.AddDocumentAsync(docZ);
        await repo.AddDocumentAsync(docM);

        // WHEN retrieving documents sorted by name ASC:
        var (nameAscItems, _) = await repo.GetPagedDocumentsAsync(1, 10, null, null, sortBy: "name", sortDir: "asc");
        var nameAscList = nameAscItems.ToList();

        // THEN records are returned in alphabetical order:
        Assert.Equal("Alpha_Brochure.pdf", nameAscList[0].FileName);
        Assert.Equal("Mike_Manual.pdf", nameAscList[1].FileName);
        Assert.Equal("Zeta_Datasheet.pdf", nameAscList[2].FileName);

        // WHEN retrieving documents sorted by date DESC:
        var (dateDescItems, _) = await repo.GetPagedDocumentsAsync(1, 10, null, null, sortBy: "date", sortDir: "desc");
        var dateDescList = dateDescItems.ToList();

        // THEN records are returned with newest first:
        Assert.Equal("Zeta_Datasheet.pdf", dateDescList[0].FileName);
        Assert.Equal("Mike_Manual.pdf", dateDescList[1].FileName);
        Assert.Equal("Alpha_Brochure.pdf", dateDescList[2].FileName);
    }

    private class TestDbContextFactory : IDbContextFactory<DocumentDbContext>
    {
        private readonly DbContextOptions<DocumentDbContext> _options;
        public TestDbContextFactory(DbContextOptions<DocumentDbContext> options) => _options = options;
        public DocumentDbContext CreateDbContext() => new(_options);
    }
}
