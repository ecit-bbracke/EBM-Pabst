using System;
using System.Collections.Generic;
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

public class DocumentPaginationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryDocumentRepository _repository = new();
    private readonly Mock<IVectorStore> _mockVectorStore = new();
    private const string ValidApiKey = "test-paging-api-key";

    public DocumentPaginationEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IdentityDb", $"Data Source=identity_paging_{Guid.NewGuid():N}.db");
            builder.UseSetting("Authentication:ApiKey", ValidApiKey);

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:ApiKey"] = ValidApiKey
                });
            });

            builder.ConfigureServices(services =>
            {
                var repoDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IDocumentRepository));
                if (repoDescriptor != null) services.Remove(repoDescriptor);
                services.AddSingleton<IDocumentRepository>(_repository);

                var storeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IVectorStore));
                if (storeDescriptor != null) services.Remove(storeDescriptor);
                _mockVectorStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<Document>());
                services.AddSingleton<IVectorStore>(_mockVectorStore.Object);
            });
        });
    }

    private async Task SeedDocumentsAsync(int count)
    {
        for (int i = 1; i <= count; i++)
        {
            var doc = new Document(
                Id: $"doc-{i:D3}",
                FileName: i % 2 == 0 ? $"Axial_Fan_{i}.pdf" : $"Centrifugal_Blower_{i}.pdf",
                FilePath: $"/uploads/doc-{i:D3}.pdf",
                UploadedAt: DateTime.UtcNow.AddMinutes(i),
                Status: DocumentStatus.Processed,
                ArticleId: $"Art_{1000 + i}"
            );
            await _repository.AddDocumentAsync(doc);
        }
    }

    [Fact]
    public async Task GetDocuments_WithoutPagingParams_ReturnsDefaultPagedResult()
    {
        // Arrange
        await SeedDocumentsAsync(5);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        // Act
        var response = await client.GetAsync("/api/documents");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var pagedResult = await response.Content.ReadFromJsonAsync<PagedResult<Document>>();
        pagedResult.Should().NotBeNull();
        pagedResult!.Items.Should().NotBeNull();
        pagedResult.Items.Count().Should().BeGreaterThanOrEqualTo(5);
        pagedResult.Page.Should().Be(1);
        pagedResult.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task GetDocuments_WithPaging_ReturnsPagedResult()
    {
        // Arrange
        await SeedDocumentsAsync(5);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        // Act
        var response = await client.GetAsync("/api/documents?page=1&pageSize=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<Document>>();
        paged.Should().NotBeNull();
        paged!.Page.Should().Be(1);
        paged.PageSize.Should().Be(2);
        paged.TotalCount.Should().BeGreaterThanOrEqualTo(5);
        paged.Items.Should().HaveCount(2);
        paged.HasPreviousPage.Should().BeFalse();
        paged.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public async Task GetDocuments_Page2_ReturnsNextSlice()
    {
        // Arrange
        await SeedDocumentsAsync(5);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        // Act
        var response = await client.GetAsync("/api/documents?page=2&pageSize=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<Document>>();
        paged.Should().NotBeNull();
        paged!.Page.Should().Be(2);
        paged.PageSize.Should().Be(2);
        paged.Items.Should().HaveCount(2);
        paged.HasPreviousPage.Should().BeTrue();
    }

    [Fact]
    public async Task GetDocuments_WithSearchFilter_ReturnsFilteredResults()
    {
        // Arrange
        await SeedDocumentsAsync(6);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        // Act - search for 'Centrifugal'
        var response = await client.GetAsync("/api/documents?page=1&pageSize=10&search=Centrifugal");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<Document>>();
        paged.Should().NotBeNull();
        paged!.Items.Should().NotBeEmpty();
        paged.Items.Should().OnlyContain(d => d.FileName.Contains("Centrifugal", StringComparison.OrdinalIgnoreCase));
    }
}
