using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IFullTextSearchStore
{
    Task AddChunksAsync(IEnumerable<DocumentChunk> chunks, CancellationToken cancellationToken = default);
    Task<IEnumerable<DocumentChunk>> SearchAsync(string query, int limit = 10, CancellationToken cancellationToken = default);
    Task DeleteChunksByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default);
    Task<int> GetIndexedChunkCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task<HashSet<string>> GetIndexedDocumentIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HashSet<string>());
}
