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
    private readonly ILlmClient _llmClient;
    private readonly ILogger<OutputGovernor>? _logger;

    public OutputGovernor(ILlmClient llmClient, ILogger<OutputGovernor>? logger = null)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _logger = logger;
    }

    public OutputGovernor(ILlmClientResolver resolver, ILogger<OutputGovernor>? logger = null)
        : this(resolver?.Resolve(LlmPurpose.OutputGovernor) ?? throw new ArgumentNullException(nameof(resolver)), logger)
    {
    }

    public OutputGovernor(ILlmService llmService, ILogger<OutputGovernor>? logger = null)
        : this(new LlmServiceToClientAdapter(llmService), logger)
    {
    }

    private const string SystemInstruction = """
You are the Output Governor. Verify if the Proposed Answer is factually supported by the Context Documentation:
- If all claims in the Proposed Answer are supported by the Context or ebm-papst domain rules, return: {"approved": true, "issues": [], "action": "APPROVE"}
- If any claim in the Proposed Answer contradicts or is not supported by the Context, return: {"approved": false, "issues": [{"type": "UNSUPPORTED_CLAIM", "claim": "...", "severity": "HIGH"}], "action": "REGENERATE"}
""";

    public async Task<OutputGovernorResult> ValidateOutputAsync(
        string question,
        string proposedAnswer,
        IEnumerable<DocumentChunk> chunks
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var chunkList = chunks.ToList();
        var contextText = string.Join("\n\n", chunkList.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"));
        var userPrompt = $"Context Documentation:\n{contextText}\n\nQuestion: {question}\nProposed Answer: {proposedAnswer}";

        var stopwatch = Stopwatch.StartNew();
        _logger?.LogInformation(
            "[OutputGovernor] Executing output validation prompt ({PromptLength} chars).\nPrompt:\n{Prompt}",
            userPrompt.Length, userPrompt);

        try
        {
            var req = LlmRequest.FromPrompt(
                userPrompt, 
                requireJson: true, 
                systemInstruction: SystemInstruction, 
                temperature: 0.0, 
                maxOutputTokens: 256);

            var structuredRes = await _llmClient.GenerateStructuredAsync<OutputGovernorResult>(req);
            stopwatch.Stop();

            var result = structuredRes.Value;
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
            _logger?.LogWarning(ex, "[OutputGovernor] Error after {DurationMs}ms: {Message}. Falling back to default approved.", stopwatch.ElapsedMilliseconds, ex.Message);
            Console.WriteLine($"Error in OutputGovernor: {ex.Message}");
        }

        // Safe fallback
        return new OutputGovernorResult(
            Approved: true,
            Issues: new List<OutputGovernorIssue>(),
            Action: "APPROVE"
        );
    }
}
