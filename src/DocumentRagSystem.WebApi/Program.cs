using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Markdig;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.TextExtractors;
using DocumentRagSystem.Infrastructure.Embeddings;
using DocumentRagSystem.Infrastructure.HealthChecks;
using DocumentRagSystem.Infrastructure.Llm;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.Infrastructure.VectorStores;
using DocumentRagSystem.WebApi.DTOs;
using DocumentRagSystem.WebApi.HostedServices;
using DocumentRagSystem.WebApi.Middleware;
using DocumentRagSystem.Infrastructure.Data;
using DocumentRagSystem.WebApi.Data;
using DocumentRagSystem.WebApi.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Database & Microsoft Identity Configuration
var connectionString = builder.Configuration.GetConnectionString("IdentityDb") ?? "Data Source=identity.db";
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDbContextFactory<DocumentDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequiredLength = 6;
    options.SignIn.RequireConfirmedAccount = false;

    // Account Lockout Protection
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login.html";
    options.LogoutPath = "/api/auth/logout";
    options.AccessDeniedPath = "/index.html";
    options.Cookie.Name = "EbmRagAuth";
    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/uploads"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        context.Response.Redirect("/index.html");
        return Task.CompletedTask;
    };
});

// Configure Hybrid Authentication (Cookie for web portal + API Key for API clients)
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "SmartScheme";
    options.DefaultAuthenticateScheme = "SmartScheme";
    options.DefaultChallengeScheme = "SmartScheme";
})
.AddPolicyScheme("SmartScheme", "Cookie or ApiKey", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (context.Request.Headers.ContainsKey(ApiKeyAuthenticationOptions.HeaderName) ||
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            authHeader.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
        {
            return ApiKeyAuthenticationOptions.DefaultScheme;
        }
        return IdentityConstants.ApplicationScheme;
    };
})
.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationOptions.DefaultScheme, options =>
    {
        options.ApiKey = builder.Configuration["Authentication:ApiKey"] 
                         ?? Environment.GetEnvironmentVariable("API_KEY") 
                         ?? string.Empty;
    });

builder.Services.AddOptions<ApiKeyAuthenticationOptions>(ApiKeyAuthenticationOptions.DefaultScheme)
    .Configure<IConfiguration>((options, config) =>
    {
        var key = config["Authentication:ApiKey"] ?? Environment.GetEnvironmentVariable("API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            options.ApiKey = key;
        }
    });

builder.Services.AddAuthorization();

// Register Core & Infrastructure Services
var geminiApiKey = builder.Configuration["Gemini:ApiKey"];
var geminiEmbeddingModel = builder.Configuration["Gemini:EmbeddingModel"] ?? "gemini-embedding-001";
var geminiLlmModel = builder.Configuration["Gemini:LlmModel"] ?? "gemini-3.6-flash";
var geminiFastLlmModel = builder.Configuration["Gemini:FastLlmModel"] ?? geminiLlmModel;

var qdrantConnString = builder.Configuration["Qdrant:ConnectionString"] ?? "http://localhost:6334";
var qdrantCollection = builder.Configuration["Qdrant:CollectionName"] ?? "document-chunks";
var uploadsDirectory = GetUploadsDirectory(builder.Configuration["Uploads:Directory"]);
var servedUploadDirectories = GetServedUploadDirectories(uploadsDirectory).ToList();

// Configure LLM Options
builder.Services.Configure<LlmOptions>(options =>
{
    builder.Configuration.GetSection(LlmOptions.SectionName).Bind(options);
    if (string.IsNullOrWhiteSpace(options.Gemini.ApiKey) && !string.IsNullOrWhiteSpace(geminiApiKey))
    {
        options.Gemini.ApiKey = geminiApiKey;
    }
    if (string.IsNullOrWhiteSpace(options.Gemini.LlmModel) && !string.IsNullOrWhiteSpace(geminiLlmModel))
    {
        options.Gemini.LlmModel = geminiLlmModel;
    }
    if (string.IsNullOrWhiteSpace(options.Gemini.FastLlmModel) && !string.IsNullOrWhiteSpace(geminiFastLlmModel))
    {
        options.Gemini.FastLlmModel = geminiFastLlmModel;
    }
});

