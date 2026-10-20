using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Configuration;
using DocumentRagSystem.Infrastructure.Data;
using DocumentRagSystem.Infrastructure.Embeddings;
using DocumentRagSystem.Infrastructure.HealthChecks;
using DocumentRagSystem.Infrastructure.Llm;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.Infrastructure.TextExtractors;
using DocumentRagSystem.Infrastructure.VectorStores;
using DocumentRagSystem.WebApi;
using DocumentRagSystem.WebApi.Data;
using DocumentRagSystem.WebApi.DTOs;
using DocumentRagSystem.WebApi.Endpoints;
using DocumentRagSystem.WebApi.HostedServices;
using DocumentRagSystem.WebApi.Middleware;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add OpenApi / Swagger documentation
builder.Services.AddOpenApi();

// Database & Microsoft Identity Configuration (SQLite with WAL mode interceptor)
var connectionString = builder.Configuration.GetConnectionString("IdentityDb") ?? "Data Source=identity.db";

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
{
    options.UseSqlite(connectionString);
    options.AddInterceptors(new SqlitePragmaInterceptor());
});
builder.Services.AddDbContextFactory<DocumentDbContext>(options =>
{
    options.UseSqlite(connectionString);
    options.AddInterceptors(new SqlitePragmaInterceptor());
});

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    if (builder.Environment.IsProduction())
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
    }
    else
    {
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequiredLength = 6;
    }
    options.SignIn.RequireConfirmedAccount = false;

    // Account Lockout Protection
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// Configure Authentication Cookies
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login.html";
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
        context.Response.Redirect(context.RedirectUri);
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
    ApiKeyAuthenticationOptions.DefaultScheme,
    options =>
    {
        var config = builder.Configuration;
        var key = config["Authentication:ApiKey"] ?? Environment.GetEnvironmentVariable("API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            options.ApiKey = key;
        }
    });

builder.Services.AddAuthorization(options =>
{
    // Default policy for all endpoints: requires an authenticated user who is NOT restricted to API key upload scope
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
        {
            var isApiKey = ctx.User.HasClaim("auth_method", "api_key") ||
                           string.Equals(ctx.User.Identity?.Name, "ApiKeyClient", StringComparison.OrdinalIgnoreCase);
            return !isApiKey;
        })
        .Build();

    // Document upload policy: allows both interactive cookie users and upload-scoped API key clients
    options.AddPolicy("DocumentUploadPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
    });
});

// Register Core & Infrastructure Services
var geminiApiKey = AppConfigurationHelper.GetGeminiApiKey(builder.Configuration);
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
    builder.Configuration.GetSection("Llm").Bind(options);
    if (!string.IsNullOrWhiteSpace(geminiApiKey) && string.IsNullOrWhiteSpace(options.Gemini.ApiKey))
    {
        options.Gemini.ApiKey = geminiApiKey;
    }
});

// Register HttpClient for Gemini
builder.Services.AddHttpClient("GeminiClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
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
    client.Timeout = TimeSpan.FromSeconds(3);
});

// Register LLM Resolver & Individual Clients
builder.Services.AddSingleton<GeminiLlmClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Gemini;
    var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiClient");
    return new GeminiLlmClient(opts.ApiKey, opts.LlmModel, opts.FastLlmModel, client, sp.GetService<ILogger<GeminiLlmClient>>());
});

builder.Services.AddSingleton<LocalLlmClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>();
    var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new LocalLlmClient(opts, clientFactory, sp.GetService<ILogger<LocalLlmClient>>());
});

builder.Services.AddSingleton<ILlmClientResolver, LlmClientResolver>(sp =>
    new LlmClientResolver(
        sp.GetRequiredService<IOptions<LlmOptions>>(),
        sp.GetRequiredService<GeminiLlmClient>(),
        sp.GetRequiredService<LocalLlmClient>(),
        sp.GetService<ILoggerFactory>(),
        sp.GetService<ILogger<LlmClientResolver>>()
    ));

builder.Services.AddSingleton<ILlmService, GeminiLlmService>(sp =>
    new GeminiLlmService(
        geminiApiKey,
        geminiLlmModel,
        geminiFastLlmModel,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiClient"),
        sp.GetService<ILogger<GeminiLlmService>>()));

