using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.WebApi.DTOs;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class DocumentDeletionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryDocumentRepository _repository = new();
    private readonly Mock<IVectorStore> _mockVectorStore = new();
    private const string ValidApiKey = "test-delete-api-key";
    private readonly string _testUploadsDir;

    public DocumentDeletionEndpointTests(WebApplicationFactory<Program> factory)
    {
        _testUploadsDir = Path.Combine(Path.GetTempPath(), $"uploads_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testUploadsDir);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IdentityDb", $"Data Source=identity_delete_{Guid.NewGuid():N}.db");
            builder.UseSetting("Authentication:ApiKey", ValidApiKey);
            builder.UseSetting("Uploads:Directory", _testUploadsDir);

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:ApiKey"] = ValidApiKey,
                    ["Uploads:Directory"] = _testUploadsDir
                });
            });

            builder.ConfigureServices(services =>
            {
                var repoDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IDocumentRepository));
                if (repoDescriptor != null) services.Remove(repoDescriptor);
                services.AddSingleton<IDocumentRepository>(_repository);

                var storeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IVectorStore));
                if (storeDescriptor != null) services.Remove(storeDescriptor);
                services.AddSingleton<IVectorStore>(_mockVectorStore.Object);
            });
        });
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@ebmpabst.dk", "Admin123!"));
        loginResponse.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task DeleteDocument_WithoutAuth_RequiresAuthentication()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.DeleteAsync("/api/documents/doc-123");

        (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Redirect).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteDocument_WhenNotFound_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync();

        _mockVectorStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<Document>());

        var response = await client.DeleteAsync($"/api/documents/non-existent-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteDocument_WhenExists_DeletesFile_Chunks_AndRepositoryRecord()
    {
        var client = await CreateAuthenticatedClientAsync();

        var docId = $"doc_{Guid.NewGuid():N}";
        var fileName = "sample_test_doc.pdf";
        var uniqueDiskFileName = $"{Guid.NewGuid():N}_{fileName}";
        var physicalFilePath = Path.Combine(_testUploadsDir, uniqueDiskFileName);
        await File.WriteAllTextAsync(physicalFilePath, "%PDF-1.4 test file content");

        var doc = new Document(
            Id: docId,
            FileName: fileName,
            FilePath: $"/uploads/{uniqueDiskFileName}",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed
        );

        await _repository.AddDocumentAsync(doc);

        _mockVectorStore
            .Setup(x => x.DeleteDocumentChunksAsync(docId))
            .Returns(Task.CompletedTask)
            .Verifiable();

        // Act
        var response = await client.DeleteAsync($"/api/documents/{docId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify file deleted
        File.Exists(physicalFilePath).Should().BeFalse();

        // Verify chunks deleted in vector store
        _mockVectorStore.Verify(x => x.DeleteDocumentChunksAsync(docId), Times.Once);

        // Verify removed from repository
        var retrieved = await _repository.GetDocumentByIdAsync(docId);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task DeleteDocument_WhenVectorStoreThrows_Returns500_AndRetainsDocumentInRepository()
    {
        var client = await CreateAuthenticatedClientAsync();

        var docId = $"doc_failing_{Guid.NewGuid():N}";
        var fileName = "failing_store_doc.pdf";
        var uniqueDiskFileName = $"{Guid.NewGuid():N}_{fileName}";
        var physicalFilePath = Path.Combine(_testUploadsDir, uniqueDiskFileName);
        await File.WriteAllTextAsync(physicalFilePath, "%PDF-1.4 test file content");

        var doc = new Document(
            Id: docId,
            FileName: fileName,
            FilePath: $"/uploads/{uniqueDiskFileName}",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed
        );

        await _repository.AddDocumentAsync(doc);

        _mockVectorStore
            .Setup(x => x.DeleteDocumentChunksAsync(docId, It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Qdrant connection dropped during deletion."));

        // Act
        var response = await client.DeleteAsync($"/api/documents/{docId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // Document record must be retained in repository so deletion can be retried!
        var retained = await _repository.GetDocumentByIdAsync(docId);
        retained.Should().NotBeNull();
        retained!.Id.Should().Be(docId);
    }

    [Fact]
    public async Task DeleteDocument_DeletesBothCompletedFile_AndAnyStagedGuidPrefixedFiles()
    {
        var client = await CreateAuthenticatedClientAsync();

        var docId = $"doc_staged_{Guid.NewGuid():N}";
        var fileName = "staging_cleanup_target.pdf";
        var completedDiskFileName = $"{Guid.NewGuid()}_{fileName}";
        var completedPath = Path.Combine(_testUploadsDir, completedDiskFileName);
        await File.WriteAllTextAsync(completedPath, "%PDF-1.4 completed");

        // Staging files left by background uploads (both 36-char and 32-char GUIDs)
        var staged36 = Path.Combine(_testUploadsDir, $"{Guid.NewGuid()}_{fileName}");
        await File.WriteAllTextAsync(staged36, "%PDF-1.4 staged 36");

        var staged32 = Path.Combine(_testUploadsDir, $"{Guid.NewGuid():N}_{fileName}");
        await File.WriteAllTextAsync(staged32, "%PDF-1.4 staged 32");

        var doc = new Document(
            Id: docId,
            FileName: fileName,
            FilePath: $"/uploads/{completedDiskFileName}",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed
        );

        await _repository.AddDocumentAsync(doc);

        _mockVectorStore
            .Setup(x => x.DeleteDocumentChunksAsync(docId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var response = await client.DeleteAsync($"/api/documents/{docId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // All variations must be deleted from disk
        File.Exists(completedPath).Should().BeFalse();
        File.Exists(staged36).Should().BeFalse();
        File.Exists(staged32).Should().BeFalse();
    }
}
