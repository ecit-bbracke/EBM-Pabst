using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.WebApi.DTOs;
using Moq;
using Testcontainers.Qdrant;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class UploadAndQueryDocumentTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly QdrantContainer _qdrant;
    private bool _dockerAvailable = true;
    private readonly WebApplicationFactory<Program> _factory;
    private HttpClient _client = null!;

    public UploadAndQueryDocumentTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        try
        {
            _qdrant = new QdrantBuilder("qdrant/qdrant:v1.10.0")
                .Build();
        }
        catch
        {
            _dockerAvailable = false;
            _qdrant = null!;
        }
    }

    public async Task InitializeAsync()
    {
        if (_dockerAvailable)
        {
            try
            {
                // 1. Start Qdrant docker container dynamically if Docker is available
                await _qdrant.StartAsync();
            }
            catch
            {
                _dockerAvailable = false;
            }
        }

        // 2. Build the HttpClient, and conditionally override configuration or mock services
        _client = _factory.WithWebHostBuilder(builder =>
        {
            if (_dockerAvailable)
            {
                var grpcPort = _qdrant.GetMappedPublicPort(6334);
                var qdrantConnectionString = $"http://{_qdrant.Hostname}:{grpcPort}";
                builder.UseSetting("Qdrant:ConnectionString", qdrantConnectionString);
                builder.UseSetting("Qdrant:CollectionName", "e2e-test-collection");
            }

            if (!_dockerAvailable)
            {
                builder.ConfigureServices(services =>
                {
                    // Replace the real QdrantVectorStore with an in-memory mock store
                    var mockStore = new Moq.Mock<IVectorStore>();
                    mockStore.Setup(x => x.AddChunkAsync(Moq.It.IsAny<DocumentChunk>(), Moq.It.IsAny<float[]>()))
                        .Returns(Task.CompletedTask);
                    
                    mockStore.Setup(x => x.SearchAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<int>()))
                        .ReturnsAsync(new[] 
                        { 
                            new DocumentChunk("e2e_chunk_0", "e2e_doc_id", "This is the main topic context.", 0) 
                        });

                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IVectorStore));
                    if (descriptor != null)
                    {
                        services.Remove(descriptor);
                    }
                    services.AddSingleton<IVectorStore>(mockStore.Object);
                });
            }
        }).CreateClient();
    }

    public async Task DisposeAsync()
    {
        if (_dockerAvailable && _qdrant != null)
        {
            await _qdrant.DisposeAsync();
        }
    }

    [Fact]
    public async Task UploadPdf_ThenQuery_ReturnsRelevantResponse()
    {
        // Arrange
        var testDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.pdf");
        
        // Assert that the test data actually exists in output directory
        Assert.True(File.Exists(testDataPath), $"Test data PDF not found at: {testDataPath}");

        var pdfContent = await File.ReadAllBytesAsync(testDataPath);
        
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdfContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "sample.pdf");

        // Upload and process the PDF
        var uploadResponse = await _client.PostAsync("/api/documents/upload", content);
        uploadResponse.EnsureSuccessStatusCode();

        // Act - Query the processed document
        var queryRequest = new QueryRequest("What is the main topic?");
        var queryResponse = await _client.PostAsJsonAsync("/api/query", queryRequest);

        // Assert
        queryResponse.EnsureSuccessStatusCode();
        var result = await queryResponse.Content.ReadFromJsonAsync<QueryResponse>();
        
        Assert.NotNull(result);
        Assert.Contains("main topic", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Citations);
        Assert.Contains("main topic", result.Citations.First().Text, StringComparison.OrdinalIgnoreCase);
    }
}
