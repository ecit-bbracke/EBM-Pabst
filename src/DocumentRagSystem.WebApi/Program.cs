using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
var geminiLlmModel = builder.Configuration["Gemini:LlmModel"] ?? "gemini-3.6-flash";
var geminiFastLlmModel = builder.Configuration["Gemini:FastLlmModel"] ?? geminiLlmModel;

var qdrantConnString = builder.Configuration["Qdrant:ConnectionString"] ?? "http://localhost:6334";
var qdrantCollection = builder.Configuration["Qdrant:CollectionName"] ?? "document-chunks";
var uploadsDirectory = GetUploadsDirectory(builder.Configuration["Uploads:Directory"]);
var servedUploadDirectories = GetServedUploadDirectories(uploadsDirectory).ToList();

// Register pooled HTTP clients for external AI API calls
builder.Services.AddHttpClient("GeminiClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Add Singletons and Scoped Services
builder.Services.AddSingleton<IDocumentRepository, InMemoryDocumentRepository>();
builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<IChunkingService, ChunkingService>(sp => new ChunkingService(1000, 200));

// Set up Gemini embedding and LLM services
builder.Services.AddSingleton<IEmbeddingService, GeminiEmbeddingService>(sp => 
    new GeminiEmbeddingService(
        geminiApiKey, 
        geminiEmbeddingModel, 
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiClient"),
        sp.GetService<ILogger<GeminiEmbeddingService>>()));

builder.Services.AddSingleton<ILlmService, GeminiLlmService>(sp => 
    new GeminiLlmService(
        geminiApiKey, 
        geminiLlmModel, 
        geminiFastLlmModel, 
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiClient"),
        sp.GetService<ILogger<GeminiLlmService>>()));

// Set up Qdrant Vector Store
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>(sp => 
    new QdrantVectorStore(
        qdrantConnString, 
        qdrantCollection, 
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetService<ILogger<QdrantVectorStore>>()));

// Set up overall DocumentProcessor
builder.Services.AddSingleton<IDocumentProcessor, DocumentProcessor>(sp => 
    new DocumentProcessor(
        sp.GetRequiredService<ITextExtractor>(),
        sp.GetRequiredService<IChunkingService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<IVectorStore>(),
        sp.GetRequiredService<IDocumentRepository>()
    ));

// Set up RAG Orchestrator Layer & ebm-papst Domain Services
var conversationStorageDir = Path.Combine(AppContext.BaseDirectory, ".conversations");
builder.Services.AddSingleton<IConversationStateStore, FileConversationStateStore>(sp => 
    new FileConversationStateStore(conversationStorageDir));

builder.Services.AddSingleton<IEbmProductCodeParser, EbmProductCodeParser>();
builder.Services.AddSingleton<IConversationalQueryRefiner, ConversationalQueryRefiner>(sp =>
    new ConversationalQueryRefiner(
        sp.GetRequiredService<ILlmService>(),
        sp.GetRequiredService<IEbmProductCodeParser>(),
        sp.GetService<ILogger<ConversationalQueryRefiner>>()));

builder.Services.AddSingleton<IInputGovernor, InputGovernor>(sp =>
    new InputGovernor(
        sp.GetRequiredService<ILlmService>(),
        sp.GetRequiredService<IEbmProductCodeParser>(),
        sp.GetService<ILogger<InputGovernor>>()));

builder.Services.AddSingleton<IWorkflowRouter, WorkflowRouter>();

builder.Services.AddSingleton<IEvidenceEvaluator, EvidenceEvaluator>(sp =>
    new EvidenceEvaluator(
        sp.GetRequiredService<ILlmService>(),
        sp.GetService<ILogger<EvidenceEvaluator>>()));

builder.Services.AddSingleton<IAnswerComposer, AnswerComposer>(sp =>
    new AnswerComposer(
        sp.GetRequiredService<ILlmService>(),
        sp.GetService<ILogger<AnswerComposer>>()));

builder.Services.AddSingleton<IOutputGovernor, OutputGovernor>(sp =>
    new OutputGovernor(
        sp.GetRequiredService<ILlmService>(),
        sp.GetService<ILogger<OutputGovernor>>()));

builder.Services.AddSingleton<ITechnicalRagOrchestrator, TechnicalRagOrchestrator>(sp =>
    new TechnicalRagOrchestrator(
        sp.GetRequiredService<IInputGovernor>(),
        sp.GetRequiredService<IWorkflowRouter>(),
        sp.GetRequiredService<IEvidenceEvaluator>(),
        sp.GetRequiredService<IAnswerComposer>(),
        sp.GetRequiredService<IOutputGovernor>(),
        sp.GetRequiredService<ILlmService>(),
        sp.GetRequiredService<IEbmProductCodeParser>(),
        sp.GetRequiredService<IConversationalQueryRefiner>(),
        sp.GetRequiredService<IConversationStateStore>(),
        sp.GetService<IDocumentRepository>(),
        sp.GetService<ILogger<TechnicalRagOrchestrator>>()));

// Register Queue and Background Hosted Services
builder.Services.AddSingleton<IDocumentQueue, DocumentQueue>();
builder.Services.AddHostedService<QueuedHostedService>();
builder.Services.AddHostedService<ConversationCleanupHostedService>();

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
    ITechnicalRagOrchestrator orchestrator, 
    ILogger<Program> logger
    ) =>
{
    var requestStopwatch = System.Diagnostics.Stopwatch.StartNew();
    var validationResult = await validator.ValidateAsync(request);
    if (!validationResult.IsValid)
    {
        return Results.BadRequest(validationResult.Errors.Select(e => e.ErrorMessage));
    }

    logger.LogInformation("Query received: {Question} (ConversationId: {ConversationId})", request.Question, request.ConversationId);

    // Process through the Multi-Stage Technical RAG Orchestrator
    var (answer, chunks, trace) = await orchestrator.ProcessQueryAsync(request.Question, vectorStore, request.ConversationId);

    logger.LogInformation("Execution Trace: {TraceJson}", JsonSerializer.Serialize(trace));

    // Convert markdown answer to HTML with advanced extensions (for tables, bold, lists, etc.)
    var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    var htmlAnswer = Markdown.ToHtml(answer, pipeline);
    var citations = new List<CitationDto>();

    foreach (var item in chunks)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.DocumentId))
        {
            continue;
        }

        var fileName = FirstNonEmpty(item.FileName, item.DocumentId);
        var filePath = item.FilePath;

        citations.Add(new CitationDto(
            item.Id,
            item.DocumentId,
            string.Empty,
            item.Index,
            fileName,
            ToDocumentUrl(filePath, fileName, servedUploadDirectories)
        ));
    }

    var effectiveConversationId = trace.ConversationStateAfter?.ConversationId ?? request.ConversationId;
    requestStopwatch.Stop();
    logger.LogInformation(
        "HTTP POST /api/query completed in {RequestDurationMs}ms (ConversationId: {ConversationId}, Citations: {CitationCount}).",
        requestStopwatch.ElapsedMilliseconds, effectiveConversationId, citations.Count);

    return Results.Ok(new QueryResponse(htmlAnswer, citations, effectiveConversationId));
})
.WithName("QueryDocuments");

