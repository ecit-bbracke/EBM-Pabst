using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Configuration;
using DocumentRagSystem.Infrastructure.Data;
using DocumentRagSystem.Infrastructure.Embeddings;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.Infrastructure.TextExtractors;
using DocumentRagSystem.Infrastructure.VectorStores;
using DocumentRagSystem.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Register Core & Infrastructure Services
var geminiApiKey = AppConfigurationHelper.GetGeminiApiKey(builder.Configuration);
var geminiEmbeddingModel = builder.Configuration["Gemini:EmbeddingModel"] ?? "gemini-embedding-001";

var qdrantConnString = builder.Configuration["Qdrant:ConnectionString"] ?? "http://localhost:6334";
var qdrantCollection = builder.Configuration["Qdrant:CollectionName"] ?? "document-chunks";

// Add Singletons and Scoped Services
var connectionString = builder.Configuration.GetConnectionString("IdentityDb") ?? "Data Source=identity.db";
builder.Services.AddDbContextFactory<DocumentDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddSingleton<IDocumentRepository, SqliteDocumentRepository>();
builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<IChunkingService, ChunkingService>(sp => new ChunkingService(1000, 200));

// Set up Gemini embedding service
builder.Services.AddSingleton<IEmbeddingService, GeminiEmbeddingService>(sp =>
    new GeminiEmbeddingService(
        geminiApiKey,
        geminiEmbeddingModel,
        null,
        sp.GetService<Microsoft.Extensions.Logging.ILogger<GeminiEmbeddingService>>()));

// Set up Qdrant Vector Store
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>(sp =>
    new QdrantVectorStore(
        qdrantConnString,
        qdrantCollection,
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetService<Microsoft.Extensions.Logging.ILogger<QdrantVectorStore>>()));

// Set up Language Detector & overall DocumentProcessor
builder.Services.AddSingleton<ILanguageDetector, DefaultLanguageDetector>();

var uploadsDirectory = builder.Configuration["Uploads:Directory"];

builder.Services.AddSingleton<IDocumentProcessor, DocumentProcessor>(sp =>
    new DocumentProcessor(
        sp.GetRequiredService<ITextExtractor>(),
        sp.GetRequiredService<IChunkingService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<IVectorStore>(),
        sp.GetRequiredService<IDocumentRepository>(),
        sp.GetRequiredService<ILanguageDetector>(),
        sp.GetService<Microsoft.Extensions.Logging.ILogger<DocumentProcessor>>(),
        null,
        uploadsDirectory
    ));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
