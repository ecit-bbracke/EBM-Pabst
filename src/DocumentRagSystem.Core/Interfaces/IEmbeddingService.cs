using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text);
}
