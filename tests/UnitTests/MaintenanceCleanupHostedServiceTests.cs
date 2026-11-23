using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Data;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.WebApi.HostedServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class MaintenanceCleanupHostedServiceTests : IDisposable
{
    private readonly string _dbFileName;
    private readonly IDbContextFactory<DocumentDbContext> _factory;
    private readonly SqliteDocumentRepository _repository;
    private readonly IServiceProvider _serviceProvider;

    public MaintenanceCleanupHostedServiceTests()
    {
        _dbFileName = $"test_cleanup_{Guid.NewGuid():N}.db";
        var options = new DbContextOptionsBuilder<DocumentDbContext>()
            .UseSqlite($"Data Source={_dbFileName}")
            .Options;

        var mockFactory = new TestDbContextFactory(options);
        _factory = mockFactory;

        using var ctx = mockFactory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _repository = new SqliteDocumentRepository(_factory);

        var services = new ServiceCollection();
        services.AddSingleton<IDocumentRepository>(_repository);
        _serviceProvider = services.BuildServiceProvider();
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
    public async Task MaintenanceCycle_HealsCorruptedDocuments_AndLeavesProcessedUntouched()
    {
        // Arrange
        // 1. Corrupted doc (English, failed due to disk check)
        var corruptedDoc1 = new Document(
            Id: "doc-corrupted-1",
            FileName: "test1.pdf",
            FilePath: "/uploads/missing1.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Failed,
            ErrorMessage: "Filen blev ikke fundet på disk.",
            Language: "en"
        );
        // 2. Corrupted doc (Non-English, failed due to disk check)
        var corruptedDoc2 = new Document(
            Id: "doc-corrupted-2",
            FileName: "test2.pdf",
            FilePath: "/uploads/missing2.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Failed,
            ErrorMessage: "Filen blev ikke fundet på disk.",
            Language: "da"
        );
        // 3. Normal processed doc without physical file on disk
        var normalDoc = new Document(
            Id: "doc-normal-3",
            FileName: "test3.pdf",
            FilePath: "/uploads/missing3.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed,
            ErrorMessage: null,
            Language: "en"
        );

        await _repository.AddDocumentAsync(corruptedDoc1);
        await _repository.AddDocumentAsync(corruptedDoc2);
        await _repository.AddDocumentAsync(normalDoc);

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new MaintenanceCleanupHostedService(scopeFactory, NullLogger<MaintenanceCleanupHostedService>.Instance);

        // Act - run maintenance cycle via reflection or stopping token with short cancel
        using var cts = new CancellationTokenSource();
        var method = typeof(MaintenanceCleanupHostedService)
            .GetMethod("PerformMaintenanceCycleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull();

        var task = (Task)method!.Invoke(service, new object[] { cts.Token })!;
        await task;

        // Assert
        var healed1 = await _repository.GetDocumentByIdAsync("doc-corrupted-1");
        healed1.Should().NotBeNull();
        healed1!.Status.Should().Be(DocumentStatus.Processed);
        healed1.ErrorMessage.Should().BeNull();

        var healed2 = await _repository.GetDocumentByIdAsync("doc-corrupted-2");
        healed2.Should().NotBeNull();
        healed2!.Status.Should().Be(DocumentStatus.Skipped);
        healed2.ErrorMessage.Should().BeNull();

        var preserved3 = await _repository.GetDocumentByIdAsync("doc-normal-3");
        preserved3.Should().NotBeNull();
        preserved3!.Status.Should().Be(DocumentStatus.Processed);
        preserved3.ErrorMessage.Should().BeNull();
    }

    private class TestDbContextFactory : IDbContextFactory<DocumentDbContext>
    {
        private readonly DbContextOptions<DocumentDbContext> _options;
        public TestDbContextFactory(DbContextOptions<DocumentDbContext> options) => _options = options;
        public DocumentDbContext CreateDbContext() => new(_options);
        public Task<DocumentDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DocumentDbContext(_options));
    }
}
