using System.Collections.Generic;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IChunkingService
{
    IEnumerable<DocumentChunk> ChunkText(string text, string documentId);
}