// Register pooled HTTP clients for external AI API calls
builder.Services.AddHttpClient("GeminiClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient("LocalLlmClient", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Local;
    if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
    {
        var rawUrl = opts.BaseUrl.Trim();
        if (rawUrl.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            rawUrl = rawUrl.Substring(0, rawUrl.Length - "/v1/chat/completions".Length);
        }
        client.BaseAddress = new Uri(rawUrl.TrimEnd('/') + "/");
    }
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 60);
});

builder.Services.AddHttpClient("LocalLlmHealthClient", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Local;
    if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
    {
        var rawUrl = opts.BaseUrl.Trim();
        if (rawUrl.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            rawUrl = rawUrl.Substring(0, rawUrl.Length - "/v1/chat/completions".Length);
        }
        client.BaseAddress = new Uri(rawUrl.TrimEnd('/') + "/");
    }
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Add Singletons and Scoped Services
builder.Services.AddSingleton<IDocumentRepository, SqliteDocumentRepository>();
builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<IChunkingService, ChunkingService>(sp => new ChunkingService(1000, 200));

// Set up Gemini embedding service
builder.Services.AddSingleton<IEmbeddingService, GeminiEmbeddingService>(sp => 
    new GeminiEmbeddingService(
        geminiApiKey, 
        geminiEmbeddingModel, 
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiClient"),
        sp.GetService<ILogger<GeminiEmbeddingService>>()));

// Register LLM Providers and Resolver
builder.Services.AddSingleton<GeminiLlmClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiClient");
    var logger = sp.GetService<ILogger<GeminiLlmClient>>();
    return new GeminiLlmClient(opts.Gemini.ApiKey ?? geminiApiKey, opts.Gemini.LlmModel, opts.Gemini.FastLlmModel ?? geminiFastLlmModel, httpClient, logger);
});

builder.Services.AddSingleton<LocalLlmClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Local;
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("LocalLlmClient");
    var logger = sp.GetService<ILogger<LocalLlmClient>>();
    return new LocalLlmClient(opts, httpClient, logger);
});

builder.Services.AddSingleton<ILlmClientResolver, LlmClientResolver>(sp =>
    new LlmClientResolver(
        sp.GetRequiredService<IOptions<LlmOptions>>(),
        sp.GetRequiredService<GeminiLlmClient>(),
        sp.GetRequiredService<LocalLlmClient>(),
        sp.GetService<ILoggerFactory>(),
        sp.GetService<ILogger<LlmClientResolver>>()));

builder.Services.AddSingleton<ILlmClient>(sp =>
{
    var resolver = sp.GetRequiredService<ILlmClientResolver>();
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    return resolver.Resolve(opts.DefaultProvider);
});

builder.Services.AddSingleton<ILlmService, GeminiLlmService>(sp => 
    new GeminiLlmService(
        sp.GetRequiredService<ILlmClient>(),
        sp.GetService<ILogger<GeminiLlmService>>()));

// Set up Qdrant Vector Store
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>(sp => 
    new QdrantVectorStore(
        qdrantConnString, 
        qdrantCollection, 
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetService<ILogger<QdrantVectorStore>>()));

// Set up Language Detector & overall DocumentProcessor
builder.Services.AddSingleton<ILanguageDetector, DefaultLanguageDetector>(sp =>
    new DefaultLanguageDetector(
        sp.GetService<ILlmClientResolver>(),
        sp.GetService<ILogger<DefaultLanguageDetector>>()));

builder.Services.AddSingleton<IDocumentProcessor, DocumentProcessor>(sp => 
    new DocumentProcessor(
        sp.GetRequiredService<ITextExtractor>(),
        sp.GetRequiredService<IChunkingService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<IVectorStore>(),
        sp.GetRequiredService<IDocumentRepository>(),
        sp.GetRequiredService<ILanguageDetector>(),
        sp.GetService<ILogger<DocumentProcessor>>()
    ));