// STREAMING QUERY ENDPOINT (SSE)
app.MapPost("/api/query/stream", async (
    QueryRequest request,
    IValidator<QueryRequest> validator,
    IVectorStore vectorStore,
    ITechnicalRagOrchestrator orchestrator,
    HttpContext httpContext,
    ILogger<Program> logger,
    CancellationToken cancellationToken
    ) =>
{
    var requestStopwatch = System.Diagnostics.Stopwatch.StartNew();
    var validationResult = await validator.ValidateAsync(request, cancellationToken);
    if (!validationResult.IsValid)
    {
        return Results.BadRequest(validationResult.Errors.Select(e => e.ErrorMessage));
    }

    logger.LogInformation("Streaming query received: {Question} (ConversationId: {ConversationId})", request.Question, request.ConversationId);

    httpContext.Response.Headers.Append("Content-Type", "text/event-stream");
    httpContext.Response.Headers.Append("Cache-Control", "no-cache");
    httpContext.Response.Headers.Append("Connection", "keep-alive");

    var stream = orchestrator.ProcessQueryStreamAsync(
        request.Question, 
        vectorStore, 
        request.ConversationId, 
        null, 
        cancellationToken);

    int chunkIndex = 0;
    await foreach (var evt in stream.WithCancellation(cancellationToken))
    {
        if (evt.EventType == "citations")
        {
            var citations = new List<CitationDto>();
            if (evt.Chunks != null)
            {
                foreach (var item in evt.Chunks)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.DocumentId))
                        continue;

                    var fileName = FirstNonEmpty(item.FileName, item.DocumentId);
                    var filePath = item.FilePath;

                    citations.Add(new CitationDto(
                        item.Id,
                        item.DocumentId,
                        string.Empty,
                        item.Index,
                        fileName,
                        ToDocumentUrl(filePath, fileName, servedUploadDirectories)
                    ));
                }
            }

            var payload = JsonSerializer.Serialize(new { citations, conversationId = evt.ConversationId });
            await httpContext.Response.WriteAsync($"event: citations\ndata: {payload}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
        else if (evt.EventType == "chunk")
        {
            chunkIndex++;
            var payload = JsonSerializer.Serialize(new { text = evt.Text });
            await httpContext.Response.WriteAsync($"event: chunk\ndata: {payload}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
        else if (evt.EventType == "done")
        {
            requestStopwatch.Stop();
            logger.LogInformation(
                "HTTP POST /api/query/stream completed in {RequestDurationMs}ms (ConversationId: {ConversationId}, TotalChunksStreamed: {ChunkCount}).",
                requestStopwatch.ElapsedMilliseconds, evt.ConversationId, chunkIndex);

            var payload = JsonSerializer.Serialize(new { conversationId = evt.ConversationId, trace = evt.Trace });
            await httpContext.Response.WriteAsync($"event: done\ndata: {payload}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
    }

    return Results.Empty;
})
.WithName("QueryDocumentsStream");

// GET CONVERSATION STATE
app.MapGet("/api/conversation/{id}", async (string id, IConversationStateStore stateStore) =>
{
    var state = await stateStore.GetStateAsync(id);
    return state != null ? Results.Ok(state) : Results.NotFound();
})
.WithName("GetConversationState");

// CLEAR CONVERSATION STATE
app.MapDelete("/api/conversation/{id}", async (string id, IConversationStateStore stateStore) =>
{
    await stateStore.ClearStateAsync(id);
    return Results.NoContent();
})
.WithName("ClearConversationState");

// REMOVE SPECIFIC CONSTRAINT FROM CONVERSATION
app.MapDelete("/api/conversation/{id}/constraints/{attribute}", async (string id, string attribute, IConversationStateStore stateStore) =>
{
    await stateStore.RemoveConstraintAsync(id, attribute);
    var updated = await stateStore.GetStateAsync(id);
    return updated != null ? Results.Ok(updated) : Results.NotFound();
})
.WithName("RemoveConversationConstraint");

// RESET CONVERSATION CONSTRAINTS
app.MapPost("/api/conversation/{id}/reset", async (string id, IConversationStateStore stateStore) =>
{
    var state = await stateStore.GetStateAsync(id);
    if (state != null)
    {
        var resetState = state with 
        { 
            ActiveConstraints = new List<ConversationConstraint>(),
            CandidateSet = null,
            TurnCount = state.TurnCount + 1,
            TopicVersion = state.TopicVersion + 1
        };
        await stateStore.SaveStateAsync(id, resetState);
        return Results.Ok(resetState);
    }
    return Results.NotFound();
})
.WithName("ResetConversationConstraints");

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

    // Try finding the existing file on disk. If the stored filePath (with a previous Guid prefix) doesn't exist anymore,
    // fallback to searching for any file with the same actual raw filename on disk.
    var currentFileName = Path.GetFileName(Uri.UnescapeDataString(filePath));
    var resolvedUrl = ToExistingUploadUrl(currentFileName, uploadDirectories);
    if (!string.IsNullOrWhiteSpace(resolvedUrl))
    {
        return resolvedUrl;
    }

    // If that fails, strip any Guid-like prefix (36 chars followed by '_') from the filename and try searching again
    if (currentFileName.Length > 37 && currentFileName[36] == '_')
    {
        var rawName = currentFileName.Substring(37);
        var fallbackUrlFromRaw = ToExistingUploadUrl(rawName, uploadDirectories);
        if (!string.IsNullOrWhiteSpace(fallbackUrlFromRaw))
        {
            return fallbackUrlFromRaw;
        }
    }

    var pathFileName = Path.GetFileName(filePath);
    return ToExistingUploadUrl(pathFileName, uploadDirectories, filePath);
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
