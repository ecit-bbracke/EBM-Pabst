using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public interface IAnswerComposer
{
    Task<string> ComposeAnswerAsync(
        string question,
        InputGovernorResult governorResult,
        List<EvidenceClaim> evidenceClaims,
        IEnumerable<DocumentChunk> chunks,
        string? draftResponse = null,
        object? workflowData = null
    );

    async IAsyncEnumerable<string> StreamAnswerAsync(
        string question,
        InputGovernorResult governorResult,
        List<EvidenceClaim> evidenceClaims,
        IEnumerable<DocumentChunk> chunks,
        string? draftResponse = null,
        object? workflowData = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await ComposeAnswerAsync(question, governorResult, evidenceClaims, chunks, draftResponse, workflowData);
        yield return result;
    }
}

public class AnswerComposer : IAnswerComposer
{
    private readonly ILlmService _llmService;
    private readonly ILogger<AnswerComposer>? _logger;

    public AnswerComposer(ILlmService llmService, ILogger<AnswerComposer>? logger = null)
    {
        _llmService = llmService;
        _logger = logger;
    }

    public async Task<string> ComposeAnswerAsync(
        string question,
        InputGovernorResult governorResult,
        List<EvidenceClaim> evidenceClaims,
        IEnumerable<DocumentChunk> chunks,
        string? draftResponse = null,
        object? workflowData = null
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var prompt = BuildPrompt(question, governorResult, evidenceClaims, chunks, draftResponse, workflowData);
        var stopwatch = Stopwatch.StartNew();

        _logger?.LogInformation(
            "[AnswerComposer] Executing answer composition prompt ({PromptLength} chars) for intent {Intent}.\nPrompt:\n{Prompt}",
            prompt.Length, governorResult.Intent, prompt);

        var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: false);
        stopwatch.Stop();

        _logger?.LogInformation(
            "[AnswerComposer] Answer composed in {DurationMs}ms (Length: {AnswerLength} chars).",
            stopwatch.ElapsedMilliseconds, response.Length);