// Set up RAG Orchestrator Layer & ebm-papst Domain Services
var conversationStorageDir = Path.Combine(AppContext.BaseDirectory, ".conversations");
builder.Services.AddSingleton<IConversationStateStore, FileConversationStateStore>(sp => 
    new FileConversationStateStore(conversationStorageDir));

builder.Services.AddSingleton<IEbmProductCodeParser, EbmProductCodeParser>();
builder.Services.AddSingleton<IConversationalQueryRefiner, ConversationalQueryRefiner>(sp =>
    new ConversationalQueryRefiner(
        sp.GetRequiredService<ILlmClientResolver>(),
        sp.GetRequiredService<IEbmProductCodeParser>(),
        sp.GetService<ILogger<ConversationalQueryRefiner>>()));

builder.Services.AddSingleton<IInputGovernor, InputGovernor>(sp =>
    new InputGovernor(
        sp.GetRequiredService<ILlmClientResolver>(),
        sp.GetRequiredService<IEbmProductCodeParser>(),
        sp.GetService<ILogger<InputGovernor>>()));

builder.Services.AddSingleton<IWorkflowRouter, WorkflowRouter>();

builder.Services.AddSingleton<IEvidenceEvaluator, EvidenceEvaluator>(sp =>
    new EvidenceEvaluator(
        sp.GetRequiredService<ILlmClientResolver>(),
        sp.GetService<ILogger<EvidenceEvaluator>>()));

builder.Services.AddSingleton<IAnswerComposer, AnswerComposer>(sp =>
    new AnswerComposer(
        sp.GetRequiredService<ILlmClientResolver>(),
        sp.GetService<ILogger<AnswerComposer>>()));

builder.Services.AddSingleton<IOutputGovernor, OutputGovernor>(sp =>
    new OutputGovernor(
        sp.GetRequiredService<ILlmClientResolver>(),
        sp.GetService<ILogger<OutputGovernor>>()));

builder.Services.AddSingleton<IContextExpander, ContextExpander>();

builder.Services.AddSingleton<IDanishDocumentResolver, DanishDocumentResolver>(sp =>
    new DanishDocumentResolver(
        sp.GetService<IDocumentRepository>(),
        sp.GetService<ILanguageDetector>(),
        sp.GetService<ITextExtractor>(),
        sp.GetService<IEbmProductCodeParser>(),
        servedUploadDirectories,
        sp.GetService<ILogger<DanishDocumentResolver>>()));

builder.Services.AddSingleton<ITechnicalRagOrchestrator, TechnicalRagOrchestrator>(sp =>
    new TechnicalRagOrchestrator(
        sp.GetRequiredService<IInputGovernor>(),
        sp.GetRequiredService<IWorkflowRouter>(),
        sp.GetRequiredService<IEvidenceEvaluator>(),
        sp.GetRequiredService<IAnswerComposer>(),
        sp.GetRequiredService<IOutputGovernor>(),
        sp.GetRequiredService<ILlmClientResolver>(),
        sp.GetRequiredService<IEbmProductCodeParser>(),
        sp.GetRequiredService<IConversationalQueryRefiner>(),
        sp.GetRequiredService<IConversationStateStore>(),
        sp.GetService<IDocumentRepository>(),
        sp.GetRequiredService<IContextExpander>(),
        sp.GetService<ILogger<TechnicalRagOrchestrator>>()));

// Add Health Checks
builder.Services.AddHealthChecks()
    .AddCheck<LocalLlmHealthCheck>("local-llm");

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
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

// Secure HTML pages and API routes middleware
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;

    // Allow login page, auth API endpoints, static assets, and health checks
    if (path.Equals("/login.html", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    // Require authentication
    if (context.User.Identity?.IsAuthenticated != true)
    {
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var returnUrl = Uri.EscapeDataString(path + context.Request.QueryString);
        context.Response.Redirect($"/login.html?returnUrl={returnUrl}");
        return;
    }

    // Restrict /users.html strictly to Admins
    if (path.Equals("/users.html", StringComparison.OrdinalIgnoreCase))
    {
        if (!IsUserAdmin(context.User))
        {
            context.Response.Redirect("/index.html");
            return;
        }
    }

    await next();
});

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
.RequireAuthorization()
.WithName("GetAllDocuments");

