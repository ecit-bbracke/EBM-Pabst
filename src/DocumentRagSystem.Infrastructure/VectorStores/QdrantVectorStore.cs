using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagDocument = DocumentRagSystem.Core.Models.Document;

namespace DocumentRagSystem.Infrastructure.VectorStores;

public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly IEmbeddingService? _embeddingService;
    private readonly ILogger<QdrantVectorStore>? _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _collectionCreated;
    private int _embeddingSize;

    public QdrantVectorStore(
        string connectionString, 
        string collectionName, 
        IEmbeddingService? embeddingService = null,
        ILogger<QdrantVectorStore>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        _collectionName = collectionName ?? throw new ArgumentNullException(nameof(collectionName));
        _embeddingService = embeddingService;
        _logger = logger;

        // Parse connectionString to support direct URIs or host/port
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            _client = new QdrantClient(uri);
        }
        else
        {
            // Split by colon to check host and port
            var parts = connectionString.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var port))
            {
                _client = new QdrantClient(parts[0], port);
            }
            else
            {
                _client = new QdrantClient(connectionString);
            }
        }
    }

    private async Task EnsureCollectionExistsAsync(int vectorSize)
    {
        if (_collectionCreated) return;

        await _initLock.WaitAsync();
        try
        {
            if (_collectionCreated) return;

            var collections = await _client.ListCollectionsAsync();
            bool exists = false;
            foreach (var col in collections)
            {
                if (col == _collectionName)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                await _client.CreateCollectionAsync(
                    _collectionName, 
                    new VectorParams { Size = (ulong)vectorSize, Distance = Distance.Cosine }
                );
                
                // Add full-text search index for payload text to support hybrid queries/filters
                await _client.CreatePayloadIndexAsync(
                    _collectionName,
                    "text",
                    PayloadSchemaType.Text
                );
            }

            _embeddingSize = vectorSize;
            _collectionCreated = true;
        }
        catch (Exception ex)
        {
            // Logging or throwing
            Console.WriteLine($"Error ensuring Qdrant collection: {ex.Message}");
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static Guid ToGuid(string id)
    {
        if (Guid.TryParse(id, out var guid))
            return guid;

        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(id));
        return new Guid(hash);
    }

    private async Task RecreateCollectionAsync(int vectorSize)
    {
        try
        {
            await _client.DeleteCollectionAsync(_collectionName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Note: Collection deletion skipped or failed: {ex.Message}");
        }

        await _client.CreateCollectionAsync(
            _collectionName, 
            new VectorParams { Size = (ulong)vectorSize, Distance = Distance.Cosine }
        );
        
        await _client.CreatePayloadIndexAsync(
            _collectionName,
            "text",
            PayloadSchemaType.Text
        );

        _embeddingSize = vectorSize;
        _collectionCreated = true;
    }

    public async Task AddChunkAsync(DocumentChunk chunk, float[] embedding)
    {
        if (chunk == null) throw new ArgumentNullException(nameof(chunk));
        if (embedding == null || embedding.Length == 0) throw new ArgumentNullException(nameof(embedding));

        await EnsureCollectionExistsAsync(embedding.Length);

        var point = new PointStruct
        {
            Id = ToGuid(chunk.Id),
            Vectors = new Vectors { Vector = new Vector { Data = { embedding } } },
            Payload =
            {
                ["chunk_id"] = chunk.Id,
                ["document_id"] = chunk.DocumentId,
                ["text"] = chunk.Text,
                ["index"] = chunk.Index
            }
        };

        if (!string.IsNullOrWhiteSpace(chunk.FileName))
        {
            point.Payload["file_name"] = chunk.FileName;
        }

        if (!string.IsNullOrWhiteSpace(chunk.FilePath))
        {
            point.Payload["file_path"] = chunk.FilePath;
        }

        if (chunk.UploadedAt.HasValue)
        {
            point.Payload["uploaded_at"] = chunk.UploadedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        try
        {
            await _client.UpsertAsync(_collectionName, new[] { point });
        }
        catch (Exception ex) when (ex.ToString().Contains("dimension") || ex.ToString().Contains("dim") || ex.ToString().Contains("InvalidArgument") || ex.ToString().Contains("Wrong input"))
        {
            Console.WriteLine($"Dimension mismatch or collection invalidation detected for '{_collectionName}'. Recreating with dimension {embedding.Length}...");
            await RecreateCollectionAsync(embedding.Length);
            await _client.UpsertAsync(_collectionName, new[] { point });
        }
    }

    public async Task<IEnumerable<DocumentChunk>> SearchAsync(string query, int limit = 3)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<DocumentChunk>();

        var totalStopwatch = Stopwatch.StartNew();
        var embeddingStopwatch = Stopwatch.StartNew();

        float[] queryEmbedding;
        if (_embeddingService != null)
        {
            queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query);
        }
        else
        {
            // If no embedding service, generate a mock or zero vector
            queryEmbedding = new float[_embeddingSize > 0 ? _embeddingSize : 1536];
        }
        embeddingStopwatch.Stop();

        var qdrantStopwatch = Stopwatch.StartNew();
        await EnsureCollectionExistsAsync(queryEmbedding.Length);

        // Perform semantic search
        var searchResults = await _client.SearchAsync(
            collectionName: _collectionName,
            vector: queryEmbedding,
            limit: (uint)limit
        );
        qdrantStopwatch.Stop();
        totalStopwatch.Stop();

        var chunks = new List<DocumentChunk>();
        foreach (var point in searchResults)
        {
            var chunkId = point.Payload.TryGetValue("chunk_id", out var idVal) ? idVal.StringValue : point.Id.ToString();
            var docId = point.Payload.TryGetValue("document_id", out var docVal) ? docVal.StringValue : string.Empty;
            var text = point.Payload.TryGetValue("text", out var textVal) ? textVal.StringValue : string.Empty;
            var index = point.Payload.TryGetValue("index", out var idxVal) ? (int)idxVal.IntegerValue : 0;
            var fileName = point.Payload.TryGetValue("file_name", out var fileNameVal) ? fileNameVal.StringValue : null;
            var filePath = point.Payload.TryGetValue("file_path", out var filePathVal) ? filePathVal.StringValue : null;
            var uploadedAt = TryGetUploadedAt(point.Payload);

            chunks.Add(new DocumentChunk(chunkId, docId, text, index, fileName, filePath, uploadedAt));
        }

        _logger?.LogInformation(
            "[QdrantVectorStore] Search completed in {DurationMs}ms (Embedding: {EmbeddingMs}ms, Qdrant: {SearchMs}ms). Query: \"{Query}\", Results: {ResultCount} chunks.",
            totalStopwatch.ElapsedMilliseconds, embeddingStopwatch.ElapsedMilliseconds, qdrantStopwatch.ElapsedMilliseconds, query, chunks.Count);

        return chunks;
    }

    public async Task<IEnumerable<RagDocument>> GetDocumentsAsync(int limit = 1000)
    {
        var collections = await _client.ListCollectionsAsync();
        if (!collections.Contains(_collectionName))
        {
            return Array.Empty<RagDocument>();
        }

        var documents = new Dictionary<string, RagDocument>();
        PointId? offset = null;
        var remaining = Math.Max(1, limit);

        do
        {
            var pageSize = (uint)Math.Min(256, remaining);
            var response = await _client.ScrollAsync(
                collectionName: _collectionName,
                limit: pageSize,
                offset: offset,
                payloadSelector: true,
                vectorsSelector: false);

            foreach (var point in response.Result)
            {
                if (!point.Payload.TryGetValue("document_id", out var documentIdValue) ||
                    string.IsNullOrWhiteSpace(documentIdValue.StringValue))
                {
                    continue;
                }

                var documentId = documentIdValue.StringValue;
                var uploadedAt = TryGetUploadedAt(point.Payload) ?? DateTime.MinValue;
                var document = new RagDocument(
                    Id: documentId,
                    FileName: point.Payload.TryGetValue("file_name", out var fileNameValue) &&
                              !string.IsNullOrWhiteSpace(fileNameValue.StringValue)
                        ? fileNameValue.StringValue
                        : documentId,
                    FilePath: point.Payload.TryGetValue("file_path", out var filePathValue)
                        ? filePathValue.StringValue
                        : string.Empty,
                    UploadedAt: uploadedAt,
                    Status: DocumentStatus.Processed);

                if (!documents.TryGetValue(documentId, out var existing) ||
                    document.UploadedAt > existing.UploadedAt ||
                    string.IsNullOrWhiteSpace(existing.FileName))
                {
                    documents[documentId] = document;
                }
            }

            remaining -= response.Result.Count;
            offset = response.NextPageOffset;
        }
        while (offset != null && remaining > 0);

        return documents.Values
            .OrderByDescending(document => document.UploadedAt)
            .ThenBy(document => document.FileName)
            .ToList();
    }

    private static DateTime? TryGetUploadedAt(IDictionary<string, Value> payload)
    {
        if (!payload.TryGetValue("uploaded_at", out var uploadedAtValue) ||
            string.IsNullOrWhiteSpace(uploadedAtValue.StringValue))
        {
            return null;
        }

        return DateTime.TryParse(
            uploadedAtValue.StringValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var uploadedAt)
            ? uploadedAt
            : null;
    }
}
