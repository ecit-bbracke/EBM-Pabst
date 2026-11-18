using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;

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
    private readonly ILlmClient _llmClient;
    private readonly ILogger<AnswerComposer>? _logger;

    public AnswerComposer(ILlmClient llmClient, ILogger<AnswerComposer>? logger = null)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _logger = logger;
    }

    public AnswerComposer(ILlmClientResolver resolver, ILogger<AnswerComposer>? logger = null)
        : this(resolver?.Resolve(LlmPurpose.AnswerComposer) ?? throw new ArgumentNullException(nameof(resolver)), logger)
    {
    }

    public AnswerComposer(ILlmService llmService, ILogger<AnswerComposer>? logger = null)
        : this(new LlmServiceToClientAdapter(llmService), logger)
    {
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

        var request = LlmRequest.FromPrompt(prompt, requireJson: false);
        var result = await _llmClient.GenerateTextAsync(request);
        stopwatch.Stop();

        _logger?.LogInformation(
            "[AnswerComposer] Answer composed in {DurationMs}ms (Length: {AnswerLength} chars).",
            stopwatch.ElapsedMilliseconds, result.Content.Length);

        return result.Content;
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

        var request = LlmRequest.FromPrompt(prompt, requireJson: false);
        return _llmClient.StreamTextAsync(request, cancellationToken);
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
            - CRITICAL LANGUAGE REQUIREMENT: You MUST ALWAYS write the ENTIRE response in the EXACT same language as the user's question (e.g. if the user asks in English, write the answer entirely in English; if the user asks in Danish, write the answer in Danish; if in German, write in German). If the nature of the question does not allow the language to be detected (e.g. it consists only of product codes, model numbers, part identifiers, specifications, numbers, or technical parameters like "K3G560PC0401" or "230V"), you MUST write the response in the same language as the application page itself: Danish (Dansk). NEVER reply in English when the user question is language-ambiguous or a model code.
            - NO LATEX / CLEAN TYPOGRAPHY: NEVER use LaTeX math notation or syntax (e.g. do NOT write $\pm 3%$, $I_max$, $\approx$, etc.). Always use clean plain text, standard Markdown, and standard Unicode symbols (e.g. write ± 3%, I_max = 1,1 mA, ≈, Δp, m³/h, µF, °C, Ø).
            - BRANDING / CASING REQUIREMENT: The company and brand name "ebm-papst" MUST ALWAYS be spelled with this exact casing ("ebm-papst", all lowercase). NEVER write "EBM-Papst", "Ebm-papst", "EBM-PAPST", or "ebmpapst".
            - FACTUAL GROUNDING & MISSING MODELS:
              * You must NEVER invent, fabricate, or assume technical specifications (such as airflow m³/h, pressure Pa, power W, voltage V, current A, speed rpm, sound dB, weight kg) that are not present in the Context chunks.
              * If the user asks for a comparison of multiple models or asks about models where one or more are not found in the Context chunks, explicitly state in the response language which model(s) are documented and which model(s) lack datasheets in the catalog.
              * You may explain what can be decoded from the ebm-papst product code key (e.g. fan type, motor technology, diameter), but clearly clarify that operating specifications for the missing model are not documented in the catalog.
              * NO HALLUCINATED MODEL VARIANTS: NEVER fabricate fictitious model names by swapping letters in the query model code. Centrifugal RadiPac K3G models are replaced by the new generation 8300-series RadiPac modules with matching diameter and voltage form, NOT by made-up model variants.
              * DISTINGUISH MODEL CODES FROM OPERATIONAL MEASUREMENTS: When the user queries a model name or product identifier (e.g. "modelnavn = 4420", "model 4420", or any 4-digit compact code like 4420, 4414, 4314), you must NEVER confuse the model code with a numeric measurement or operating point (such as speed 4420 rpm/min⁻¹, pressure Pa, or power W) found in tables of completely different models (such as 8300101432). If the specific requested model (e.g. 4420) is not found as a documented product in the Context chunks, explicitly state in the response language that model 4420 is not documented in the catalog, explain what the code represents (e.g. ebm-papst 4400-serien af 119x119 mm DC-kompaktblæsere), and NEVER cite an unrelated model (like 8300101432) as if it were the requested model.
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
                          question.Contains("computer", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("kabinet", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("118", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("119", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("120", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("121", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("4114", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("4400", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("4300", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("6314", StringComparison.OrdinalIgnoreCase) ||
                          question.Contains("3258", StringComparison.OrdinalIgnoreCase);

        bool isReplacement = governorResult.Intent == "COMPATIBILITY" ||
                             question.Contains("erstat", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
                             question.Contains("substitut", StringComparison.OrdinalIgnoreCase) ||
                             (workflowData is CompatibilityResult c && c.ReplacementAnalysis != null);

        bool isEnergyCalculation = question.Contains("energibesparelse", StringComparison.OrdinalIgnoreCase) ||
                                  question.Contains("tilbagebetaling", StringComparison.OrdinalIgnoreCase) ||
                                  question.Contains("payback", StringComparison.OrdinalIgnoreCase) ||
                                  question.Contains("kwh", StringComparison.OrdinalIgnoreCase);

        bool isGeneral = !hasAxial && !hasCentrifugal && !hasCompact;

        var sb = new StringBuilder();
        sb.AppendLine("Domain Rules & Guidance:");

        if (isEnergyCalculation)
        {
            sb.AppendLine("- **Energy Savings & Payback Calculations (ebm-papst GreenTech EC Benchmark)**:");
            sb.AppendLine("  - When calculating energy savings from upgrading older AC fans to modern GreenTech EC fans:");
            sb.AppendLine("  - If exact EC power (W) or investment price is not specified in the question, proactively apply standard ebm-papst benchmark estimates (typical 40% EC power reduction, e.g. 150 W for a 250 W AC fan; typical industrial investment of ~2.500 DKK per fan) to compute and deliver an immediate, concrete, actionable business case.");
            sb.AppendLine("  - Provide the clear step-by-step calculation: Annual kWh saved = ((P_AC - P_EC) * N * hours) / 1000; Annual DKK saved = kWh * price; Payback years = Total cost / Annual DKK saved.");
            sb.AppendLine("  - State the benchmark assumptions clearly and explain how the customer can adjust the result with exact project quotations.");
        }

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
            sb.AppendLine("- **Compact Fans & Colloquial / Non-Standard Dimensions (e.g. 120 mm, 121 mm, 118 mm, computer/cabinet fans)**:");
            sb.AppendLine("  - ebm-papst does not produce nominal '120 mm' or '121 mm' compact fans. The official global industrial standard size is **119 x 119 mm** (standard mounting hole distance 104,8 mm, often referred to colloquially by customers as 120 mm computer or cabinet fans).");
            sb.AppendLine("  - When users ask about 120 mm, 121 mm, 118 mm, or computer/cabinet fans:");
            sb.AppendLine("    * Politely and clearly inform the user that 121 mm (or 120 mm) corresponds to ebm-papst' standard **119 x 119 mm** series (e.g. ACi 4400, 4400, 4300, 4100 series).");
            sb.AppendLine("    * If matching 119 mm compact fan models are present in the Context chunks, present them with their specifications (e.g. air flow, voltage, noise, power).");
            sb.AppendLine("    * If the Context chunks only contain large industrial fans (e.g. 300 mm+ W3G/A3G) and lack compact fan datasheets, clearly explain that while ebm-papst standard models for 120/121 mm applications are the 119x119 mm series (such as ACi 4400 / 4400 / 4300 with typical airflow ~170–180 m³/h), specific datasheets for these compact models are not documented in the retrieved catalog context.");
            sb.AppendLine("- **Compact Fans**: 10-digit codes (96/94/92/97...) or series numbers (4114N, 4414F, 4314N, 6314H, 3258J, RLF100).");
        }

        sb.AppendLine("- **Motor Technology**: '3G' = EC-motor (electronically commutated); AC motor codes = poles + phase ('E' = 1~ 230V, 'D' = 3~ 400V).");
        sb.AppendLine("- **Engineering Units & Operating Parameters (CFM, l/s, mmVS, Pa, IP-ratings)**:");
        sb.AppendLine("  - When user questions specify airflow in CFM or l/s, always provide the metric m³/h equivalent alongside for clarity (1 CFM ≈ 1,7 m³/h; 1 l/s = 3,6 m³/h; e.g. 100 CFM ≈ 170 m³/h).");
        sb.AppendLine("  - When user questions specify pressure in mmVS, mm H2O, or in. wg, show the Pascal (Pa) equivalent (1 mmVS ≈ 9,81 Pa; 1 in. wg ≈ 249 Pa).");
        sb.AppendLine("  - When discussing harsh, wet, or outdoor environments, explicitly mention ingress protection ratings (IP20 for standard indoor/IT racks, IP54/IP55 for industrial dust/splash protection, IP68 for fully potted/washdown/outdoor).");
        sb.AppendLine("- **Danish Technical Jargon & Mounting Conventions**:");
        sb.AppendLine("  - **'Sugende blæser'** corresponds to **Airflow direction 'V'** (intake over struts/guard, odd suffix digit e.g. ..01 / ..03).");
        sb.AppendLine("  - **'Trykkende / Blæsende blæser'** corresponds to **Airflow direction 'A'** (discharge over struts/guard, even suffix digit e.g. ..02 / ..04).");
        sb.AppendLine("  - **'Vægramme / Vægring'** corresponds to ebm-papst **W-series** axial fans with square wall mounting plate (e.g. W3G, W4E).");
        sb.AppendLine("  - **'Gitter / Kurvegitter'** corresponds to ebm-papst **S-series** axial fans mounted in wire guard grille (e.g. S3G, S4E).");
        sb.AppendLine("  - **'Pillefyr / Røggasblæser'** corresponds to ebm-papst flue gas blowers (e.g. R2E120, R2E150, G2E).");
        sb.AppendLine("  - **'Tavlekøling / Tavleblæser'** corresponds to control cabinet filter fans and compact axial fans.");
        sb.AppendLine("  - **'Kraftstrøm'** corresponds to 3-phase 400 VAC (3~ 400V). **'Lysnet'** corresponds to single-phase 230 VAC (1~ 230V).");

        if (isReplacement)
        {
            sb.AppendLine("- **Replacement / Substitution Queries**:");
            sb.AppendLine("  1. State original model's decoded and documented specifications (Type, EC/AC, Diameter, Airflow direction, Voltage, Power W, Speed rpm, Airflow m³/h, Pressure Pa).");
            sb.AppendLine("  2. DIRECT DROP-IN REPLACEMENT FIRST & PROMINENT: ALWAYS feature the direct drop-in replacement (matching diameter, voltage, and nearest power) FIRST and PROMINENTLY. Present its concrete technical specifications (nominal voltage, power consumption W, speed rpm, airflow m³/h, pressure Pa) and explicitly cite the documented datasheet.");
            sb.AppendLine("  3. DO NOT ASK USER TO READ NAMEPLATE WHEN DATA IS IN DATASHEETS: Do NOT tell the user to read the nameplate on the fan for voltage, power, or RPM when the technical specifications are already documented in the datasheets in Context chunks. Present the documented values directly!");
            sb.AppendLine("  4. DO NOT DISCUSS UNREQUESTED INCOMPATIBLE MODELS: If a model has lower airflow/pressure or different aerodynamic geometry and is not a direct replacement, do NOT dedicate sections or comparison tables to it unless the user explicitly asked about that specific model. Focus your answer on the direct drop-in replacement from the database or catalog.");
            sb.AppendLine("  5. NO OVERSHADOWING BY PARTIAL REPLACEMENTS: NEVER let a 'delvis erstatning' (such as replacing only the motorized impeller without the frame) dominate the response or become the main topic. A partial replacement / rebuild must ONLY be mentioned as a brief secondary note at the very end, and you must clearly explain that it requires reusing the old frame.");
            sb.AppendLine("  6. If a direct local database match or documented candidate exists, emphasize it as the primary solution and deliver its specifications from the datasheet.");
            sb.AppendLine("  6. Electrical Supply Matching: Replacements must use the same supply form (1~ 230 VAC vs 3~ 400 VAC vs DC 12/24/48V). Never present a 400 VAC 3-phase fan as a direct drop-in for a 230 VAC fan without explicitly pointing out the electrical incompatibility.");
            sb.AppendLine("  7. Power Consumption (W) Proximity: Prioritize replacements whose nominal power consumption (W) is as close to the original model as possible (e.g. for a 750W model, an 800W fan is a vastly superior replacement compared to an over-dimensioned 1300W fan).");
            sb.AppendLine("  8. Dimensional Matching: Diameter and mounting footprint must match as closely as possible.");
            sb.AppendLine("  9. Alternative Options with Full Disclosure: You may suggest alternative replacement models if an optimal drop-in is unavailable, BUT you MUST explicitly state and explain all differences (e.g. supply voltage/phase differences, wattage differences, dimensions).");
            sb.AppendLine("  10. Proactively provide concrete ebm-papst series options (e.g. S-series with guard grille, W-series in wall ring, 8300-series RadiPac) from the Specialized Workflow Analysis.");
            sb.AppendLine("  11. Highlight critical compatibility requirements (diameter, airflow direction A/V, motor technology, voltage/phase).");
            sb.AppendLine("  12. Do NOT discuss unrelated models unless directly asked.");
        }

        var ecReplacements = EcReplacementAdvisor.FindEcReplacements(ebmProducts);
        if (ecReplacements.Count > 0)
        {
            sb.AppendLine("- **AUTOMATED GREENTECH EC REPLACEMENT RECOMMENDATION FOR AC MODELS**:");
            sb.AppendLine("  - One or more older/legacy AC models were identified in the query.");
            sb.AppendLine("  - You MUST append a dedicated section at the end of your response titled: `### 💡 Moderne EC-erstatning findes (GreenTech EC)`.");
            sb.AppendLine("  - Explicitly recommend the corresponding modern GreenTech EC equivalent series, explain the typical 30-50% energy savings, stepless 0-10V control, and identical mechanical mounting dimensions.");
            foreach (var rec in ecReplacements)
            {
                sb.AppendLine($"    * For {rec.LegacyAcModel}: Anbefal serie {rec.RecommendedEcSeries} ({rec.RecommendedEcModelPrefix}...), forventet besparelse: {rec.EnergySavingsEstimate}.");
            }
        }

        return sb.ToString().Trim();
    }
}