// DOCUMENT STATUS ENDPOINTS
app.MapGet("/api/documents/status", async (
    [FromQuery] string? fileName,
    [FromQuery] string? name,
    [FromQuery] string? file,
    IDocumentRepository repository,
    IVectorStore vectorStore) =>
{
    var resolvedFileName = FirstNonEmpty(fileName, name, file);
    if (string.IsNullOrWhiteSpace(resolvedFileName))
    {
        return Results.BadRequest(new { error = "The 'fileName' parameter is required." });
    }

    return await GetDocumentStatusResultAsync(resolvedFileName, repository, vectorStore, servedUploadDirectories);
})
.RequireAuthorization()
.WithName("GetDocumentStatusByQuery")
.WithSummary("Get document upload status by filename (query parameter)")
.WithDescription("Checks whether a document has already been uploaded by filename and returns its current status.")
.Produces<DocumentStatusResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

app.MapGet("/api/documents/status/{*fileName}", async (
    string fileName,
    IDocumentRepository repository,
    IVectorStore vectorStore) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
    {
        return Results.BadRequest(new { error = "The 'fileName' parameter is required." });
    }

    return await GetDocumentStatusResultAsync(fileName, repository, vectorStore, servedUploadDirectories);
})
.RequireAuthorization()
.WithName("GetDocumentStatusByPath")
.WithSummary("Get document upload status by filename (route parameter)")
.WithDescription("Checks whether a document has already been uploaded by filename and returns its current status.")
.Produces<DocumentStatusResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

app.MapGet("/api/documents/{fileName}/status", async (
    string fileName,
    IDocumentRepository repository,
    IVectorStore vectorStore) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
    {
        return Results.BadRequest(new { error = "The 'fileName' parameter is required." });
    }

    return await GetDocumentStatusResultAsync(fileName, repository, vectorStore, servedUploadDirectories);
})
.RequireAuthorization()
.WithName("GetDocumentStatusByResourcePath")
.WithSummary("Get document upload status by filename (resource route parameter)")
.WithDescription("Checks whether a document has already been uploaded by filename and returns its current status.")
.Produces<DocumentStatusResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

// QUERY ENDPOINT WITH CITATIONS
app.MapPost("/api/query", async (
    QueryRequest request, 
    IValidator<QueryRequest> validator,
    IVectorStore vectorStore, 
    ITechnicalRagOrchestrator orchestrator, 
    IDanishDocumentResolver danishResolver,
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

    // Append Danish companion documents as file references if available
    var danishCompanions = await danishResolver.FindDanishCompanionDocumentsAsync(chunks);
    foreach (var danishDoc in danishCompanions)
    {
        var daFileName = danishDoc.FileName;
        var daUrl = ToDocumentUrl(danishDoc.FilePath, daFileName, servedUploadDirectories);

        if (!citations.Any(c => string.Equals(c.filename, daFileName, StringComparison.OrdinalIgnoreCase)))
        {
            citations.Add(new CitationDto(
                ChunkId: $"{danishDoc.Id}_danish_companion",
                DocumentId: danishDoc.Id,
                Text: "Dansk version (reference)",
                Index: 0,
                filename: daFileName,
                filepath: daUrl,
                language: "da",
                isCompanion: true
            ));
        }
    }

    var effectiveConversationId = trace.ConversationStateAfter?.ConversationId ?? request.ConversationId;
    requestStopwatch.Stop();
    logger.LogInformation(
        "HTTP POST /api/query completed in {RequestDurationMs}ms (ConversationId: {ConversationId}, Citations: {CitationCount}).",
        requestStopwatch.ElapsedMilliseconds, effectiveConversationId, citations.Count);

    return Results.Ok(new QueryResponse(htmlAnswer, citations, effectiveConversationId));
})
.RequireAuthorization()
.WithName("QueryDocuments");