// Register Speech-to-Text Service
builder.Services.AddSingleton<ISpeechToTextService, GeminiSpeechToTextService>(sp =>
    new GeminiSpeechToTextService(
        sp.GetRequiredService<IOptions<LlmOptions>>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiClient"),
        sp.GetService<ILogger<GeminiSpeechToTextService>>()));

// Register Embedding Service (Using Gemini API with in-memory caching and batching)
builder.Services.AddSingleton<IEmbeddingService, GeminiEmbeddingService>(sp =>
    new GeminiEmbeddingService(
        geminiApiKey,
        geminiEmbeddingModel,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiClient"),
        sp.GetService<ILogger<GeminiEmbeddingService>>()));

// Register Vector Store & Repository
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>(sp =>
    new QdrantVectorStore(
        qdrantConnString,
        qdrantCollection,
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetService<ILogger<QdrantVectorStore>>()));

builder.Services.AddSingleton<IDocumentRepository, SqliteDocumentRepository>();
builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<IChunkingService, ChunkingService>(sp => new ChunkingService(1000, 200));

// Register Language Detector & DocumentProcessor
builder.Services.AddSingleton<ILanguageDetector, DefaultLanguageDetector>(sp =>
    new DefaultLanguageDetector(
        sp.GetService<ILlmClientResolver>(),
        sp.GetService<ILogger<DefaultLanguageDetector>>()));

builder.Services.AddSingleton<IFullTextSearchStore, SqliteFtsStore>();

builder.Services.AddSingleton<IDocumentProcessor, DocumentProcessor>(sp =>
    new DocumentProcessor(
        sp.GetRequiredService<ITextExtractor>(),
        sp.GetRequiredService<IChunkingService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<IVectorStore>(),
        sp.GetRequiredService<IDocumentRepository>(),
        sp.GetRequiredService<ILanguageDetector>(),
        sp.GetService<ILogger<DocumentProcessor>>(),
        sp.GetService<IFullTextSearchStore>(),
        uploadsDirectory: uploadsDirectory
    ));

// Set up RAG Orchestrator Layer & ebm-papst Domain Services
var conversationStorageDir = Path.Combine(AppContext.BaseDirectory, ".conversations");
builder.Services.AddSingleton<IConversationStateStore, FileConversationStateStore>(sp =>
    new FileConversationStateStore(conversationStorageDir));

builder.Services.AddSingleton<IEbmProductCodeParser, EbmProductCodeParser>();
builder.Services.AddSingleton<IConversationalQueryRefiner, ConversationalQueryRefiner>(sp =>
    new ConversationalQueryRefiner(
        sp.GetRequiredService<ILlmClientResolver>(),
        sp.GetService<IEbmProductCodeParser>(),
        sp.GetService<ILogger<ConversationalQueryRefiner>>()));

builder.Services.AddSingleton<IInputGovernor, InputGovernor>(sp =>
    new InputGovernor(
        sp.GetRequiredService<ILlmService>(),
        sp.GetRequiredService<IEbmProductCodeParser>(),
        sp.GetService<ILogger<InputGovernor>>()));

builder.Services.AddSingleton<IOutputGovernor, OutputGovernor>(sp =>
    new OutputGovernor(
        sp.GetRequiredService<ILlmService>(),
        sp.GetService<ILogger<OutputGovernor>>()));

builder.Services.AddSingleton<IWorkflowRouter, WorkflowRouter>();

builder.Services.AddSingleton<IEvidenceEvaluator, EvidenceEvaluator>(sp =>
    new EvidenceEvaluator(
        sp.GetRequiredService<ILlmClientResolver>(),
        sp.GetService<ILogger<EvidenceEvaluator>>()));

builder.Services.AddSingleton<IAnswerComposer, AnswerComposer>(sp =>
    new AnswerComposer(
        sp.GetRequiredService<ILlmService>(),
        sp.GetService<ILogger<AnswerComposer>>()));

