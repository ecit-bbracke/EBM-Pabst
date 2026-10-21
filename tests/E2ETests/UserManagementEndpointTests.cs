using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.WebApi.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class UserManagementEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Moq.Mock<IVectorStore> _mockVectorStore = new();

    public UserManagementEndpointTests(WebApplicationFactory<Program> factory)
    {
        _mockVectorStore
            .Setup(x => x.GetDocumentsAsync(It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<Document>());

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IdentityDb", $"Data Source=identity_user_{Guid.NewGuid():N}.db");
            builder.ConfigureServices(services =>
            {
                var storeDescriptor = System.Linq.Enumerable.SingleOrDefault(services, d => d.ServiceType == typeof(IVectorStore));
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

    private async Task<HttpClient> CreateNonAdminClientAsync()
    {
        var adminClient = await CreateAuthenticatedClientAsync();
        var normalEmail = $"employee_{Guid.NewGuid():N}@ebmpabst.dk";
        var createResp = await adminClient.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(normalEmail, "Password123!"));
        createResp.EnsureSuccessStatusCode();

        var nonAdminClient = _factory.CreateClient();
        var loginResponse = await nonAdminClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(normalEmail, "Password123!"));
        loginResponse.EnsureSuccessStatusCode();
        return nonAdminClient;
    }

    [Fact]
    public async Task GetAdminUsers_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var unauthenticatedClient = _factory.CreateClient();
        var response = await unauthenticatedClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAdminUsers_WhenNonAdminUser_ReturnsForbidden()
    {
        var nonAdminClient = await CreateNonAdminClientAsync();
        var response = await nonAdminClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UsersPage_WhenNonAdminUser_RedirectsToIndex()
    {
        var nonAdminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var normalEmail = $"employee_{Guid.NewGuid():N}@ebmpabst.dk";
        var adminClient = await CreateAuthenticatedClientAsync();
        await adminClient.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(normalEmail, "Password123!"));
        await nonAdminClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(normalEmail, "Password123!"));

        var response = await nonAdminClient.GetAsync("/users.html");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.True(location.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase), $"Expected redirect to end with /index.html, but was {location}");
    }

    [Fact]
    public async Task GetAdminUsers_WhenAuthenticatedAdmin_ReturnsUsersList()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<UserSummaryDto>>();

        Assert.NotNull(users);
        Assert.NotEmpty(users);
        Assert.Contains(users, u => u.Email.Equals("admin@ebmpabst.dk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAdminUser_CreatesNewUserSuccessfully()
    {
        var client = await CreateAuthenticatedClientAsync();
        var newEmail = $"user_{Guid.NewGuid():N}@ebmpabst.dk";
        var createRequest = new CreateUserRequest(newEmail, "Password123!");

        var response = await client.PostAsJsonAsync("/api/admin/users", createRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<UserSummaryDto>();
        Assert.NotNull(created);
        Assert.Equal(newEmail, created.Email);

        // Verify the user can log in with new credentials
        var userClient = _factory.CreateClient();
        var loginResp = await userClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(newEmail, "Password123!"));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
    }

    [Fact]
    public async Task DeleteAdminUser_WhenAttemptingToDeleteDefaultAdmin_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();
        var usersResponse = await client.GetAsync("/api/admin/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<List<UserSummaryDto>>();
        var defaultAdmin = users!.Find(u => u.Email.Equals("admin@ebmpabst.dk", StringComparison.OrdinalIgnoreCase));

        var response = await client.DeleteAsync($"/api/admin/users/{defaultAdmin!.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CleanOrphanedDocuments_RemovesRecordsWithMissingUploadFilesAndNoVectors()
    {
        var client = await CreateAuthenticatedClientAsync();
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        var orphanId = $"orphan_{Guid.NewGuid():N}";
        var orphanDoc = new Document(
            Id: orphanId,
            FileName: "completely_missing.pdf",
            FilePath: "/uploads/completely_missing.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-10),
            Status: DocumentStatus.Processed
        );
        await repo.AddDocumentAsync(orphanDoc);

        // Act: Run orphan cleanup
        var response = await client.PostAsync("/api/admin/clean-orphans", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert: The orphan document was removed from repository because its physical file and vectors are gone
        var retrieved = await repo.GetDocumentByIdAsync(orphanId);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task CleanOrphanedDocuments_WhenVectorStoreThrows_ReturnsServiceUnavailableAndDeletesNoRecords()
    {
        var client = await CreateAuthenticatedClientAsync();
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        var docId = $"orphan_fail_{Guid.NewGuid():N}";
        var doc = new Document(
            Id: docId,
            FileName: "missing_store_down.pdf",
            FilePath: "/uploads/missing_store_down.pdf",
            UploadedAt: DateTime.UtcNow.AddDays(-10),
            Status: DocumentStatus.Processed
        );
        await repo.AddDocumentAsync(doc);

        _mockVectorStore
            .Setup(x => x.GetDocumentsAsync(It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Qdrant connection dropped"));

        try
        {
            // Act: Run orphan cleanup when Qdrant is down
            var response = await client.PostAsync("/api/admin/clean-orphans", null);

            // Assert: Must return 503 Service Unavailable and abort cleanup without deleting metadata!
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var stillExists = await repo.GetDocumentByIdAsync(docId);
            Assert.NotNull(stillExists);
        }
        finally
        {
            // Reset mock
            _mockVectorStore
                .Setup(x => x.GetDocumentsAsync(It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new List<Document>());
        }
    }
}
