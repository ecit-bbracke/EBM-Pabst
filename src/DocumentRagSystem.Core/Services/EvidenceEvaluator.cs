using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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

    public EvidenceEvaluator(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<List<EvidenceClaim>> EvaluateEvidenceAsync(
        string question,
        string draftResponse,
        IEnumerable<DocumentChunk> chunks
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var prompt = $$"""
            You are the Evidence Layer evaluator for a highly specialized Technical RAG Orchestrator for ventilation and motor systems (ebm-papst domain).
            Your job is to identify the key technical claims made or implied in the draft response to the question, and evaluate them based ONLY on the provided retrieved documentation context chunks and established ebm-papst domain rules.

            Domain Knowledge Rules:
            - 12th digit of axial fan codes indicates airflow direction (even digit = direction A; odd digit = direction V).
            - 'A' is base axial, 'S' is with guard grille, 'W' is with wall ring.
            - 'R' is centrifugal single-inlet, 'K' is in bracket/RadiPac, 'G' is in single scroll, 'D' is dual scroll.
            - '3G' is EC technology; AC codes indicate pole count and phase (E = 1-phase, D = 3-phase).
            - If a claim about airflow direction, diameter, or technology is directly deduced from a standard product code, it should be marked as DERIVED or SUPPORTED.

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
            {{string.Join("\n\n", chunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Question: {{question}}
            Draft Response: {{draftResponse}}
            """;

        try
        {
            var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var result = JsonSerializer.Deserialize<List<EvidenceClaim>>(response, options);
            if (result != null)
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in EvidenceEvaluator: {ex.Message}");
        }

        return new List<EvidenceClaim>();
    }
}
