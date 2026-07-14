using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Markdig;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.TextExtractors;
using DocumentRagSystem.Infrastructure.Embeddings;
using DocumentRagSystem.Infrastructure.Llm;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.Infrastructure.VectorStores;
using DocumentRagSystem.WebApi.DTOs;
using DocumentRagSystem.WebApi.HostedServices;
using DocumentRagSystem.WebApi.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Register Core & Infrastructure Services
var geminiApiKey = builder.Configuration["Gemini:ApiKey"];
var geminiEmbeddingModel = builder.Configuration["Gemini:EmbeddingModel"] ?? "text-embedding-004";
var geminiLlmModel = builder.Configuration["Gemini:LlmModel"] ?? "gemini-1.5-flash";

var qdrantConnString = builder.Configuration["Qdrant:ConnectionString"] ?? "http://localhost:6334";
var qdrantCollection = builder.Configuration["Qdrant:CollectionName"] ?? "document-chunks";
var uploadsDirectory = GetUploadsDirectory(builder.Configuration["Uploads:Directory"]);
var servedUploadDirectories = GetServedUploadDirectories(uploadsDirectory).ToList();

// Add Singletons and Scoped Services
builder.Services.AddSingleton<IDocumentRepository, InMemoryDocumentRepository>();
builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<IChunkingService, ChunkingService>(sp => new ChunkingService(1000, 200));

// Set up Gemini embedding and LLM services
builder.Services.AddSingleton<IEmbeddingService, GeminiEmbeddingService>(sp => 
    new GeminiEmbeddingService(geminiApiKey, geminiEmbeddingModel));

builder.Services.AddSingleton<ILlmService, GeminiLlmService>(sp => 
    new GeminiLlmService(geminiApiKey, geminiLlmModel));

// Set up Qdrant Vector Store
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>(sp => 
    new QdrantVectorStore(qdrantConnString, qdrantCollection, sp.GetRequiredService<IEmbeddingService>()));

// Set up overall DocumentProcessor
builder.Services.AddSingleton<IDocumentProcessor, DocumentProcessor>(sp => 
    new DocumentProcessor(
        sp.GetRequiredService<ITextExtractor>(),
        sp.GetRequiredService<IChunkingService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<IVectorStore>(),
        sp.GetRequiredService<IDocumentRepository>()
    ));

// Register Queue and Background Hosted Service
builder.Services.AddSingleton<IDocumentQueue, DocumentQueue>();
builder.Services.AddHostedService<QueuedHostedService>();

// Register Validators
builder.Services.AddValidatorsFromAssemblyContaining<QueryRequestValidator>();

var app = builder.Build();

// Enable Global Exception Handling
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseDefaultFiles();
app.UseStaticFiles();
Directory.CreateDirectory(uploadsDirectory);
foreach (var uploadDirectory in servedUploadDirectories)
{
    Directory.CreateDirectory(uploadDirectory);
}

var uploadFileProviders = servedUploadDirectories
    .Select(directory => new PhysicalFileProvider(directory))
    .Cast<IFileProvider>()
    .ToList();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = uploadFileProviders.Count == 1
        ? uploadFileProviders[0]
        : new CompositeFileProvider(uploadFileProviders),
    RequestPath = "/uploads"
});

// HEALTH ENDPOINT
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }))
   .WithName("HealthCheck");

// GET ALL DOCUMENTS ENDPOINT
app.MapGet("/api/documents", async (IDocumentRepository repository, IVectorStore vectorStore) =>
{
    var repositoryDocs = await repository.GetAllDocumentsAsync();
    var vectorDocs = await vectorStore.GetDocumentsAsync();
    var docs = repositoryDocs
        .Concat(vectorDocs)
        .GroupBy(doc => doc.Id)
        .Select(group => group
            .OrderByDescending(doc => doc.UploadedAt)
            .ThenByDescending(doc => !string.IsNullOrWhiteSpace(doc.FileName))
            .First())
        .Select(doc => doc with { FilePath = ToDocumentUrl(doc.FilePath, doc.FileName, servedUploadDirectories) })
        .OrderByDescending(doc => doc.UploadedAt)
        .ToList();

    return Results.Ok(docs);
})
.WithName("GetAllDocuments");

