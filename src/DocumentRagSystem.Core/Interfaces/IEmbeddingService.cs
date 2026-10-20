using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    async Task<IList<float[]>> GenerateEmbeddingsAsync(IList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts == null || texts.Count == 0)
        {
            return new List<float[]>();
        }

        var results = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            results.Add(await GenerateEmbeddingAsync(text, cancellationToken));
        }
        return results;
    }
}
