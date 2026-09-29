using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public interface IEvidenceEvaluator
{
    Task<List<EvidenceClaim>> EvaluateEvidenceAsync(
        string question,
        string draftResponse,
        IEnumerable<DocumentChunk> chunks
    );
}

public class EvidenceEvaluator : IEvidenceEvaluator
{
    private readonly ILlmClient _llmClient;
    private readonly ILogger<EvidenceEvaluator>? _logger;

    public EvidenceEvaluator(ILlmClient llmClient, ILogger<EvidenceEvaluator>? logger = null)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _logger = logger;
    }

    public EvidenceEvaluator(ILlmClientResolver resolver, ILogger<EvidenceEvaluator>? logger = null)
        : this(resolver?.Resolve(LlmPurpose.EvidenceEvaluator) ?? throw new ArgumentNullException(nameof(resolver)), logger)
    {
    }

    public EvidenceEvaluator(ILlmService llmService, ILogger<EvidenceEvaluator>? logger = null)
        : this(new LlmServiceToClientAdapter(llmService), logger)
    {
    }

    private const string SystemInstruction = """
You are the Evidence Layer evaluator for an industrial ventilation & motor system (ebm-papst domain).
Identify technical claims in the draft response and evaluate each against retrieved documentation:
- SUPPORTED: directly stated in documentation
- DERIVED: follows logically or via ebm-papst domain rules (e.g. 12th digit 1=airflow V, 2=airflow A)
- CONFLICTING: sources disagree
- MISSING: not mentioned

Output JSON object:
{
  "claims": [
    {
      "claim": "exact claim",
      "status": "SUPPORTED | DERIVED | CONFLICTING | MISSING",
      "reason": "explanation",
      "sources": ["doc name"]
    }
  ]
}
""";

    private sealed record EvidenceClaimsEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName("claims")] List<EvidenceClaim>? Claims
    );

    public async Task<List<EvidenceClaim>> EvaluateEvidenceAsync(
        string question,
        string draftResponse,
        IEnumerable<DocumentChunk> chunks
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var chunkList = chunks.ToList();
        var contextText = string.Join("\n\n", chunkList.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"));
        var userPrompt = $"Question: {question}\nDraft Response: {draftResponse}\n\nContext:\n{contextText}";

        var stopwatch = Stopwatch.StartNew();
        _logger?.LogInformation(
            "[EvidenceEvaluator] Executing evidence evaluation prompt ({PromptLength} chars) against {ChunkCount} context chunks.\nPrompt:\n{Prompt}",
            userPrompt.Length, chunkList.Count, userPrompt);

        try
        {
            var req = LlmRequest.FromPrompt(
                userPrompt, 
                requireJson: true, 
                systemInstruction: SystemInstruction, 
                temperature: 0.0, 
                maxOutputTokens: 256);

            var structuredRes = await _llmClient.GenerateStructuredAsync<EvidenceClaimsEnvelope>(req);
            stopwatch.Stop();

            List<EvidenceClaim>? result = null;
            if (structuredRes.Value?.Claims != null && structuredRes.Value.Claims.Count > 0)
            {
                result = structuredRes.Value.Claims;
            }
            else if (!string.IsNullOrWhiteSpace(structuredRes.RawContent))
            {
                try
                {
                    result = JsonSerializer.Deserialize<List<EvidenceClaim>>(structuredRes.RawContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    // Ignore fallback deserialization error
                }
            }

            if (result != null)
            {
                int supported = result.Count(c => c.Status == "SUPPORTED");
                int derived = result.Count(c => c.Status == "DERIVED");
                int conflicting = result.Count(c => c.Status == "CONFLICTING");
                int missing = result.Count(c => c.Status == "MISSING");

                _logger?.LogInformation(
                    "[EvidenceEvaluator] Completed in {DurationMs}ms. Evaluated {TotalClaims} claims (Supported: {Supported}, Derived: {Derived}, Conflicting: {Conflicting}, Missing: {Missing}).",
                    stopwatch.ElapsedMilliseconds, result.Count, supported, derived, conflicting, missing);

                return result;
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogWarning(ex, "[EvidenceEvaluator] Error after {DurationMs}ms: {Message}.", stopwatch.ElapsedMilliseconds, ex.Message);
            Console.WriteLine($"Error in EvidenceEvaluator: {ex.Message}");
        }

        return new List<EvidenceClaim>();
    }
}
