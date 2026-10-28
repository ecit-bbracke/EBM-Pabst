using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.WebApi.DTOs;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class QueryStreamEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryDocumentRepository _repository = new();
    private readonly Mock<IVectorStore> _mockVectorStore = new();
    private readonly Mock<ILlmClient> _mockLlmClient = new();
    private readonly Mock<ILlmClientResolver> _mockResolver = new();
    private const string ValidApiKey = "test-stream-api-key";

    public QueryStreamEndpointTests(WebApplicationFactory<Program> factory)
    {
        _mockResolver.Setup(x => x.Resolve(It.IsAny<LlmPurpose>()))
            .Returns(_mockLlmClient.Object);
        _mockResolver.Setup(x => x.Resolve(It.IsAny<string>()))
            .Returns(_mockLlmClient.Object);

        _mockLlmClient
            .Setup(x => x.GenerateStructuredAsync<OutputGovernorResult>(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmStructuredResult<OutputGovernorResult>(
                new OutputGovernorResult(true, new List<OutputGovernorIssue>(), "APPROVE"),
                new LlmExecutionMetadata("Mock", "mock-model", 10, 10, 10, true, 1)));

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IdentityDb", $"Data Source=identity_stream_{Guid.NewGuid():N}.db");
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
                services.AddSingleton<IVectorStore>(_mockVectorStore.Object);

                var resolverDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmClientResolver));
                if (resolverDescriptor != null) services.Remove(resolverDescriptor);
                services.AddSingleton<ILlmClientResolver>(_mockResolver.Object);
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
    public async Task PostQueryStream_WithoutAuth_ReturnsUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = new QueryRequest("Hvad er spændingen for K3G560PC0401?");

        // Act
        var response = await client.PostAsJsonAsync("/api/query/stream", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostQueryStream_WithApiKey_ReturnsSuccess()
    {
        // Arrange
        TechnicalRagOrchestrator.InvalidateQueryCache();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);
        var request = new QueryRequest("Spænding for test model via API?");

        var testChunks = new List<DocumentChunk>
        {
            new DocumentChunk("10327430_chunk_4", "10327430", "Spændingsområde 380-480 VAC, 50/60 Hz", 4, "Art_8300102253-Doc_10327430.pdf", "/uploads/doc1.pdf")
        };
        _mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(testChunks);

        _mockLlmClient.Setup(x => x.StreamTextAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { "Spændingen", " er", " 400V." }.ToAsyncEnumerable());

        // Act - API key MUST have access to streaming queries via QueryPolicy
        var response = await client.PostAsJsonAsync("/api/query/stream", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostQueryStream_WithValidQuery_StreamsSseCitationsAndChunks()
    {
        // Arrange
        TechnicalRagOrchestrator.InvalidateQueryCache();
        var client = await CreateAuthenticatedClientAsync();

        // Mock vector store chunks
        var testChunks = new List<DocumentChunk>
        {
            new DocumentChunk("10327430_chunk_4", "10327430", "Spændingsområde 380-480 VAC, 50/60 Hz", 4, "Art_8300102253-Doc_10327430.pdf", "/uploads/doc1.pdf")
        };
        _mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(testChunks);

        // Mock LLM streaming response
        _mockLlmClient.Setup(x => x.StreamTextAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { "Driftsspændingen er ", "380-480 VAC." }.ToAsyncEnumerable());

        var queryRequest = new QueryRequest("Hvad er spændingen for K3G560PC0401?");

        // Act - Call the streaming SSE endpoint with HttpCompletionOption.ResponseHeadersRead
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/query/stream")
        {
            Content = JsonContent.Create(queryRequest)
        };
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

        // Assert HTTP headers
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        // Read and parse SSE stream
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var eventsReceived = new List<(string EventType, string Data)>();
        string? currentEvent = null;

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (line.StartsWith("event: "))
            {
                currentEvent = line.Substring("event: ".Length).Trim();
            }
            else if (line.StartsWith("data: "))
            {
                var data = line.Substring("data: ".Length).Trim();
                if (!string.IsNullOrEmpty(currentEvent))
                {
                    eventsReceived.Add((currentEvent, data));
                }
            }
        }

        // Verify SSE Events
        eventsReceived.Should().NotBeEmpty();

        // 1. Must receive citations event
        var citationEvent = eventsReceived.FirstOrDefault(e => e.EventType == "citations");
        citationEvent.Should().NotBeNull();
        citationEvent.Data.Should().Contain("\"citations\"");
        citationEvent.Data.Should().Contain("\"index\":4", "Citations event must serialize index as camelCase.");
        citationEvent.Data.Should().Contain("Art_8300102253-Doc_10327430.pdf");

        // 2. Must receive chunk events
        var chunkEvents = eventsReceived.Where(e => e.EventType == "chunk").ToList();
        chunkEvents.Should().NotBeEmpty();
        var streamedFullText = string.Join("", chunkEvents.Select(c =>
        {
            using var doc = JsonDocument.Parse(c.Data);
            return doc.RootElement.GetProperty("text").GetString();
        }));
        streamedFullText.Should().Match(s => s.Contains("380-480") || s.Contains("380–480"));

        // 3. Must receive done event
        var doneEvent = eventsReceived.FirstOrDefault(e => e.EventType == "done");
        doneEvent.Should().NotBeNull();
    }
}
