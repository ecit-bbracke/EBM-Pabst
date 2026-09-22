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
    private readonly ILlmService _llmService;
    private readonly ILogger<EvidenceEvaluator>? _logger;

    public EvidenceEvaluator(ILlmService llmService, ILogger<EvidenceEvaluator>? logger = null)
    {
        _llmService = llmService;
        _logger = logger;
    }

    public async Task<List<EvidenceClaim>> EvaluateEvidenceAsync(
        string question,
        string draftResponse,
        IEnumerable<DocumentChunk> chunks
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var chunkList = chunks.ToList();
        var prompt = $$"""
            You are the Evidence Layer evaluator for a highly specialized Technical RAG Orchestrator for ventilation and motor systems (ebm-papst domain).
            Your job is to identify the key technical claims made or implied in the draft response to the question, and evaluate them based ONLY on the provided retrieved documentation context chunks and established ebm-papst domain rules.

            Domain Knowledge Rules:
            - 12th digit of axial fan codes indicates airflow direction (even digit = direction A; odd digit = direction V).
            - 'A' is base axial, 'S' is with guard grille, 'W' is with wall ring.
            - 'R' is centrifugal single-inlet, 'K' is in bracket/RadiPac, 'G' is in single scroll, 'D' is dual scroll.
            - '3G' is EC technology; AC codes indicate pole count and phase (E = 1-phase, D = 3-phase).
            - If a claim about airflow direction, diameter, or technology is directly deduced from a standard product code, it should be marked as DERIVED or SUPPORTED.
            - Theoretical ebm-papst series replacement patterns (e.g. recommending S-series with guard grille or W-series in wall ring for an A-series model, or R-series for a K-series model) and airflow matching rules are DERIVED domain knowledge and MUST be marked as DERIVED (Domain Rules), NOT MISSING or CONFLICTING.

            For each technical claim, determine its status:
            - SUPPORTED: The retrieved documentation explicitly states the claim.
            - DERIVED: The claim follows logically/mathematically or via domain rules from supported facts (e.g. Operating voltage is 18-30 VDC, so 24 VDC is supported; or 12th digit '02' implies airflow direction A).
            - CONFLICTING: Retrieved sources disagree on this claim.
            - MISSING: The retrieved documentation does not establish or mention this claim.

            Return a JSON array conforming exactly to this schema:
            [
              {
                "claim": "The exact technical claim text",
                "status": "SUPPORTED",
                "reason": "Detailed reason why it was classified as such based on the context",
                "sources": ["List of source document names or chunk identifiers that support/disprove/conflict"]
              }
            ]

            Context chunks:
            {{string.Join("\n\n", chunkList.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Question: {{question}}
            Draft Response: {{draftResponse}}
            """;

        var stopwatch = Stopwatch.StartNew();
        _logger?.LogInformation(
            "[EvidenceEvaluator] Executing evidence evaluation prompt ({PromptLength} chars) against {ChunkCount} context chunks.\nPrompt:\n{Prompt}",
            prompt.Length, chunkList.Count, prompt);

        try
        {
            var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
            stopwatch.Stop();

            var cleanedResponse = response.Trim();
            if (cleanedResponse.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                cleanedResponse = cleanedResponse.Substring(7);
                if (cleanedResponse.EndsWith("```"))
                {
                    cleanedResponse = cleanedResponse.Substring(0, cleanedResponse.Length - 3);
                }
            }
            else if (cleanedResponse.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                cleanedResponse = cleanedResponse.Substring(3);
                if (cleanedResponse.EndsWith("```"))
                {
                    cleanedResponse = cleanedResponse.Substring(0, cleanedResponse.Length - 3);
                }
            }
            cleanedResponse = cleanedResponse.Trim();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var result = JsonSerializer.Deserialize<List<EvidenceClaim>>(cleanedResponse, options);
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