        return response;
    }

    public IAsyncEnumerable<string> StreamAnswerAsync(
        string question,
        InputGovernorResult governorResult,
        List<EvidenceClaim> evidenceClaims,
        IEnumerable<DocumentChunk> chunks,
        string? draftResponse = null,
        object? workflowData = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var prompt = BuildPrompt(question, governorResult, evidenceClaims, chunks, draftResponse, workflowData);
        _logger?.LogInformation(
            "[AnswerComposer] Starting streaming answer composition prompt ({PromptLength} chars) for intent {Intent}.\nPrompt:\n{Prompt}",
            prompt.Length, governorResult.Intent, prompt);

        return _llmService.StreamCompletionAsync(prompt, cancellationToken);
    }

    private string BuildPrompt(
        string question,
        InputGovernorResult governorResult,
        List<EvidenceClaim> evidenceClaims,
        IEnumerable<DocumentChunk> chunks,
        string? draftResponse,
        object? workflowData
    )
    {
        var evidenceSection = (evidenceClaims != null && evidenceClaims.Count > 0)
            ? string.Join("\n", evidenceClaims.Select(e => $"- [{e.Status}] {e.Claim} (Reason: {e.Reason})"))
            : "No specific evidence claims evaluated.";

        string workflowSection;
        if (!string.IsNullOrWhiteSpace(draftResponse))
        {
            workflowSection = draftResponse.Trim();
        }
        else if (workflowData != null)
        {
            workflowSection = JsonSerializer.Serialize(workflowData, new JsonSerializerOptions { WriteIndented = false });
        }
        else
        {
            workflowSection = "No specific workflow data provided.";
        }

        var chunkList = chunks?.ToList() ?? new List<DocumentChunk>();
        var chunksText = chunkList.Count > 0
            ? string.Join("\n\n", chunkList.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text.Trim()}"))
            : "No specific document context chunks retrieved.";

        var domainRulesSection = BuildDomainRules(question, governorResult, workflowData);

        return $$"""
            You are the Answer Composer for a highly specialized Technical RAG Orchestrator for industrial ventilation and motor systems (ebm-papst domain).
            Your job is to compose a comprehensive, grounded, actionable, and highly auditable final technical answer.

            User Question:
            {{question}}

            Input Governor Intent: {{governorResult.Intent}}

            Specialized Workflow & Domain Analysis:
            {{workflowSection}}

            Evidence Claims:
            {{evidenceSection}}

            Context chunks:
            {{chunksText}}

            {{domainRulesSection}}

            General Instructions:
            - CONCISENESS & STRUCTURE: Be direct, concise, and structured. Use short bullet points and clear headings.
            - Do NOT include conversational filler, preamble (e.g. "Sure, I can help with that"), or redundant concluding remarks.
            - Explicitly state and cite documented specifications.
            - Clearly mark derived conclusions as derived, showing the logical steps (e.g. "Since operating voltage is 18-30 VDC, 24 VDC is compatible").
            - Highlight any conflicting information from different sources; do not choose one silently.
            - CRITICAL LANGUAGE REQUIREMENT: You MUST ALWAYS write the ENTIRE response in the EXACT same language as the user's question (e.g. if the user asks in English, write the answer entirely in English; if the user asks in Danish, write the answer in Danish; if in German, write in German). NEVER reply in a different language than the question.
            - Keep simple questions simple. For complex questions, make the reasoning auditable.
            """;
    }

    private static string BuildDomainRules(
        string question,
        InputGovernorResult governorResult,
        object? workflowData
    )
    {
        if (governorResult.Intent == "OVERVIEW" || workflowData is CatalogOverviewResult)
        {
            var overviewSb = new StringBuilder();
            overviewSb.AppendLine("Domain Rules & Guidance:");
            overviewSb.AppendLine("- **Overview & Catalog Queries**: Present models clearly grouped by category with decoded specifications.");
            overviewSb.AppendLine("- **Typenøgle conventions**: 1st char = Fan type (A, S, W, R, K, G, D); 2-3 = Motor (3G/EC or AC); 4-6 = Diameter Ømm; 12th = Axial airflow (even = A, odd = V).");
            return overviewSb.ToString().Trim();
        }

        var ebmProducts = governorResult.ParsedEbmProducts ?? new List<EbmProductInfo>();
        
        bool hasAxial = ebmProducts.Any(p => p.FanFamily == FanFamilyType.Axial) ||
                        question.Contains("axial", StringComparison.OrdinalIgnoreCase) ||
                        question.Contains("aksial", StringComparison.OrdinalIgnoreCase) ||
                        question.Contains("A6E", StringComparison.OrdinalIgnoreCase) ||
                        question.Contains("A3G", StringComparison.OrdinalIgnoreCase) ||
                        question.Contains("S3G", StringComparison.OrdinalIgnoreCase) ||
                        question.Contains("W3G", StringComparison.OrdinalIgnoreCase) ||
                        question.Contains("S4E", StringComparison.OrdinalIgnoreCase) ||
                        question.Contains("W4E", StringComparison.OrdinalIgnoreCase);

        bool hasCentrifugal = ebmProducts.Any(p => p.FanFamily == FanFamilyType.Centrifugal) ||
                             question.Contains("centrifugal", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("radipac", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("K3G", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("R3G", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("G3G", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("D3G", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("8300", StringComparison.OrdinalIgnoreCase);

        bool hasCompact = ebmProducts.Any(p => p.FanFamily == FanFamilyType.CompactOrSpecial) ||
                          question.Contains("kompakt", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("compact", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("4114", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("6314", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("3258", StringComparison.OrdinalIgnoreCase);

        bool isReplacement = governorResult.Intent == "COMPATIBILITY" ||
                             question.Contains("erstat", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("substitut", StringComparison.OrdinalIgnoreCase) ||
                             (workflowData is CompatibilityResult c && c.ReplacementAnalysis != null);

        bool isGeneral = !hasAxial && !hasCentrifugal && !hasCompact;

        var sb = new StringBuilder();
        sb.AppendLine("Domain Rules & Guidance:");

        if (hasAxial || isGeneral)
        {
            sb.AppendLine("- **Airflow Direction on Axial Fans (A, S, W)**:");
            sb.AppendLine("  - 12th char in 12-char product code: Even digit (0, 2, 4, 6, 8) = Direction 'A'; Odd digit (1, 3, 5, 7, 9) = Direction 'V'.");
            sb.AppendLine("  - Example: A6E450AP0201 (V) and A6E450AP0202 (A) have reversed airflow and are 100% INCOMPATIBLE as drop-in replacements.");
            sb.AppendLine("- **Axial Fan Variants**: 'A' = base axial fan; 'S' = with guard grille; 'W' = in wall ring.");
        }

        if (hasCentrifugal || isGeneral)
        {
            sb.AppendLine("- **Centrifugal Fan Types**: 'R' = motorized single-inlet impeller, 'K' = RadiPac in frame/bracket, 'G' = single scroll, 'D' = dual scroll.");
            sb.AppendLine("- '8300...' series replaces newer 'K3G...'.");
        }

        if (hasCompact || isGeneral)
        {
            sb.AppendLine("- **Compact Fans**: 10-digit codes (96/94/92/97...) or series numbers (4114N, 6314H, 3258J, RLF100).");
        }

        sb.AppendLine("- **Motor Technology**: '3G' = EC-motor (electronically commutated); AC motor codes = poles + phase ('E' = 1~ 230V, 'D' = 3~ 400V).");

        if (isReplacement)
        {
            sb.AppendLine("- **Replacement / Substitution Queries**:");
            sb.AppendLine("  1. State original model's decoded specifications (Type, EC/AC, Diameter, Airflow direction).");
            sb.AppendLine("  2. If no direct local database match exists, state this clearly.");
            sb.AppendLine("  3. Proactively provide concrete ebm-papst series options (e.g. S-series with guard grille, W-series in wall ring) from the Specialized Workflow Analysis.");
            sb.AppendLine("  4. Highlight critical compatibility requirements (diameter, airflow direction A/V, motor technology).");
            sb.AppendLine("  5. Do NOT discuss unrelated models unless directly asked.");
        }

        return sb.ToString().Trim();
    }
}
