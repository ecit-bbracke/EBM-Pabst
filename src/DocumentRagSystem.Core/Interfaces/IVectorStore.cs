using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IVectorStore
{
    Task AddChunkAsync(DocumentChunk chunk, float[] embedding, CancellationToken cancellationToken = default);

    async Task AddChunksAsync(IEnumerable<(DocumentChunk Chunk, float[] Embedding)> chunksWithEmbeddings, CancellationToken cancellationToken = default)
    {
        if (chunksWithEmbeddings == null) return;
        foreach (var (chunk, embedding) in chunksWithEmbeddings)
        {
            await AddChunkAsync(chunk, embedding, cancellationToken);
        }
    }

    Task<IEnumerable<DocumentChunk>> SearchAsync(string query, int limit = 3, CancellationToken cancellationToken = default);
    Task<IEnumerable<Document>> GetDocumentsAsync(int limit = 1000, CancellationToken cancellationToken = default);
    Task<IEnumerable<DocumentChunk>> GetChunksByDocumentAndIndicesAsync(string documentId, IEnumerable<int> indices, CancellationToken cancellationToken = default);
    Task<IEnumerable<DocumentChunk>> GetAllChunksByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
        => GetChunksByDocumentAndIndicesAsync(documentId, System.Linq.Enumerable.Range(0, 100_000), cancellationToken);
    Task DeleteDocumentChunksAsync(string documentId, CancellationToken cancellationToken = default);
}
