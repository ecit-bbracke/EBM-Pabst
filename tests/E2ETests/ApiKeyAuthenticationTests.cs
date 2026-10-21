using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.WebApi.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class ApiKeyAuthenticationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string ValidTestApiKey = "test-e2e-api-key-999";

    public ApiKeyAuthenticationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IdentityDb", $"Data Source=identity_apikey_{Guid.NewGuid():N}.db");
            builder.UseSetting("Authentication:ApiKey", ValidTestApiKey);

            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:ApiKey"] = ValidTestApiKey
                });
            });

            builder.ConfigureServices(services =>
            {
                var storeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IVectorStore));
                if (storeDescriptor != null) services.Remove(storeDescriptor);
                var mockStore = new Mock<IVectorStore>();
                mockStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<Document>());
                services.AddSingleton<IVectorStore>(mockStore.Object);

                var procDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IDocumentProcessor));
                if (procDescriptor != null) services.Remove(procDescriptor);
                var mockProcessor = new Mock<IDocumentProcessor>();
                mockProcessor.Setup(x => x.ProcessPdfAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Document("doc-test", "test.pdf", "/uploads/test.pdf", DateTime.UtcNow, DocumentStatus.Processed));
                services.AddSingleton<IDocumentProcessor>(mockProcessor.Object);
            });
        });
    }

    private static MultipartFormDataContent CreateTestPdfContent()
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("%PDF-1.4 sample test content"u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "sample_upload.pdf");
        return content;
    }

    [Fact]
    public async Task UploadDocument_WithValidXApiKeyHeader_ReturnsSuccess()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        // Act - API key MUST be allowed on document upload
        using var content = CreateTestPdfContent();
        var response = await client.PostAsync("/api/documents/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UploadDocument_WithValidBearerAuthorizationHeader_ReturnsSuccess()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidTestApiKey);

        // Act - API key via Bearer header MUST be allowed on document upload
        using var content = CreateTestPdfContent();
        var response = await client.PostAsync("/api/documents/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetDocumentStatus_WithValidApiKey_ReturnsSuccess()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        // Act - API key MUST be allowed on document upload status checks
        var response = await client.GetAsync("/api/documents/status?fileName=test.pdf");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetDocuments_WithApiKey_ReturnsForbidden()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        // Act - API key MUST NOT have access to general document listing/search
        var response = await client.GetAsync("/api/documents");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostQuery_WithApiKey_ReturnsForbidden()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        // Act - API key MUST NOT have access to chat queries
        var response = await client.PostAsJsonAsync("/api/query", new QueryRequest("Spænding?"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAdminUsers_WithApiKey_ReturnsForbidden()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        // Act - API key MUST NOT have administrative access
        var response = await client.GetAsync("/api/admin/users");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UploadDocument_WithInvalidApiKey_ReturnsUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-secret-key-12345");

        // Act
        using var content = CreateTestPdfContent();
        var response = await client.PostAsync("/api/documents/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadDocument_WithoutAnyApiKeyOrCookie_ReturnsUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        using var content = CreateTestPdfContent();
        var response = await client.PostAsync("/api/documents/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/uploads/../../appsettings.json")]
    [InlineData("/uploads/..%2f..%2fappsettings.json")]
    [InlineData("/uploads/%2e%2e%2f%2e%2e%2fappsettings.json")]
    [InlineData("/uploads/subfolder/../../secret.pdf")]
    public async Task Uploads_PathTraversal_ReturnsNotFound(string traversalUrl)
    {
        // Arrange
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@ebmpabst.dk", "Admin123!"));
        loginResponse.EnsureSuccessStatusCode();

        // Act - Attempt path traversal outside uploads directory
        var response = await client.GetAsync(traversalUrl);

        // Assert - Path traversal MUST be rejected with 404 Not Found
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Uploads_WhenRequestedWithApiKey_ReturnsForbidden()
    {
        // Arrange: Client authenticated via upload-only API key
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        // Act: Attempt to download/view a protected PDF via /uploads
        var response = await client.GetAsync("/uploads/sample.pdf");

        // Assert: Upload-only API key MUST be rejected with 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
