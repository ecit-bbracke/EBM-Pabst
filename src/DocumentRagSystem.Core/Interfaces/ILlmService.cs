using System.Collections.Generic;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface ILlmService
{
    Task<string> GenerateResponseAsync(string query, IEnumerable<DocumentChunk> contextChunks);
    Task<string> GenerateCompletionAsync(string prompt, bool requireJson = false);
}