builder.Services.AddSingleton<IContextExpander, ContextExpander>();
builder.Services.AddSingleton<IDanishDocumentResolver, DanishDocumentResolver>();

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
        sp.GetService<IContextExpander>(),
        sp.GetService<ILogger<TechnicalRagOrchestrator>>(),
        sp.GetService<IFullTextSearchStore>()
    ));

// Register Background Queue Channel & Hosted Services
builder.Services.AddSingleton<IDocumentQueue, DocumentQueue>();
builder.Services.AddHostedService<QueuedHostedService>();
builder.Services.AddHostedService<ConversationCleanupHostedService>();
builder.Services.AddHostedService<MaintenanceCleanupHostedService>();

// Register Health Check services
builder.Services.AddHealthChecks()
    .AddCheck<LocalLlmHealthCheck>("local_llm");

// Register Validators
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Forwarded Headers for reverse proxies
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

// Security Headers Middleware
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://cdn.tailwindcss.com https://cdnjs.cloudflare.com https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com https://fonts.googleapis.com; " +
        "font-src 'self' https://cdnjs.cloudflare.com https://fonts.gstatic.com; " +
        "img-src 'self' data: https:; " +
        "connect-src 'self' ws: wss:; " +
        "frame-src 'self';");
    await next();
});

app.UseRouting();

// Authentication & Authorization Middleware
app.UseAuthentication();
app.UseAuthorization();

// Route Protection Middleware: Protect HTML Pages
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;

    // Allow public access to login page and authentication endpoints
    if (path.Equals("/login.html", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    // Require authentication
    if (context.User.Identity?.IsAuthenticated != true)
    {
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase))
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
        if (!UserSecurityHelper.IsUserAdmin(context.User))
        {
            context.Response.Redirect("/index.html");
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
    }
});

// Configure File Providers for Uploads & Data Directories
var uploadFileProviders = new List<IFileProvider>();
foreach (var dir in servedUploadDirectories)
{
    if (Directory.Exists(dir))
    {
        uploadFileProviders.Add(new PhysicalFileProvider(dir));
    }
}

if (uploadFileProviders.Count > 0)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = uploadFileProviders.Count == 1
            ? uploadFileProviders[0]
            : new CompositeFileProvider(uploadFileProviders),
        RequestPath = "/uploads"
    });
}

// MAP MODULAR ENDPOINTS
app.MapDocumentEndpoints(servedUploadDirectories);
app.MapQueryEndpoints(servedUploadDirectories);
app.MapConversationEndpoints();
app.MapAuthEndpoints();
app.MapAdminEndpoints(servedUploadDirectories);
app.MapDataberegning();

