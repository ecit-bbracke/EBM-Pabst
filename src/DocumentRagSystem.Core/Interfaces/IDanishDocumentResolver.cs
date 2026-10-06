using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IDanishDocumentResolver
{
    Task<IEnumerable<Document>> FindDanishCompanionDocumentsAsync(
        IEnumerable<DocumentChunk> referencedChunks,
        CancellationToken cancellationToken = default);
}
