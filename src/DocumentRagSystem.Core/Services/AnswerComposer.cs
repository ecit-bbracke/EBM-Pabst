using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public interface IAnswerComposer
{
    Task<string> ComposeAnswerAsync(
        string question,
        InputGovernorResult governorResult,
        List<EvidenceClaim> evidenceClaims,
        IEnumerable<DocumentChunk> chunks
    );
}

public class AnswerComposer : IAnswerComposer
{
    private readonly ILlmService _llmService;

    public AnswerComposer(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<string> ComposeAnswerAsync(
        string question,
        InputGovernorResult governorResult,
        List<EvidenceClaim> evidenceClaims,
        IEnumerable<DocumentChunk> chunks
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var serializedEvidence = JsonSerializer.Serialize(evidenceClaims, new JsonSerializerOptions { WriteIndented = true });

        var prompt = $$"""
            You are the Answer Composer for a highly specialized Technical RAG Orchestrator for industrial ventilation and motor systems (ebm-papst domain).
            Your job is to compose a comprehensive, grounded, and highly auditable final technical answer.

            You are given:
            1. Original user question.
            2. Input Governor analysis (intent: {{governorResult.Intent}}).
            3. Evidence claims analysis (identifying which claims are SUPPORTED, DERIVED, CONFLICTING, or MISSING).
            4. Context chunks (with citations).

            Domain Rules & Guidance:
            - **Airflow Direction on Axial Fans (A, S, W)**:
              - The 12th character in a standard 12-character product number indicates airflow direction:
                - Even digit (0, 2, 4, 6, 8) = Airflow direction 'A'.
                - Odd digit (1, 3, 5, 7, 9) = Airflow direction 'V'.
              - Example: 'A6E450AP0201' and 'A6E450AP0202' are physically identical except for reversed airflow direction. They are 100% INCOMPATIBLE as drop-in replacements for one another. Always highlight this difference clearly to the user.
            - **Axial Fan Accessories**:
              - 'A' is the base axial fan without accessories.
              - 'S' is an 'A' model with a guard grille (beskyttelsesgitter).
              - 'W' is an 'A' model with a wall ring (vægring).
            - **Centrifugal Fan Types**:
              - 'R' = Motorized single-inlet impeller.
              - 'K' = 'R' version in mounting frame/bracket (RadiPac).
              - 'G' = Single-inlet in scroll housing (sneglehus).
              - 'D' = Dual-inlet in scroll housing.
            - **Motor Technology**:
              - '3G' = EC-motor (electronically commutated, new generation).
              - AC motor codes: Pole number (2, 4, 6, 8, 12) + Phase ('E' = 1-phase / 230V, 'D' = 3-phase / 400V).
            - **Other Series**:
              - '8300...' series replaces newer 'K3G...'.
              - Compact fans / small blowers use 10-digit codes (e.g. 96/94/92/97...) or series numbers (4114N, 6314H, 3258J, RLF100).

            General Instructions:
            - Explicitly state and cite documented specifications.
            - Clearly mark derived conclusions as derived, showing the logical steps (e.g., "Siden driftsspændingen er 18-30 VDC, kan det udledes at 24 VDC er kompatibel").
            - Highlight any conflicting information from different sources; do not choose one silently.
            - Explicitly list any missing information as "Missing/Unknown" rather than guessing or assuming.
            - Match the language of the user question (primarily Danish or English). If the query is in Danish, write the answer in Danish.
            - Keep simple questions simple. For complex questions, make the reasoning auditable.

            User Question:
            {{question}}

            Evidence Claims:
            {{serializedEvidence}}

            Context chunks:
            {{string.Join("\n\n", chunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}
            """;

        return await _llmService.GenerateCompletionAsync(prompt, requireJson: false);
    }
}
