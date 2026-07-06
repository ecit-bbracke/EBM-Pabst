using DocumentRagSystem.Worker;
using DocumentRagSystem.Worker.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.TextExtractors;
using DocumentRagSystem.Infrastructure.Embeddings;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.Infrastructure.VectorStores;

var builder = Host.CreateApplicationBuilder(args);

// Register Core & Infrastructure Services
var geminiApiKey = builder.Configuration["Gemini:ApiKey"];
var geminiEmbeddingModel = builder.Configuration["Gemini:EmbeddingModel"] ?? "text-embedding-004";

var qdrantConnString = builder.Configuration["Qdrant:ConnectionString"] ?? "http://localhost:6334";
var qdrantCollection = builder.Configuration["Qdrant:CollectionName"] ?? "document-chunks";

// Add Singletons and Scoped Services
builder.Services.AddSingleton<IDocumentRepository, InMemoryDocumentRepository>();
builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<IChunkingService, ChunkingService>(sp => new ChunkingService(1000, 200));

// Set up Gemini embedding service
builder.Services.AddSingleton<IEmbeddingService, GeminiEmbeddingService>(sp => 
    new GeminiEmbeddingService(geminiApiKey, geminiEmbeddingModel));

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

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<PeriodicCleanupService>();

var host = builder.Build();
host.Run();
