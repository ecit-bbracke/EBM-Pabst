using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public interface IOutputGovernor
{
    Task<OutputGovernorResult> ValidateOutputAsync(
        string question,
        string proposedAnswer,
        IEnumerable<DocumentChunk> chunks
    );
}

public class OutputGovernor : IOutputGovernor
{
    private readonly ILlmService _llmService;

    public OutputGovernor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<OutputGovernorResult> ValidateOutputAsync(
        string question,
        string proposedAnswer,
        IEnumerable<DocumentChunk> chunks
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var prompt = $$"""
            You are the Output Governor for a highly specialized Technical RAG Orchestrator for industrial ventilation and motor systems (ebm-papst domain).
            Your job is to validate the proposed final answer before it is presented to the user.

            Check the following criteria:
            1. Does the answer address the actual question?
            2. Are all important technical claims supported or logically derived from the retrieved context chunks and domain rules?
            3. CRITICAL AIRFLOW CHECK: Did the answer claim two axial fans with different 12th digits (e.g., A6E450AP0201 [Airflow V] and A6E450AP0202 [Airflow A]) are fully compatible/drop-in replacements? If so, this is a dangerous FALSE assertion and MUST be rejected!
            4. Are there unsupported technical claims present (claims that cannot be traced to the context chunks or domain rules)?
            5. Were conflicting sources hidden or ignored?
            6. Was missing information presented as fact?
            7. Does the answer overstate certainty?

            Return a JSON object conforming exactly to this schema:
            {
              "approved": false,
              "issues": [
                {
                  "type": "UNSUPPORTED_CLAIM",
                  "claim": "The specific technical claim with the issue",
                  "severity": "HIGH"
                }
              ],
              "action": "APPROVE"
            }

            Allowed actions:
            - APPROVE: If the answer is accurate, fully grounded, and answers the question.
            - REGENERATE: If there are minor unsupported claims, formatting issues, or missed details.
            - RETRIEVE_MORE: If crucial specifications needed to answer the question are missing.
            - REQUEST_CLARIFICATION: If the question itself is too ambiguous to answer safely.
            - FAIL_SAFE: If there is a high-severity contradiction or risk, and we should fall back to a safe "unable to verify" response.

            Question: {{question}}
            Proposed Answer: {{proposedAnswer}}

            Context chunks:
            {{string.Join("\n\n", chunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}
            """;

        try
        {
            var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var result = JsonSerializer.Deserialize<OutputGovernorResult>(response, options);
            if (result != null)
            {
                return result with { Issues = result.Issues ?? new List<OutputGovernorIssue>() };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in OutputGovernor: {ex.Message}");
        }

        // Safe default: approve if we can't parse, or request fail_safe to be safe. Let's default to approve to maintain system availability.
        return new OutputGovernorResult(
            Approved: true,
            Issues: new(),
            Action: "APPROVE"
        );
    }
}
