using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

/// <summary>
/// Provider-neutral abstraction for LLM execution supporting text, structured, and streaming generation.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// The unique name of this provider (e.g. "Gemini", "Local").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Generates free-form text completion based on the supplied request.
    /// </summary>
    Task<LlmTextResult> GenerateTextAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a typed structured response deserialized to <typeparamref name="T"/>.
    /// </summary>
    Task<LlmStructuredResult<T>> GenerateStructuredAsync<T>(
        LlmRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams tokens for the given request.
    /// </summary>
    IAsyncEnumerable<string> StreamTextAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);
}
