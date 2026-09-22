using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface ILlmService
{
    Task<string> GenerateResponseAsync(string query, IEnumerable<DocumentChunk> contextChunks);
    Task<string> GenerateCompletionAsync(string prompt, bool requireJson = false);

    async IAsyncEnumerable<string> StreamResponseAsync(
        string query, 
        IEnumerable<DocumentChunk> contextChunks, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await GenerateResponseAsync(query, contextChunks);
        yield return result;
    }

    async IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await GenerateCompletionAsync(prompt, false);
        yield return result;
    }
}
