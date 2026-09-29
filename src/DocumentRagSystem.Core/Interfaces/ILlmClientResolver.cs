namespace DocumentRagSystem.Core.Interfaces;

/// <summary>
/// Architectural responsibilities / components in the RAG pipeline that may require different LLM providers.
/// </summary>
public enum LlmPurpose
{
    QueryRefiner,
    InputGovernor,
    EvidenceEvaluator,
    AnswerComposer,
    OutputGovernor,
    Workflow,
    General
}

/// <summary>
/// Resolves the appropriate ILlmClient for a given architectural responsibility or provider name.
/// </summary>
public interface ILlmClientResolver
{
    /// <summary>
    /// Resolves the configured ILlmClient for the specified architectural purpose.
    /// </summary>
    ILlmClient Resolve(LlmPurpose purpose);

    /// <summary>
    /// Resolves an ILlmClient directly by its provider name (e.g. "Gemini", "Local").
    /// </summary>
    ILlmClient Resolve(string providerName);
}
