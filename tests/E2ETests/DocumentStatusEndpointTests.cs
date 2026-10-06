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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class DocumentStatusEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryDocumentRepository _repository = new();
    private readonly Mock<IVectorStore> _mockVectorStore = new();

    public DocumentStatusEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateTestClientAsync()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace IDocumentRepository with test instance
                var repoDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IDocumentRepository));
                if (repoDescriptor != null) services.Remove(repoDescriptor);
                services.AddSingleton<IDocumentRepository>(_repository);

                // Replace IVectorStore with mock
                var storeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IVectorStore));
                if (storeDescriptor != null) services.Remove(storeDescriptor);
                services.AddSingleton<IVectorStore>(_mockVectorStore.Object);

                // Replace IEmbeddingService with mock
                var embeddingDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEmbeddingService));
                if (embeddingDescriptor != null) services.Remove(embeddingDescriptor);
                var mockEmbedding = new Mock<IEmbeddingService>();
                mockEmbedding.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>()))
                    .ReturnsAsync(new float[768]);
                services.AddSingleton<IEmbeddingService>(mockEmbedding.Object);

                // Replace ILlmService with mock
                var llmDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmService));
                if (llmDescriptor != null) services.Remove(llmDescriptor);
                var mockLlm = new Mock<ILlmService>();
                services.AddSingleton<ILlmService>(mockLlm.Object);
            });
        }).CreateClient();

        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@ebmpabst.dk", "Admin123!"));
        loginResp.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task GetDocumentStatus_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var unauthenticatedClient = _factory.CreateClient();

        // Act
        var response = await unauthenticatedClient.GetAsync("/api/documents/status?fileName=test.pdf");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDocumentStatus_WhenNotUploaded_ReturnsNotUploaded()
    {
        // Arrange
        _mockVectorStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<Document>());
        var client = await CreateTestClientAsync();

        // Act
        var response = await client.GetAsync("/api/documents/status?fileName=unknown_doc.pdf");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(result);
        Assert.False(result.HasBeenUploaded);
        Assert.False(result.IsUploaded);
        Assert.Equal("NotUploaded", result.Status);
        Assert.Equal("unknown_doc.pdf", result.FileName);
    }

    [Fact]
    public async Task GetDocumentStatus_WhenInRepositoryProcessed_ReturnsProcessed()
    {
        // Arrange
        var doc = new Document(
            Id: "repo-doc-123",
            FileName: "datasheet_k3g.pdf",
            FilePath: "/uploads/repo-doc-123_datasheet_k3g.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed
        );
        await _repository.AddDocumentAsync(doc);
        _mockVectorStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<Document>());
        var client = await CreateTestClientAsync();

        // Act
        var response = await client.GetAsync("/api/documents/status?fileName=datasheet_k3g.pdf");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(result);
        Assert.True(result.HasBeenUploaded);
        Assert.True(result.IsUploaded);
        Assert.Equal("Processed", result.Status);
        Assert.Equal("repo-doc-123", result.DocumentId);
        Assert.Equal("datasheet_k3g.pdf", result.FileName);
    }

    [Fact]
    public async Task GetDocumentStatus_WhenInVectorStoreOnly_ReturnsProcessed()
    {
        // Arrange
        var vectorDoc = new Document(
            Id: "vector-doc-456",
            FileName: "axial_fan_w3g.pdf",
            FilePath: "/uploads/vector-doc-456_axial_fan_w3g.pdf",
            UploadedAt: DateTime.UtcNow.AddHours(-1),
            Status: DocumentStatus.Processed
        );
        _mockVectorStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>()))
            .ReturnsAsync(new[] { vectorDoc });
        var client = await CreateTestClientAsync();

        // Act
        var response = await client.GetAsync("/api/documents/status?fileName=axial_fan_w3g.pdf");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(result);
        Assert.True(result.HasBeenUploaded);
        Assert.True(result.IsUploaded);
        Assert.Equal("Processed", result.Status);
        Assert.Equal("vector-doc-456", result.DocumentId);
    }

    [Fact]
    public async Task GetDocumentStatus_WhenPendingOrFailed_ReflectsStatusCorrectly()
    {
        // Arrange
        var failedDoc = new Document(
            Id: "failed-doc-789",
            FileName: "corrupt.pdf",
            FilePath: "/uploads/corrupt.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Failed,
            ErrorMessage: "Invalid PDF structure."
        );
        await _repository.AddDocumentAsync(failedDoc);
        var client = await CreateTestClientAsync();

        // Act
        var response = await client.GetAsync("/api/documents/status?fileName=corrupt.pdf");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(result);
        Assert.True(result.HasBeenUploaded);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("Invalid PDF structure.", result.ErrorMessage);
    }

    [Fact]
    public async Task GetDocumentStatus_DifferentUrlRoutes_WorkConsistently()
    {
        // Arrange
        var doc = new Document(
            Id: "multi-route-doc",
            FileName: "spec_sheet.pdf",
            FilePath: "/uploads/multi-route-doc_spec_sheet.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed
        );
        await _repository.AddDocumentAsync(doc);
        var client = await CreateTestClientAsync();

        // 1. Query parameter (?fileName=...)
        var respQuery = await client.GetAsync("/api/documents/status?fileName=spec_sheet.pdf");
        Assert.Equal(HttpStatusCode.OK, respQuery.StatusCode);
        var res1 = await respQuery.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(res1);
        Assert.True(res1.HasBeenUploaded);

        // 2. Route parameter (/api/documents/status/spec_sheet.pdf)
        var respPath = await client.GetAsync("/api/documents/status/spec_sheet.pdf");
        Assert.Equal(HttpStatusCode.OK, respPath.StatusCode);
        var res2 = await respPath.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(res2);
        Assert.True(res2.HasBeenUploaded);

        // 3. Resource path (/api/documents/spec_sheet.pdf/status)
        var respResPath = await client.GetAsync("/api/documents/spec_sheet.pdf/status");
        Assert.Equal(HttpStatusCode.OK, respResPath.StatusCode);
        var res3 = await respResPath.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(res3);
        Assert.True(res3.HasBeenUploaded);
    }

    [Fact]
    public async Task GetDocumentStatus_WithoutExtension_MatchesDocument()
    {
        // Arrange
        var doc = new Document(
            Id: "doc-no-ext",
            FileName: "my_special_document.pdf",
            FilePath: "/uploads/my_special_document.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Processed
        );
        await _repository.AddDocumentAsync(doc);
        var client = await CreateTestClientAsync();

        // Act - Query without '.pdf' extension
        var response = await client.GetAsync("/api/documents/status?fileName=my_special_document");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(result);
        Assert.True(result.HasBeenUploaded);
        Assert.Equal("my_special_document.pdf", result.FileName);
    }

    [Fact]
    public async Task GetDocumentStatus_WhenInRepositorySkipped_ReturnsSkippedWithLanguage()
    {
        // Arrange
        var doc = new Document(
            Id: "repo-skipped-123",
            FileName: "Data_sheet_DA_-_8300100049.pdf",
            FilePath: "/uploads/repo-skipped-123_Data_sheet_DA_-_8300100049.pdf",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Skipped,
            ErrorMessage: "Document language is 'da'. Only English documents are embedded.",
            Language: "da"
        );
        await _repository.AddDocumentAsync(doc);
        _mockVectorStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<Document>());
        var client = await CreateTestClientAsync();

        // Act
        var response = await client.GetAsync("/api/documents/status?fileName=Data_sheet_DA_-_8300100049.pdf");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(result);
        Assert.True(result.HasBeenUploaded);
        Assert.True(result.IsUploaded);
        Assert.Equal("Skipped", result.Status);
        Assert.Equal("da", result.Language);
        Assert.Equal("repo-skipped-123", result.DocumentId);
        Assert.Contains("da", result.ErrorMessage);
    }

    [Fact]
    public async Task GetDocumentStatus_MissingFileNameParameter_ReturnsBadRequest()
    {
        // Arrange
        var client = await CreateTestClientAsync();

        // Act
        var response = await client.GetAsync("/api/documents/status");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