// QUERY ENDPOINT WITH CITATIONS
app.MapPost("/api/query", async (
    QueryRequest request, 
    IValidator<QueryRequest> validator,
    IVectorStore vectorStore, 
    ILlmService llmService, 
    ILogger<Program> logger
    ) =>
{
    var validationResult = await validator.ValidateAsync(request);
    if (!validationResult.IsValid)
    {
        return Results.BadRequest(validationResult.Errors.Select(e => e.ErrorMessage));
    }

    logger.LogInformation("Query received: {Question}", request.Question);

    // Perform Semantic Hybrid Search
    var chunks = await vectorStore.SearchAsync(request.Question);

    // Generate Response using LLM with context chunks
    var answer = await llmService.GenerateResponseAsync(request.Question, chunks);

    // Convert markdown answer to HTML with advanced extensions (for tables, bold, lists, etc.)
    var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    var htmlAnswer = Markdown.ToHtml(answer, pipeline);
    var citations = new List<CitationDto>();
    var documentsById = (await vectorStore.GetDocumentsAsync())
        .ToDictionary(document => document.Id, StringComparer.OrdinalIgnoreCase);

    foreach (var item in chunks)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.DocumentId))
        {
            continue;
        }

        documentsById.TryGetValue(item.DocumentId, out var document);
        var fileName = FirstNonEmpty(item.FileName, document?.FileName, item.DocumentId);
        var filePath = FirstNonEmpty(item.FilePath, document?.FilePath);

        citations.Add(new CitationDto(
            item.Id,
            item.DocumentId,
            string.Empty,
            item.Index,
            fileName,
            ToDocumentUrl(filePath, fileName, servedUploadDirectories)
        ));
    }

    return Results.Ok(new QueryResponse(htmlAnswer, citations));
})
.WithName("QueryDocuments");

// DOCUMENT UPLOAD ENDPOINT
app.MapPost("/api/documents/upload", async (
    HttpRequest request, 
    IDocumentProcessor processor, 
    IDocumentQueue queue,
    IDocumentRepository repository,
    ILogger<Program> logger) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Invalid content type. Multipart form-data expected.");
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest("No file was uploaded under the field name 'file'.");
    }

    if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Only PDF documents are supported.");
    }

    bool background = request.Query.TryGetValue("background", out var bgStr) && bool.TryParse(bgStr, out var bg) && bg;

    if (background)
    {
        logger.LogInformation("Uploading and queuing file for background processing: {FileName}", file.FileName);
        
        var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        var documentId = Guid.NewGuid().ToString();
        var tempDocument = new Document(
            Id: documentId,
            FileName: file.FileName,
            FilePath: "Queued/Background",
            UploadedAt: DateTime.UtcNow,
            Status: DocumentStatus.Pending
        );
        await repository.AddDocumentAsync(tempDocument);

        queue.QueueBackgroundWorkItem(async token =>
        {
            try
            {
                await processor.ProcessPdfAsync(memoryStream, file.FileName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background processing failed for file: {FileName}", file.FileName);
            }
            finally
            {
                await memoryStream.DisposeAsync();
            }
        });

        return Results.Accepted($"/api/documents", tempDocument);
    }
    else
    {
        logger.LogInformation("Uploading and processing file synchronously: {FileName}", file.FileName);

        using var stream = file.OpenReadStream();
        var document = await processor.ProcessPdfAsync(stream, file.FileName);

        return Results.Ok(document);
    }
})
.WithName("UploadDocument");

app.Run();

static string FirstNonEmpty(params string?[] values)
{
    foreach (var value in values)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return string.Empty;
}

static string GetUploadsDirectory(string? configuredUploadsDirectory)
{
    return string.IsNullOrWhiteSpace(configuredUploadsDirectory)
        ? Path.Combine(AppContext.BaseDirectory, "uploads")
        : Path.GetFullPath(configuredUploadsDirectory);
}

static IEnumerable<string> GetServedUploadDirectories(string uploadsDirectory)
{
    yield return uploadsDirectory;

    var workerUploadsDirectory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "DocumentRagSystem.Worker",
        "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0",
        "uploads"));

    if (!string.Equals(workerUploadsDirectory, uploadsDirectory, StringComparison.OrdinalIgnoreCase))
    {
        yield return workerUploadsDirectory;
    }
}

static string ToDocumentUrl(string? filePath, string? fileName, IEnumerable<string> uploadDirectories)
{
    if (string.IsNullOrWhiteSpace(filePath))
    {
        return ToExistingUploadUrl(fileName, uploadDirectories);
    }

    if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
    {
        return filePath;
    }

    if (filePath.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
    {
        var uploadFileName = Path.GetFileName(Uri.UnescapeDataString(filePath));
        return ToExistingUploadUrl(uploadFileName, uploadDirectories, filePath);
    }

    var pathFileName = Path.GetFileName(filePath);
    return ToExistingUploadUrl(pathFileName, uploadDirectories);
}

static string ToExistingUploadUrl(string? fileName, IEnumerable<string> uploadDirectories, string? fallbackUrl = null)
{
    if (string.IsNullOrWhiteSpace(fileName))
    {
        return string.Empty;
    }

    var normalizedFileName = Path.GetFileName(fileName);
    foreach (var uploadDirectory in uploadDirectories)
    {
        var directPath = Path.Combine(uploadDirectory, normalizedFileName);
        if (File.Exists(directPath))
        {
            return $"/uploads/{Uri.EscapeDataString(normalizedFileName)}";
        }

        if (!Directory.Exists(uploadDirectory))
        {
            continue;
        }

        var suffixMatches = Directory
            .EnumerateFiles(uploadDirectory, $"*_{normalizedFileName}", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (suffixMatches.Count > 0)
        {
            return $"/uploads/{Uri.EscapeDataString(Path.GetFileName(suffixMatches[0]))}";
        }
    }

    return fallbackUrl ?? string.Empty;
}

// Required to make Program class visible to integration/E2E test project
public partial class Program { }
