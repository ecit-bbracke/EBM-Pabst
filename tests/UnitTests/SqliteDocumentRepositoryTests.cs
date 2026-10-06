using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.WebApi.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class SqliteDocumentRepositoryTests : IDisposable
{
    private readonly string _dbFileName;
    private readonly IDbContextFactory<ApplicationDbContext> _factory;

    public SqliteDocumentRepositoryTests()
    {
        _dbFileName = $"test_repo_{Guid.NewGuid():N}.db";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
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

    private class TestDbContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> _options;
        public TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) => _options = options;
        public ApplicationDbContext CreateDbContext() => new(_options);
    }
}
