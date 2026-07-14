using System.Collections.Generic;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IVectorStore
{
    Task AddChunkAsync(DocumentChunk chunk, float[] embedding);
    Task<IEnumerable<DocumentChunk>> SearchAsync(string query, int limit = 3);
    Task<IEnumerable<Document>> GetDocumentsAsync(int limit = 1000);
}
