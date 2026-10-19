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
using DocumentRagSystem.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class UploadPerformanceOptimizationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _testTempDir;
    private readonly InMemoryDocumentRepository _repository = new();
    private readonly Mock<IVectorStore> _mockVectorStore = new();
    private const string ValidApiKey = "test-opt-api-key";

    public UploadPerformanceOptimizationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IdentityDb", $"Data Source=identity_opt_{Guid.NewGuid():N}.db");
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
            });
        });

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        _testTempDir = Path.Combine(Path.GetTempPath(), $"rag_cache_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);
    }

    private static string GetTestDataPath(string fileName)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var testDataPath = Path.Combine(current, "tests", "TestData", fileName);
            if (File.Exists(testDataPath)) return testDataPath;
            var directTestDataPath = Path.Combine(current, "TestData", fileName);
            if (File.Exists(directTestDataPath)) return directTestDataPath;
            current = Path.GetDirectoryName(current);
        }
        throw new FileNotFoundException($"Test data file not found: {fileName}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch { }

        Program.DirFileCache.Clear();
    }

    [Fact]
    public void DirFileCache_CachesDirectoryListings_AndClearsOnInvalidation()
    {
        // Arrange
        var initialFile = Path.Combine(_testTempDir, "file_a.pdf");
        File.WriteAllText(initialFile, "sample");

        Program.DirFileCache.Clear();

        // Act 1: Populate cache
        Program.DirFileCache[_testTempDir] = (DateTime.UtcNow, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file_a.pdf" });

        Program.DirFileCache.TryGetValue(_testTempDir, out var cachedEntry).Should().BeTrue();
        cachedEntry.Files.Should().Contain("file_a.pdf");
        cachedEntry.Files.Should().NotContain("file_b.pdf");

        // Act 2: Add a new file to disk
        var secondFile = Path.Combine(_testTempDir, "file_b.pdf");
        File.WriteAllText(secondFile, "sample 2");

        // Cache still holds old snapshot before invalidation
        Program.DirFileCache.TryGetValue(_testTempDir, out var snapshot).Should().BeTrue();
        snapshot.Files.Should().NotContain("file_b.pdf");

        // Act 3: Invalidate cache
        Program.DirFileCache.Clear();

        // Assert
        Program.DirFileCache.TryGetValue(_testTempDir, out _).Should().BeFalse();
    }

    [Fact]
    public async Task UploadPdf_AutomaticallyClears_DirFileCache()
    {
        // Arrange
        Program.DirFileCache["dummy_directory"] = (DateTime.UtcNow, new HashSet<string> { "dummy.pdf" });
        Program.DirFileCache.Count.Should().BeGreaterThan(0);

        var samplePdfPath = GetTestDataPath("sample.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(samplePdfPath);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", $"cache_invalidation_test_{Guid.NewGuid():N}.pdf");

        // Act - upload document
        var response = await _client.PostAsync("/api/documents/upload", content);
        response.EnsureSuccessStatusCode();

        // Assert
        // The cache must have been invalidated (cleared) by the upload handler
        Program.DirFileCache.ContainsKey("dummy_directory").Should().BeFalse(
            "Upload handler should clear DirFileCache so new files are immediately visible.");
    }
}
