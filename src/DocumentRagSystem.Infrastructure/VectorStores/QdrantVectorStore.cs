using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DocumentRagSystem.Infrastructure.VectorStores;

public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly IEmbeddingService? _embeddingService;
    private bool _collectionCreated;
    private int _embeddingSize;

    public QdrantVectorStore(string connectionString, string collectionName, IEmbeddingService? embeddingService = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        _collectionName = collectionName ?? throw new ArgumentNullException(nameof(collectionName));
        _embeddingService = embeddingService;

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

        try
        {
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

        await EnsureCollectionExistsAsync(queryEmbedding.Length);

        // Perform semantic search
        var searchResults = await _client.SearchAsync(
            collectionName: _collectionName,
            vector: queryEmbedding,
            limit: (uint)limit
        );

        var chunks = new List<DocumentChunk>();
        foreach (var point in searchResults)
        {
            var chunkId = point.Payload.TryGetValue("chunk_id", out var idVal) ? idVal.StringValue : point.Id.ToString();
            var docId = point.Payload.TryGetValue("document_id", out var docVal) ? docVal.StringValue : string.Empty;
            var text = point.Payload.TryGetValue("text", out var textVal) ? textVal.StringValue : string.Empty;
            var index = point.Payload.TryGetValue("index", out var idxVal) ? (int)idxVal.IntegerValue : 0;

            chunks.Add(new DocumentChunk(chunkId, docId, text, index));
        }

        return chunks;
    }
}
