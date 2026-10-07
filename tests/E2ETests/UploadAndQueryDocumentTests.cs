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
            builder.UseSetting("ConnectionStrings:IdentityDb", $"Data Source=identity_upload_{Guid.NewGuid():N}.db");

            if (_dockerAvailable)
            {
                var grpcPort = _qdrant.GetMappedPublicPort(6334);
                var qdrantConnectionString = $"http://{_qdrant.Hostname}:{grpcPort}";
                builder.UseSetting("Qdrant:ConnectionString", qdrantConnectionString);
                builder.UseSetting("Qdrant:CollectionName", "e2e-test-collection");
            }

            builder.ConfigureServices(services =>
            {
                var mockEmbedding = new Moq.Mock<IEmbeddingService>();
                mockEmbedding.Setup(x => x.GenerateEmbeddingAsync(Moq.It.IsAny<string>()))
                    .ReturnsAsync(new float[768]);

                var embeddingDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEmbeddingService));
                if (embeddingDescriptor != null) services.Remove(embeddingDescriptor);
                services.AddSingleton<IEmbeddingService>(mockEmbedding.Object);

                var mockLlm = new Moq.Mock<ILlmService>();
                mockLlm.Setup(x => x.GenerateCompletionAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<bool>()))
                    .ReturnsAsync("""
                    {
                      "intent": "SPEC_LOOKUP",
                      "confidence": 0.95,
                      "entities": [],
                      "requested_attributes": [],
                      "constraints": [],
                      "clarification_required": false,
                      "clarification_reason": null
                    }
                    """);
                mockLlm.Setup(x => x.GenerateResponseAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<IEnumerable<DocumentChunk>>()))
                    .ReturnsAsync("This is the main topic response based on uploaded document context.");

                var llmDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmService));
                if (llmDescriptor != null) services.Remove(llmDescriptor);
                services.AddSingleton<ILlmService>(mockLlm.Object);

                if (!_dockerAvailable)
                {
                    // Replace the real QdrantVectorStore with an in-memory mock store
                    var storedChunks = new List<DocumentChunk>();
                    var mockStore = new Moq.Mock<IVectorStore>();
                    mockStore.Setup(x => x.AddChunkAsync(Moq.It.IsAny<DocumentChunk>(), Moq.It.IsAny<float[]>()))
                        .Callback<DocumentChunk, float[]>((chunk, _) => storedChunks.Add(chunk))
                        .Returns(Task.CompletedTask);
                    
                    mockStore.Setup(x => x.SearchAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<int>()))
                        .ReturnsAsync(() => storedChunks.Count > 0 
                            ? storedChunks.Take(3).ToList() 
                            : new List<DocumentChunk> { new("e2e_chunk_0", "e2e_doc_id", "This is the main topic context.", 0) });

                    mockStore.Setup(x => x.GetChunksByDocumentAndIndicesAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<IEnumerable<int>>()))
                        .ReturnsAsync((string docId, IEnumerable<int> indices) => 
                            storedChunks.Where(c => c.DocumentId == docId && indices.Contains(c.Index)).ToList());

                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IVectorStore));
                    if (descriptor != null)
                    {
                        services.Remove(descriptor);
                    }
                    services.AddSingleton<IVectorStore>(mockStore.Object);
                }
            });
        }).CreateClient();

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@ebmpabst.dk", "Admin123!"));
        loginResponse.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        if (_dockerAvailable && _qdrant != null)
        {
            await _qdrant.DisposeAsync();
        }
    }

    private static string GetTestDataPath(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", filename);
        if (File.Exists(path)) return path;

        var flatPath = Path.Combine(AppContext.BaseDirectory, $"TestData\\{filename}");
        if (File.Exists(flatPath)) return flatPath;

        var repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "TestData", filename));
        if (File.Exists(repoPath)) return repoPath;

        return path;
    }

    [Fact]
    public async Task UploadPdf_ThenQuery_ReturnsRelevantResponse()
    {
        // Arrange
        var testDataPath = GetTestDataPath("sample.pdf");
        
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
        Assert.NotEmpty(result.Citations);
    }

    [Fact]
    public async Task UploadPdf_WhenNonEnglish_SavesFile_AndSkipsEmbedding()
    {
        // Arrange
        var testDataPath = GetTestDataPath("sample.pdf");
        Assert.True(File.Exists(testDataPath), $"Test data PDF not found at: {testDataPath}");

        var pdfContent = await File.ReadAllBytesAsync(testDataPath);
        
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdfContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        var nonEnglishFileName = "Data_sheet_DA_-_8300100049.pdf";
        content.Add(fileContent, "file", nonEnglishFileName);

        // Act - Upload the PDF
        var uploadResponse = await _client.PostAsync("/api/documents/upload", content);
        uploadResponse.EnsureSuccessStatusCode();

        var uploadedDoc = await uploadResponse.Content.ReadFromJsonAsync<Document>();
        Assert.NotNull(uploadedDoc);
        Assert.Equal(DocumentStatus.Skipped, uploadedDoc.Status);
        Assert.Equal("da", uploadedDoc.Language);
        Assert.NotNull(uploadedDoc.FilePath);

        // Verify the physical file was saved to the uploads directory
        var cleanFileName = Path.GetFileName(Uri.UnescapeDataString(uploadedDoc.FilePath));
        var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
        var physicalPath = Path.Combine(uploadsDir, cleanFileName);
        Assert.True(File.Exists(physicalPath), $"Expected file {physicalPath} to exist on disk.");

        // Clean up uploaded test file
        try { File.Delete(physicalPath); } catch { }

        // Verify status endpoint reflects Skipped status and language
        var statusResponse = await _client.GetAsync($"/api/documents/status?fileName={nonEnglishFileName}");
        statusResponse.EnsureSuccessStatusCode();
        var statusResult = await statusResponse.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(statusResult);
        Assert.True(statusResult.HasBeenUploaded);
        Assert.Equal("Skipped", statusResult.Status);
        Assert.Equal("da", statusResult.Language);
    }

    [Fact]
    public async Task Query_WhenReferencedDocumentHasDanishCompanion_IncludesDanishFileReferenceInCitations()
    {
        // Arrange
        var enDataPath = GetTestDataPath("sample.pdf");
        var daDataPath = GetTestDataPath("sample_da.pdf");

        var enPdfContent = await File.ReadAllBytesAsync(enDataPath);
        var daPdfContent = await File.ReadAllBytesAsync(daDataPath);

        // Upload English PDF (no language code in filename)
        using var contentEn = new MultipartFormDataContent();
        var fileContentEn = new ByteArrayContent(enPdfContent);
        fileContentEn.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        contentEn.Add(fileContentEn, "file", "Art_9293512011-Doc_1001.pdf");
        var uploadEnResponse = await _client.PostAsync("/api/documents/upload", contentEn);
        uploadEnResponse.EnsureSuccessStatusCode();

        // Upload Danish companion PDF (no language code in filename)
        using var contentDa = new MultipartFormDataContent();
        var fileContentDa = new ByteArrayContent(daPdfContent);
        fileContentDa.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        contentDa.Add(fileContentDa, "file", "Art_9293512011-Doc_2002.pdf");
        var uploadDaResponse = await _client.PostAsync("/api/documents/upload", contentDa);
        uploadDaResponse.EnsureSuccessStatusCode();

        // Act - Query
        var queryRequest = new QueryRequest("What is the main topic for 9293512011?");
        var queryResponse = await _client.PostAsJsonAsync("/api/query", queryRequest);

        // Assert
        queryResponse.EnsureSuccessStatusCode();
        var result = await queryResponse.Content.ReadFromJsonAsync<QueryResponse>();
        Assert.NotNull(result);
        Assert.NotEmpty(result.Citations);

        // Verify Danish companion is present in citations
        var danishCitation = result.Citations.FirstOrDefault(c => 
            c.filename.Contains("Art_9293512011-Doc_2002.pdf", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(danishCitation);
        Assert.Equal("da", danishCitation.language);
        Assert.True(danishCitation.isCompanion);
    }
}
