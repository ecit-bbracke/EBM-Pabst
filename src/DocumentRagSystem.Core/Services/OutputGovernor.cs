using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;

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
You are the Output Governor. Verify if the Proposed Answer is factually supported by the Context Documentation and approved ebm-papst domain rules:
- APPROVED ebm-papst DOMAIN RULES:
  * GreenTech EC Replacement Recommendations: Sections titled "Moderne EC-erstatning findes (GreenTech EC)" or recommending modern GreenTech EC equivalent series (e.g., A3G for A4E/A6E/A2E; R3G for R2E/R4E; K3G for older series) with typical 30-50% energy savings, stepless 0-10V/MODBUS speed control, and identical mechanical mounting dimensions are official ebm-papst engineering recommendations. These recommendations are fully APPROVED and must NEVER be flagged as unsupported claims or hallucinations.
  * Energy Savings & Payback Calculations: Standard benchmark calculations (typical 30-50% EC power reduction, e.g. 150 W for 250 W AC fan; ~2.500 DKK unit cost) are approved domain conventions.
  * Model Typenøgle Conventions: Fan family type, diameter (Ømm), AC/EC motor technology, and airflow direction (A/V) derived from model codes are approved domain rules.
  * Honest Missing Documentation Disclaimers: Clear statements explaining that a specific requested model is not found in the local catalog are approved and factually sound.

- RULES FOR VERIFICATION:
  * If the technical operational claims (temperatures, voltages, dimensions, airflows, speeds, power) for the documented models are supported by the Context Documentation or domain rules, return: {"approved": true, "issues": [], "action": "APPROVE"}
  * Only return {"approved": false, "issues": [...], "action": "REGENERATE"} if the answer invents specific operating numbers for a model that contradict or have no factual basis in the Context Documentation or approved domain rules.
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
                maxOutputTokens: 2048);

            var structuredRes = await _llmClient.GenerateStructuredAsync<OutputGovernorResult>(req);
            stopwatch.Stop();

            var result = structuredRes?.Value;
            if (result != null)
            {
                var issues = result.Issues ?? new List<OutputGovernorIssue>();
                _logger?.LogInformation(
                    "[OutputGovernor] Validation completed in {DurationMs}ms. Action: {Action}, Approved: {Approved}, Issues: {IssueCount}.",
                    stopwatch.ElapsedMilliseconds, result.Action, result.Approved, issues.Count);

                return result with { Issues = issues };
            }

            // Lenient recovery from raw content if deserialization returned null
            if (!string.IsNullOrWhiteSpace(structuredRes?.RawContent))
            {
                var recovered = TryRecoverResult(structuredRes.RawContent);
                if (recovered != null)
                {
                    _logger?.LogInformation(
                        "[OutputGovernor] Recovered validation result from raw content in {DurationMs}ms. Action: {Action}, Approved: {Approved}.",
                        stopwatch.ElapsedMilliseconds, recovered.Action, recovered.Approved);
                    return recovered;
                }

                _logger?.LogWarning(
                    "[OutputGovernor] Validation deserialization failed. Error: {Error}. Raw content: {RawContent}",
                    structuredRes.ErrorMessage, structuredRes.RawContent);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "[OutputGovernor] Error after {DurationMs}ms: {Message}. Output verification failed.", stopwatch.ElapsedMilliseconds, ex.Message);

            return new OutputGovernorResult(
                Approved: false,
                Issues: new List<OutputGovernorIssue>
                {
                    new OutputGovernorIssue(
                        Type: "VALIDATION_PROVIDER_ERROR",
                        Claim: $"Svarvalidering fejlede: {ex.Message}",
                        Severity: "HIGH"
                    )
                },
                Action: "FAIL_SAFE"
            );
        }

        // Safe fallback when result is unexpectedly null
        return new OutputGovernorResult(
            Approved: false,
            Issues: new List<OutputGovernorIssue>
            {
                new OutputGovernorIssue(
                    Type: "EMPTY_VALIDATION_RESULT",
                    Claim: "Valideringsudbyderen returnerede et tomt resultat.",
                    Severity: "HIGH"
                )
            },
            Action: "FAIL_SAFE"
        );
    }

    private static OutputGovernorResult? TryRecoverResult(string rawContent)
    {
        try
        {
            var cleaned = rawContent.Trim();
            var firstBrace = cleaned.IndexOf('{');
            var lastBrace = cleaned.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                var json = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool approved = false;
                if (root.TryGetProperty("approved", out var appProp))
                {
                    if (appProp.ValueKind == JsonValueKind.True) approved = true;
                    else if (appProp.ValueKind == JsonValueKind.False) approved = false;
                    else if (appProp.ValueKind == JsonValueKind.String) bool.TryParse(appProp.GetString(), out approved);
                }

                string action = approved ? "APPROVE" : "REGENERATE";
                if (root.TryGetProperty("action", out var actProp) && actProp.ValueKind == JsonValueKind.String)
                {
                    action = actProp.GetString() ?? action;
                }

                var issuesList = new List<OutputGovernorIssue>();
                if (root.TryGetProperty("issues", out var issuesProp) && issuesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in issuesProp.EnumerateArray())
                    {
                        var type = elem.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "ISSUE" : "ISSUE";
                        var claim = elem.TryGetProperty("claim", out var cProp) ? cProp.GetString() ?? "" : "";
                        var severity = elem.TryGetProperty("severity", out var sProp) ? sProp.GetString() ?? "HIGH" : "HIGH";
                        issuesList.Add(new OutputGovernorIssue(type, claim, severity));
                    }
                }

                return new OutputGovernorResult(approved, issuesList, action);
            }
        }
        catch
        {
            // Ignore recovery parse error and proceed to fail-safe
        }

        return null;
    }
}
