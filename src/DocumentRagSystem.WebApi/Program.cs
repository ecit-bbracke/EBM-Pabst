using System;
using System.IO;
using System.Linq;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

// HEALTH ENDPOINT
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }))
   .WithName("HealthCheck");

// GET ALL DOCUMENTS ENDPOINT
app.MapGet("/api/documents", async (IDocumentRepository repository) =>
{
    var docs = await repository.GetAllDocumentsAsync();
    return Results.Ok(docs);
})
.WithName("GetAllDocuments");

// QUERY ENDPOINT WITH CITATIONS
app.MapPost("/api/query", async (
    QueryRequest request, 
    IValidator<QueryRequest> validator,
    IVectorStore vectorStore, 
    ILlmService llmService, 
    ILogger<Program> logger) =>
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

    // Construct citations
    var citations = chunks.Select(c => new CitationDto(c.Id, c.DocumentId, c.Text, c.Index)).ToList();

    return Results.Ok(new QueryResponse(answer, citations));
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

// Required to make Program class visible to integration/E2E test project
public partial class Program { }