// STREAMING QUERY ENDPOINT (SSE)
app.MapPost("/api/query/stream", async (
    QueryRequest request,
    IValidator<QueryRequest> validator,
    IVectorStore vectorStore,
    ITechnicalRagOrchestrator orchestrator,
    IDanishDocumentResolver danishResolver,
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

                var danishCompanions = await danishResolver.FindDanishCompanionDocumentsAsync(evt.Chunks, cancellationToken);
                foreach (var danishDoc in danishCompanions)
                {
                    var daFileName = danishDoc.FileName;
                    var daUrl = ToDocumentUrl(danishDoc.FilePath, daFileName, servedUploadDirectories);

                    if (!citations.Any(c => string.Equals(c.filename, daFileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        citations.Add(new CitationDto(
                            ChunkId: $"{danishDoc.Id}_danish_companion",
                            DocumentId: danishDoc.Id,
                            Text: "Dansk version (reference)",
                            Index: 0,
                            filename: daFileName,
                            filepath: daUrl,
                            language: "da",
                            isCompanion: true
                        ));
                    }
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
.RequireAuthorization()
.WithName("QueryDocumentsStream");

// GET CONVERSATION STATE
app.MapGet("/api/conversation/{id}", async (string id, IConversationStateStore stateStore) =>
{
    var state = await stateStore.GetStateAsync(id);
    return state != null ? Results.Ok(state) : Results.NotFound();
})
.RequireAuthorization()
.WithName("GetConversationState");

// CLEAR CONVERSATION STATE
app.MapDelete("/api/conversation/{id}", async (string id, IConversationStateStore stateStore) =>
{
    await stateStore.ClearStateAsync(id);
    return Results.NoContent();
})
.RequireAuthorization()
.WithName("ClearConversationState");

// REMOVE SPECIFIC CONSTRAINT FROM CONVERSATION
app.MapDelete("/api/conversation/{id}/constraints/{attribute}", async (string id, string attribute, IConversationStateStore stateStore) =>
{
    await stateStore.RemoveConstraintAsync(id, attribute);
    var updated = await stateStore.GetStateAsync(id);
    return updated != null ? Results.Ok(updated) : Results.NotFound();
})
.RequireAuthorization()
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
.RequireAuthorization()
.WithName("ResetConversationConstraints");

// AUTH ENDPOINTS
app.MapPost("/api/auth/login", async (
    LoginRequest model, 
    SignInManager<IdentityUser> signInManager, 
    UserManager<IdentityUser> userManager) =>
{
    if (string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.Password))
    {
        return Results.BadRequest(new { message = "Email og adgangskode skal udfyldes." });
    }

    var user = await userManager.FindByEmailAsync(model.Email) ?? await userManager.FindByNameAsync(model.Email);
    if (user == null)
    {
        return Results.Unauthorized();
    }

    var result = await signInManager.PasswordSignInAsync(user, model.Password, isPersistent: model.RememberMe, lockoutOnFailure: true);
    if (result.Succeeded)
    {
        return Results.Ok(new { message = "Logget ind", email = user.Email });
    }

    if (result.IsLockedOut)
    {
        return Results.BadRequest(new { message = "Kontoen er midlertidigt låst pga. for mange mislykkede loginforsøg. Prøv igen om 15 minutter." });
    }

    return Results.Unauthorized();
})
.AllowAnonymous()
.WithName("Login");

app.MapPost("/api/auth/logout", async (SignInManager<IdentityUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Ok(new { message = "Logget ud" });
})
.RequireAuthorization()
.WithName("Logout");

app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
{
    if (user.Identity?.IsAuthenticated == true)
    {
        return Results.Ok(new { isAuthenticated = true, email = user.Identity.Name, isAdmin = IsUserAdmin(user) });
    }
    return Results.Unauthorized();
})
.RequireAuthorization()
.WithName("GetCurrentUser");

// USER MANAGEMENT ENDPOINTS
app.MapGet("/api/admin/users", async (
    ClaimsPrincipal currentUser,
    UserManager<IdentityUser> userManager) =>
{
    if (!IsUserAdmin(currentUser))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var users = await userManager.Users.ToListAsync();
    var dtos = users.Select(u => new UserSummaryDto(
        Id: u.Id,
        Email: u.Email ?? u.UserName ?? string.Empty,
        UserName: u.UserName,
        IsLockedOut: u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow
    )).ToList();

    return Results.Ok(dtos);
})
.RequireAuthorization()
.WithName("GetAdminUsers");

app.MapPost("/api/admin/users", async (
    CreateUserRequest model,
    ClaimsPrincipal currentUser,
    UserManager<IdentityUser> userManager) =>
{
    if (!IsUserAdmin(currentUser))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.Password))
    {
        return Results.BadRequest(new { message = "Email og adgangskode skal udfyldes." });
    }

    var existing = await userManager.FindByEmailAsync(model.Email);
    if (existing != null)
    {
        return Results.BadRequest(new { message = "En bruger med denne email findes allerede." });
    }

    var newUser = new IdentityUser
    {
        UserName = model.Email.Trim(),
        Email = model.Email.Trim(),
        EmailConfirmed = true
    };

    var result = await userManager.CreateAsync(newUser, model.Password);
    if (result.Succeeded)
    {
        return Results.Ok(new UserSummaryDto(
            Id: newUser.Id,
            Email: newUser.Email,
            UserName: newUser.UserName,
            IsLockedOut: false
        ));
    }

    return Results.BadRequest(new { message = string.Join(", ", result.Errors.Select(e => e.Description)) });
})
.RequireAuthorization()
.WithName("CreateAdminUser");

app.MapDelete("/api/admin/users/{id}", async (
    string id,
    ClaimsPrincipal currentUser,
    UserManager<IdentityUser> userManager) =>
{
    if (!IsUserAdmin(currentUser))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var user = await userManager.FindByIdAsync(id);
    if (user == null)
    {
        return Results.NotFound(new { message = "Bruger ikke fundet." });
    }

    if (string.Equals(user.Email, "admin@ebmpabst.dk", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Standard administratoren kan ikke slettes." });
    }

    if (string.Equals(user.Email, currentUser.Identity?.Name, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Du kan ikke slette din egen bruger." });
    }

    var result = await userManager.DeleteAsync(user);
    if (result.Succeeded)
    {
        return Results.Ok(new { message = "Bruger slettet." });
    }

    return Results.BadRequest(new { message = string.Join(", ", result.Errors.Select(e => e.Description)) });
})
.RequireAuthorization()
.WithName("DeleteAdminUser");

app.MapPost("/api/admin/users/{id}/unlock", async (
    string id,
    ClaimsPrincipal currentUser,
    UserManager<IdentityUser> userManager) =>
{
    if (!IsUserAdmin(currentUser))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var user = await userManager.FindByIdAsync(id);
    if (user == null)
    {
        return Results.NotFound(new { message = "Bruger ikke fundet." });
    }

    await userManager.SetLockoutEndDateAsync(user, null);
    await userManager.ResetAccessFailedCountAsync(user);

    return Results.Ok(new { message = "Bruger låst op." });
})
.RequireAuthorization()
.WithName("UnlockAdminUser");

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
.RequireAuthorization()
.WithName("UploadDocument");

// DATABEREGNING ENDPOINTS (Native execution with Workqueue robot integration)
app.MapDataberegning();

// Seed default admin user and ensure tables exist safely (concurrency-safe for parallel tests)
await _dbInitLock.WaitAsync();
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync();
    }
    catch
    {
        // Ignore if tables were created concurrently
    }

    // Ensure all Identity tables and Documents table exist even if database was created by Worker or partially migrated
    try
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AspNetRoles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetRoles" PRIMARY KEY,
                "Name" TEXT NULL,
                "NormalizedName" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "RoleNameIndex" ON "AspNetRoles" ("NormalizedName");

            CREATE TABLE IF NOT EXISTS "AspNetUsers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetUsers" PRIMARY KEY,
                "UserName" TEXT NULL,
                "NormalizedUserName" TEXT NULL,
                "Email" TEXT NULL,
                "NormalizedEmail" TEXT NULL,
                "EmailConfirmed" INTEGER NOT NULL,
                "PasswordHash" TEXT NULL,
                "SecurityStamp" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL,
                "PhoneNumber" TEXT NULL,
                "PhoneNumberConfirmed" INTEGER NOT NULL,
                "TwoFactorEnabled" INTEGER NOT NULL,
                "LockoutEnd" TEXT NULL,
                "LockoutEnabled" INTEGER NOT NULL,
                "AccessFailedCount" INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "EmailIndex" ON "AspNetUsers" ("NormalizedEmail");
            CREATE UNIQUE INDEX IF NOT EXISTS "UserNameIndex" ON "AspNetUsers" ("NormalizedUserName");

            CREATE TABLE IF NOT EXISTS "AspNetRoleClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY AUTOINCREMENT,
                "RoleId" TEXT NOT NULL CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AspNetRoleClaims_RoleId" ON "AspNetRoleClaims" ("RoleId");

            CREATE TABLE IF NOT EXISTS "AspNetUserClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserClaims_UserId" ON "AspNetUserClaims" ("UserId");

            CREATE TABLE IF NOT EXISTS "AspNetUserLogins" (
                "LoginProvider" TEXT NOT NULL,
                "ProviderKey" TEXT NOT NULL,
                "ProviderDisplayName" TEXT NULL,
                "UserId" TEXT NOT NULL CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
                CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey")
            );
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserLogins_UserId" ON "AspNetUserLogins" ("UserId");

            CREATE TABLE IF NOT EXISTS "AspNetUserRoles" (
                "UserId" TEXT NOT NULL CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
                "RoleId" TEXT NOT NULL CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE,
                CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId")
            );
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserRoles_RoleId" ON "AspNetUserRoles" ("RoleId");

            CREATE TABLE IF NOT EXISTS "AspNetUserTokens" (
                "UserId" TEXT NOT NULL CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
                "LoginProvider" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Value" TEXT NULL,
                CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name")
            );

            CREATE TABLE IF NOT EXISTS "Documents" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Documents" PRIMARY KEY,
                "FileName" TEXT NOT NULL,
                "FilePath" TEXT NOT NULL,
                "UploadedAt" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ErrorMessage" TEXT NULL,
                "Language" TEXT NULL,
                "ArticleId" TEXT NULL,
                "SourceDocumentId" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Documents_FileName" ON "Documents" ("FileName");
            CREATE INDEX IF NOT EXISTS "IX_Documents_ArticleId" ON "Documents" ("ArticleId");
        """);
    }
    catch
    {
        // Ignore if executed concurrently
    }

    try
    {
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roleManager.RoleExistsAsync("Admin"))
        {
            await roleManager.CreateAsync(new IdentityRole("Admin"));
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var adminEmail = "admin@ebmpabst.dk";
        var defaultUser = await userManager.FindByEmailAsync(adminEmail);
        if (defaultUser == null)
        {
            defaultUser = new IdentityUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true
            };
            var result = await userManager.CreateAsync(defaultUser, "Admin123!");
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(defaultUser, "Admin");
                app.Logger.LogInformation("Default admin user created and assigned Admin role: {Email}", adminEmail);
            }
            else
            {
                app.Logger.LogWarning("Failed to create default admin user: {Errors}", string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
        else
        {
            if (!await userManager.IsInRoleAsync(defaultUser, "Admin"))
            {
                await userManager.AddToRoleAsync(defaultUser, "Admin");
            }
        }
    }
    catch
    {
        // Safe concurrency handling
    }
}
finally
{
    _dbInitLock.Release();
}

app.Run();

static bool IsUserAdmin(ClaimsPrincipal user)
{
    if (user.Identity?.IsAuthenticated != true) return false;
    return user.IsInRole("Admin") ||
           string.Equals(user.Identity.Name, "admin@ebmpabst.dk", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(user.Identity.Name, "ApiKeyClient", StringComparison.OrdinalIgnoreCase);
}

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

static async Task<IResult> GetDocumentStatusResultAsync(
    string fileName,
    IDocumentRepository repository,
    IVectorStore vectorStore,
    IEnumerable<string> uploadDirectories)
{
    if (string.IsNullOrWhiteSpace(fileName))
    {
        return Results.BadRequest(new { error = "The 'fileName' parameter cannot be empty." });
    }

    var cleanName = fileName.Trim();
    var targetFileName = Path.GetFileName(cleanName);
    if (string.IsNullOrWhiteSpace(targetFileName))
    {
        targetFileName = cleanName;
    }

    // 1. Fetch documents from repository and vector store
    var repositoryDocs = await repository.GetAllDocumentsAsync();
    var vectorDocs = await vectorStore.GetDocumentsAsync();

    var allDocs = repositoryDocs
        .Concat(vectorDocs)
        .GroupBy(doc => doc.Id)
        .Select(group => group
            .OrderByDescending(doc => doc.UploadedAt)
            .ThenByDescending(doc => !string.IsNullOrWhiteSpace(doc.FileName))
            .First())
        .Select(doc => doc with { FilePath = ToDocumentUrl(doc.FilePath, doc.FileName, uploadDirectories) })
        .ToList();

    // 2. Find matching documents
    var matches = allDocs.Where(doc =>
    {
        if (string.Equals(doc.FileName, cleanName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(doc.FileName))
        {
            var docFileNameOnly = Path.GetFileName(doc.FileName);
            if (string.Equals(docFileNameOnly, targetFileName, StringComparison.OrdinalIgnoreCase))
                return true;

            var docWithoutExt = Path.GetFileNameWithoutExtension(docFileNameOnly);
            var targetWithoutExt = Path.GetFileNameWithoutExtension(targetFileName);
            if (!string.IsNullOrWhiteSpace(targetWithoutExt) &&
                string.Equals(docWithoutExt, targetWithoutExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (string.Equals(doc.Id, cleanName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(doc.FilePath))
        {
            var docPathFileName = Path.GetFileName(Uri.UnescapeDataString(doc.FilePath));
            if (string.Equals(docPathFileName, targetFileName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (docPathFileName.EndsWith($"_{targetFileName}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }).ToList();

    if (matches.Count > 0)
    {
        int MatchScore(Document d)
        {
            if (string.Equals(d.FileName, cleanName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(d.FileName), targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            return 1;
        }

        int StatusPriority(DocumentStatus status) => status switch
        {
            DocumentStatus.Processed => 1,
            DocumentStatus.Skipped => 2,
            DocumentStatus.Processing => 3,
            DocumentStatus.Pending => 4,
            DocumentStatus.Failed => 5,
            _ => 6
        };

        var bestMatch = matches
            .OrderBy(MatchScore)
            .ThenBy(m => StatusPriority(m.Status))
            .ThenByDescending(m => m.UploadedAt)
            .First();

        return Results.Ok(new DocumentStatusResponse(
            HasBeenUploaded: true,
            Status: bestMatch.Status.ToString(),
            DocumentId: bestMatch.Id,
            FileName: bestMatch.FileName,
            FilePath: bestMatch.FilePath,
            UploadedAt: bestMatch.UploadedAt,
            ErrorMessage: bestMatch.ErrorMessage,
            Language: bestMatch.Language,
            ArticleId: bestMatch.ArticleId,
            SourceDocumentId: bestMatch.SourceDocumentId
        ));
    }

    // 3. Fallback: check if the file exists directly in served upload directories
    var existingUrl = ToExistingUploadUrl(targetFileName, uploadDirectories);
    if (!string.IsNullOrWhiteSpace(existingUrl))
    {
        return Results.Ok(new DocumentStatusResponse(
            HasBeenUploaded: true,
            Status: "Uploaded",
            DocumentId: null,
            FileName: targetFileName,
            FilePath: existingUrl,
            UploadedAt: null,
            ErrorMessage: null
        ));
    }

    // 4. Not uploaded / not found
    return Results.Ok(new DocumentStatusResponse(
        HasBeenUploaded: false,
        Status: "NotUploaded",
        DocumentId: null,
        FileName: targetFileName,
        FilePath: null,
        UploadedAt: null,
        ErrorMessage: null
    ));
}

// Required to make Program class visible to integration/E2E test project
public partial class Program
{
    private static readonly SemaphoreSlim _dbInitLock = new(1, 1);
}
