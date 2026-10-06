using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IContextExpander
{
    Task<List<DocumentChunk>> ExpandContextAsync(
        IEnumerable<DocumentChunk> initialChunks,
        IVectorStore vectorStore,
        int windowSize = 1,
        CancellationToken cancellationToken = default);
}
