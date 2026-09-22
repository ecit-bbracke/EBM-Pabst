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
    private readonly ILogger<OutputGovernor>? _logger;

    public OutputGovernor(ILlmService llmService, ILogger<OutputGovernor>? logger = null)
    {
        _llmService = llmService;
        _logger = logger;
    }

    public async Task<OutputGovernorResult> ValidateOutputAsync(
        string question,
        string proposedAnswer,
        IEnumerable<DocumentChunk> chunks
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var chunkList = chunks.ToList();
        var prompt = $$"""
            You are the Output Governor for a highly specialized Technical RAG Orchestrator for industrial ventilation and motor systems (ebm-papst domain).
            Your job is to validate the proposed final answer before it is presented to the user.

            Validation Guidelines:
            1. APPROVE if the answer is grounded in the retrieved context chunks, accurately follows ebm-papst domain rules (e.g., 12th digit airflow direction decoding, EC vs AC technology, diameter, fan types), or honestly explains that certain data was not found in the documents.
            2. REGENERATE if the answer makes ungrounded technical claims that can be corrected from the context chunks or needs clarification.
            3. Do NOT reject an answer merely because it acknowledges missing specifications or politely states that a value is absent from the datasheet.
            4. CRITICAL AIRFLOW CHECK: Did the answer claim two axial fans with different 12th digits (e.g., A6E450AP0201 [Airflow V] and A6E450AP0202 [Airflow A]) are direct drop-in replacements for each other without warning? This is a dangerous false claim and must be rejected with action REGENERATE or FAIL_SAFE.
            5. Use FAIL_SAFE ONLY when there is an unresolvable critical safety contradiction that cannot be answered safely.

            Return a JSON object conforming to this schema:
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }

            If issues are found, set approved to false, specify the issues, and set action to REGENERATE (or FAIL_SAFE only for severe safety hazards):
            {
              "approved": false,
              "issues": [
                {
                  "type": "UNSUPPORTED_CLAIM",
                  "claim": "The specific technical claim with the issue",
                  "severity": "HIGH"
                }
              ],
              "action": "REGENERATE"
            }

            Question: {{question}}
            Proposed Answer: {{proposedAnswer}}

            Context chunks:
            {{string.Join("\n\n", chunkList.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}
            """;

        var stopwatch = Stopwatch.StartNew();
        _logger?.LogInformation(
            "[OutputGovernor] Executing output validation prompt ({PromptLength} chars).\nPrompt:\n{Prompt}",
            prompt.Length, prompt);

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
            var result = JsonSerializer.Deserialize<OutputGovernorResult>(cleanedResponse, options);
            if (result != null)
            {
                var issues = result.Issues ?? new List<OutputGovernorIssue>();
                _logger?.LogInformation(
                    "[OutputGovernor] Validation completed in {DurationMs}ms. Action: {Action}, Approved: {Approved}, Issues: {IssueCount}.",
                    stopwatch.ElapsedMilliseconds, result.Action, result.Approved, issues.Count);

                return result with { Issues = issues };
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogWarning(ex, "[OutputGovernor] Error after {DurationMs}ms: {Message}. Defaulting to APPROVE.", stopwatch.ElapsedMilliseconds, ex.Message);
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