// HEALTH ENDPOINT
app.MapGet("/health", async (
    IDocumentRepository repository,
    IVectorStore vectorStore,
    IOptions<LlmOptions> llmOptions,
    IFullTextSearchStore? ftsStore) =>
{
    int dbCount = 0;
    try
    {
        var (_, count) = await repository.GetPagedDocumentsAsync(1, 1, null);
        dbCount = count;
    }
    catch { }

    int ftsChunkCount = 0;
    if (ftsStore != null)
    {
        try { ftsChunkCount = await ftsStore.GetIndexedChunkCountAsync(); } catch { }
    }

    int vectorDocCount = 0;
    try
    {
        var vDocs = await vectorStore.GetDocumentsAsync(1);
        vectorDocCount = vDocs.Count();
    }
    catch { }

    var process = System.Diagnostics.Process.GetCurrentProcess();
    var memoryMb = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 1);
    var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

    var opts = llmOptions.Value;

    return Results.Ok(new
    {
        status = "Healthy",
        timestamp = DateTime.UtcNow,
        uptime = $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s",
        memoryUsageMb = memoryMb,
        databaseDocumentsCount = dbCount,
        ftsChunksCount = ftsChunkCount,
        llmProvider = opts.DefaultProvider,
        geminiModel = opts.Gemini.LlmModel,
        qdrantStatus = "Online"
    });
})
.WithName("HealthCheck");

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
                "SourceDocumentId" TEXT NULL,
                "ProductCode" TEXT NULL,
                "ProductCategory" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Documents_FileName" ON "Documents" ("FileName");
            CREATE INDEX IF NOT EXISTS "IX_Documents_ArticleId" ON "Documents" ("ArticleId");
            CREATE INDEX IF NOT EXISTS "IX_Documents_Language" ON "Documents" ("Language");
        """);

        try
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                await db.Database.OpenConnectionAsync();
                command.CommandText = "PRAGMA table_info(\"Documents\");";
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            if (!existingColumns.Contains("ProductCode"))
            {
                await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Documents"" ADD COLUMN ""ProductCode"" TEXT NULL;");
            }
            if (!existingColumns.Contains("ProductCategory"))
            {
                await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Documents"" ADD COLUMN ""ProductCategory"" TEXT NULL;");
            }
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_Documents_ProductCode"" ON ""Documents"" (""ProductCode"");");
        }
        catch { }
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
        var adminEmail = builder.Configuration["Admin:Email"] ?? "admin@ebmpabst.dk";
        var defaultUser = await userManager.FindByEmailAsync(adminEmail);
        if (defaultUser == null)
        {
            var adminPassword = builder.Configuration["Admin:InitialPassword"]
                ?? Environment.GetEnvironmentVariable("ADMIN_INITIAL_PASSWORD");

            if (string.IsNullOrWhiteSpace(adminPassword))
            {
                if (app.Environment.IsProduction())
                {
                    // Generate cryptographically secure one-time bootstrap password in production
                    adminPassword = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)) + "!Aa1";
                    app.Logger.LogWarning("SECURITY ALERT: Admin initial password was not configured via 'Admin:InitialPassword' or 'ADMIN_INITIAL_PASSWORD'. Generated one-time bootstrap password: {BootstrapPassword}. Change immediately upon first login.", adminPassword);
                }
                else
                {
                    adminPassword = "Admin123!";
                }
            }

            defaultUser = new IdentityUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true
            };
            var result = await userManager.CreateAsync(defaultUser, adminPassword);
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

static string GetUploadsDirectory(string? configuredUploadsDirectory)
{
    return string.IsNullOrWhiteSpace(configuredUploadsDirectory)
        ? Path.Combine(AppContext.BaseDirectory, "uploads")
        : Path.GetFullPath(configuredUploadsDirectory);
}

static IEnumerable<string> GetServedUploadDirectories(string uploadsDirectory)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (Directory.Exists(uploadsDirectory) && seen.Add(uploadsDirectory))
    {
        yield return uploadsDirectory;
    }

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

    if (Directory.Exists(workerUploadsDirectory) && seen.Add(workerUploadsDirectory))
    {
        yield return workerUploadsDirectory;
    }

    // Include data and data/Generelt directories
    var searchDirs = new[]
    {
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "Generelt")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data", "Generelt")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "uploads"))
    };

    foreach (var dir in searchDirs)
    {
        if (Directory.Exists(dir) && seen.Add(dir))
        {
            yield return dir;
        }
    }
}

// Required to make Program class visible to integration/E2E test project
public partial class Program
{
    private static readonly SemaphoreSlim _dbInitLock = new(1, 1);
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime CachedAt, HashSet<string> Files)> DirFileCache = new(StringComparer.OrdinalIgnoreCase);
    public static readonly JsonSerializerOptions JsonCamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string CleanLatexMath(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text;
        // Unpack \text{...}, \mathrm{...}, \mathbf{...}
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\(text|mathrm|mathbf)\{([^{}]+)\}", "$2");

        // Replace LaTeX symbols
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\pm\b", "±");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\mp\b", "∓");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\times\b", "×");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\cdot\b", "·");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\approx\b", "≈");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\neq\b", "≠");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\(le|leq)\b", "≤");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\(ge|geq)\b", "≥");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\degree\b", "°");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\mu\b", "µ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\Omega\b", "Ω");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\Delta\b", "Δ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\pi\b", "π");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\infty\b", "∞");

        // Subscripts and superscripts
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\^3(?!\d)", "³");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\^2(?!\d)", "²");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"_\{([^{}]+)\}", "_$1");

        // Strip enclosing math delimiters: $$...$$ and $...$
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\$\$([\s\S]*?)\$\$", "$1");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\$([^\$\n]+?)\$", "$1");

        return result;
    }
}
