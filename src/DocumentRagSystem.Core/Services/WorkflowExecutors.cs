using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

internal static class CatalogCacheHelper
{
    private static readonly ConcurrentDictionary<string, (DateTime CachedAt, Dictionary<string, Document> Docs, List<EbmProductInfo> Products)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public static async Task<(Dictionary<string, Document> AllDocs, List<EbmProductInfo> CatalogProducts)> GetCatalogDocumentsAndProductsAsync(
        IVectorStore vectorStore,
        IDocumentRepository? documentRepository,
        IEbmProductCodeParser ebmParser
    )
    {
        var cacheKey = $"catalog_{documentRepository?.GetHashCode()}_{vectorStore.GetHashCode()}";
        if (_cache.TryGetValue(cacheKey, out var entry) && (DateTime.UtcNow - entry.CachedAt) < CacheTtl)
        {
            return (entry.Docs, entry.Products);
        }

        var allDocsDict = new Dictionary<string, Document>();

        if (documentRepository != null)
        {
            try
            {
                var repoDocs = await documentRepository.GetAllDocumentsAsync();
                foreach (var doc in repoDocs)
                {
                    allDocsDict[doc.Id] = doc;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error querying document repository: {ex.Message}");
            }
        }

        try
        {
            var vectorDocs = await vectorStore.GetDocumentsAsync(1000);
            foreach (var doc in vectorDocs)
            {
                if (!allDocsDict.ContainsKey(doc.Id))
                {
                    allDocsDict[doc.Id] = doc;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving vector store docs: {ex.Message}");
        }

        var catalogProducts = new List<EbmProductInfo>();
        foreach (var doc in allDocsDict.Values)
        {
            var docName = !string.IsNullOrWhiteSpace(doc.FileName) ? doc.FileName : doc.Id;
            var extracted = ebmParser.ExtractProductsFromText(docName);
            catalogProducts.AddRange(extracted);
        }

        _cache[cacheKey] = (DateTime.UtcNow, allDocsDict, catalogProducts);
        return (allDocsDict, catalogProducts);
    }
}

public interface IWorkflowExecutor
{
    Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    );
}

public class SimpleWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public SimpleWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var chunks = (initialSearchTask != null ? await initialSearchTask : await vectorStore.SearchAsync(question)).ToList();
        var answer = await _llmService.GenerateResponseAsync(question, chunks);
        return (answer, chunks, null);
    }
}

public class ComparisonWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;
    private readonly IEbmProductCodeParser _ebmParser;

    public ComparisonWorkflowExecutor(ILlmService llmService, IEbmProductCodeParser? ebmParser = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var allChunks = new List<DocumentChunk>();
        var entities = governorResult.Entities ?? new List<EntityInfo>();

        var ebmProducts = (governorResult.ParsedEbmProducts != null && governorResult.ParsedEbmProducts.Count > 0)
            ? governorResult.ParsedEbmProducts
            : _ebmParser.ExtractProductsFromText(question);

        EbmComparisonEvaluation? ebmEval = null;
        if (ebmProducts.Count >= 2)
        {
            ebmEval = _ebmParser.Compare(ebmProducts[0], ebmProducts[1]);
        }

        if (entities.Count > 0)
        {
            var searchTasks = entities.Select(entity =>
            {
                var attrs = governorResult.RequestedAttributes != null ? string.Join(" ", governorResult.RequestedAttributes) : "";
                var query = $"{entity.Name} specifications {attrs}".Trim();
                return vectorStore.SearchAsync(query);
            }).ToList();

            var searchResults = await Task.WhenAll(searchTasks);
            foreach (var entityChunks in searchResults)
            {
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = initialSearchTask != null
                ? await initialSearchTask
                : await vectorStore.SearchAsync(question);
            allChunks.AddRange(fallbackChunks);
        }

        // Deduplicate chunks
        allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

        var ebmPromptContext = ebmEval != null
            ? $"\n\nDeterministic ebm-papst Domain Evaluation:\nSummary: {ebmEval.Summary}\nCritical Blockers:\n{string.Join("\n", ebmEval.CriticalBlockers.Select(b => $"- {b}"))}\nDifferences:\n{string.Join("\n", ebmEval.Differences.Select(d => $"- {d.Dimension}: {d.ValueA} vs {d.ValueB} (Critical: {d.IsCriticalIncompatibility}) - {d.Explanation}"))}"
            : "";

        var prompt = $$"""
            You are the Comparison RAG workflow executor for ventilation and motor systems (ebm-papst domain).
            Based on the retrieved technical document chunks and domain knowledge, compare the entities mentioned in the query.
            Only compare documented or deterministically decoded attributes. Do not invent missing specifications.
            If an attribute is unknown, set its value to "UNKNOWN".

            Pay special attention to:
            1. Airflow direction (A vs V on axial fans based on even/odd 12th digit in product code).
            2. Fan type & accessories (A = base, S = guard grille, W = wall ring; R = motorized impeller, K = RadiPac/bracket, G = single-inlet scroll, D = dual-inlet scroll).
            3. Motor technology (3G = EC; poles & 1-phase E / 3-phase D for AC).
            4. Impeller diameter (mm).
            {{ebmPromptContext}}

            Return a structured JSON object strictly matching this schema:
            {
              "entities": {
                "EntityNameA": {
                  "name": "EntityNameA",
                  "attributes": {
                    "attribute_name": {
                      "value": "attribute value",
                      "source": "source document or section"
                    }
                  }
                }
              }
            }

            Context chunks:
            {{string.Join("\n\n", allChunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Query: {{question}}
            """;

        var jsonResponse = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
        ComparisonResult? comparisonResult = null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            comparisonResult = JsonSerializer.Deserialize<ComparisonResult>(jsonResponse, options);
            if (comparisonResult != null)
            {
                comparisonResult = comparisonResult with { EbmEvaluation = ebmEval };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Comparison RAG output: {ex.Message}");
            if (ebmEval != null)
            {
                comparisonResult = new ComparisonResult(new Dictionary<string, EntityComparisonData>(), ebmEval);
            }
        }

        var textResponse = "";

        if (ebmEval != null)
        {
            if (ebmEval.CriticalBlockers.Count > 0)
            {
                textResponse += "### ⚠️ Kritisk ebm-papst Typenøgle & Kompatibilitetsadvarsel\n\n";
                foreach (var blocker in ebmEval.CriticalBlockers)
                {
                    textResponse += $"- ❌ **{blocker}**\n";
                }
                textResponse += "\n";
            }

            textResponse += "### Typenøgle-afkodning\n";
            textResponse += $"* **{ebmEval.ProductA.RawCode}**: {ebmEval.ProductA.FanTypeDescription}, {ebmEval.ProductA.MotorDescription}, Ø{ebmEval.ProductA.ImpellerDiameterMm}mm, {ebmEval.ProductA.AirflowDescription}\n";
            textResponse += $"* **{ebmEval.ProductB.RawCode}**: {ebmEval.ProductB.FanTypeDescription}, {ebmEval.ProductB.MotorDescription}, Ø{ebmEval.ProductB.ImpellerDiameterMm}mm, {ebmEval.ProductB.AirflowDescription}\n\n";
        }

        textResponse += "### Comparison Summary\n\n";
        if (comparisonResult?.Entities != null && comparisonResult.Entities.Count > 0)
        {
            foreach (var kvp in comparisonResult.Entities)
            {
                textResponse += $"**{kvp.Key}**:\n";
                foreach (var attr in kvp.Value.Attributes)
                {
                    textResponse += $"- {attr.Key}: {attr.Value.Value} (Source: {attr.Value.Source})\n";
                }
                textResponse += "\n";
            }
        }
        else
        {
            textResponse += jsonResponse;
        }

        return (textResponse, allChunks, comparisonResult);
    }
}

public class CompatibilityWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly IDocumentRepository? _documentRepository;

    public CompatibilityWorkflowExecutor(
        ILlmService llmService,
        IEbmProductCodeParser? ebmParser = null,
        IDocumentRepository? documentRepository = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
        _documentRepository = documentRepository;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var allChunks = new List<DocumentChunk>();
        var entities = governorResult.Entities ?? new List<EntityInfo>();

        var ebmProducts = (governorResult.ParsedEbmProducts != null && governorResult.ParsedEbmProducts.Count > 0)
            ? governorResult.ParsedEbmProducts
            : _ebmParser.ExtractProductsFromText(question);

        EbmComparisonEvaluation? ebmEval = null;
        EbmReplacementAnalysis? replacementAnalysis = null;

        // CASE 1: Open replacement / single product query
        if (ebmProducts.Count == 1)
        {
            var sourceProduct = ebmProducts[0];
            var (allDocsDict, catalogProducts) = await CatalogCacheHelper.GetCatalogDocumentsAndProductsAsync(vectorStore, _documentRepository, _ebmParser);

            replacementAnalysis = _ebmParser.AnalyzeReplacements(sourceProduct, catalogProducts);

            // Fetch source chunks and candidate chunks in parallel
            var searchTasks = new List<Task<IEnumerable<DocumentChunk>>>
            {
                vectorStore.SearchAsync($"{sourceProduct.RawCode} {sourceProduct.CleanCode} specifications")
            };

            foreach (var match in replacementAnalysis.MatchedDatabaseCandidates)
            {
                searchTasks.Add(vectorStore.SearchAsync($"{match.RawCode} {match.CleanCode} specifications"));
            }

            var searchResults = await Task.WhenAll(searchTasks);
            foreach (var res in searchResults)
            {
                allChunks.AddRange(res);
            }

            allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

            var checks = new List<CompatibilityCheck>
            {
                new CompatibilityCheck(
                    Dimension: "Impeller Diameter",
                    SourceValue: $"Ø{sourceProduct.ImpellerDiameterMm} mm",
                    TargetRequirement: $"Ø{sourceProduct.ImpellerDiameterMm} mm (match installation cutout)",
                    Status: "COMPATIBLE",
                    Reason: $"Samme impellerdiameter er påkrævet for mekanisk montering."
                ),
                new CompatibilityCheck(
                    Dimension: "Airflow Direction",
                    SourceValue: sourceProduct.AirflowDescription,
                    TargetRequirement: sourceProduct.AirflowDescription,
                    Status: "COMPATIBLE",
                    Reason: sourceProduct.FanFamily == FanFamilyType.Axial
                        ? $"12. ciffer skal have {(sourceProduct.AirflowDirection == AirflowDirection.A ? "et LIGE ciffer for Luftretning A" : "et ULIGE ciffer for Luftretning V")}."
                        : "Aksial indsugning, radial udblæsning."
                ),
                new CompatibilityCheck(
                    Dimension: "Motor Technology",
                    SourceValue: sourceProduct.MotorDescription,
                    TargetRequirement: sourceProduct.Technology == MotorTechnology.EC ? "EC motor (3G) med 0-10V/Modbus" : "AC motor",
                    Status: "COMPATIBLE",
                    Reason: sourceProduct.Technology == MotorTechnology.EC
                        ? "EC 3G teknologi sikrer integreret motorstyring."
                        : "AC motorstyring."
                )
            };

            var overallStatus = replacementAnalysis.MatchedDatabaseCandidates.Count > 0 ? "COMPATIBLE" : "CONDITIONAL";
            var overallReason = replacementAnalysis.Summary;

            var draftSb = new StringBuilder();
            draftSb.AppendLine($"### Erstatnings- og kompatibilitetsanalyse for {sourceProduct.RawCode}\n");
            draftSb.AppendLine($"**Produkt**: {sourceProduct.RawCode} ({sourceProduct.CleanCode})");
            draftSb.AppendLine($"**Type**: {sourceProduct.FanFamily} ({sourceProduct.FanTypeDescription})");
            draftSb.AppendLine($"**Motorteknologi**: {sourceProduct.Technology} ({sourceProduct.MotorDescription})");
            draftSb.AppendLine($"**Diameter**: Ø{sourceProduct.ImpellerDiameterMm} mm");
            draftSb.AppendLine($"**Luftretning**: {sourceProduct.AirflowDescription}\n");
            draftSb.AppendLine($"**Status**: {overallStatus}");
            draftSb.AppendLine($"**Konklusion**: {overallReason}\n");

            if (replacementAnalysis.MatchedDatabaseCandidates.Count > 0)
            {
                draftSb.AppendLine("#### Direkte erstatningsmodeller i databasen:");
                foreach (var match in replacementAnalysis.MatchedDatabaseCandidates)
                {
                    draftSb.AppendLine($"- **{match.RawCode}**: {match.CleanCode} (Ø{match.ImpellerDiameterMm} mm, {match.MotorDescription}, {match.AirflowDescription})");
                }
                draftSb.AppendLine();
            }
            else
            {
                draftSb.AppendLine("Der er ingen direkte drop-in erstatningsmodeller tilgængelige i den lokale database.\n");
            }

            if (replacementAnalysis.TheoreticalPatterns.Count > 0)
            {
                draftSb.AppendLine("#### Anbefalede erstatningsmodeller fra ebm-papst sortimentet:");
                foreach (var pat in replacementAnalysis.TheoreticalPatterns)
                {
                    draftSb.AppendLine($"- **{pat.SuggestedModelOrPrefix}**: {pat.PatternType} - {pat.Description}");
                    if (pat.Requirements != null && pat.Requirements.Count > 0)
                    {
                        foreach (var req in pat.Requirements)
                        {
                            draftSb.AppendLine($"  * {req}");
                        }
                    }
                }
                draftSb.AppendLine();
            }

            if (replacementAnalysis.ReplacementRules.Count > 0)
            {
                draftSb.AppendLine("#### Kritiske kompatibilitetskrav og regler:");
                foreach (var rule in replacementAnalysis.ReplacementRules)
                {
                    draftSb.AppendLine($"- {rule}");
                }
            }

            var structuredDraft = draftSb.ToString().Trim();
            var result = new CompatibilityResult(checks, overallStatus, overallReason, null, replacementAnalysis);
            return (structuredDraft, allChunks, result);
        }

        // CASE 2: Two or more products point-to-point comparison
        if (ebmProducts.Count >= 2)
        {
            ebmEval = _ebmParser.Compare(ebmProducts[0], ebmProducts[1]);
        }

        if (entities.Count > 0)
        {
            var searchTasks = entities.Select(entity =>
            {
                var attrs = governorResult.RequestedAttributes != null ? string.Join(" ", governorResult.RequestedAttributes) : "";
                var query = $"{entity.Name} compatibility {attrs}".Trim();
                return vectorStore.SearchAsync(query);
            }).ToList();

            var searchResults = await Task.WhenAll(searchTasks);
            foreach (var entityChunks in searchResults)
            {
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = initialSearchTask != null
                ? await initialSearchTask
                : await vectorStore.SearchAsync(question);
            allChunks.AddRange(fallbackChunks);
        }

        allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

        var ebmPromptContext = ebmEval != null
            ? $"\n\nDeterministic ebm-papst Domain Evaluation:\nSummary: {ebmEval.Summary}\nCritical Blockers:\n{string.Join("\n", ebmEval.CriticalBlockers.Select(b => $"- {b}"))}\nDifferences:\n{string.Join("\n", ebmEval.Differences.Select(d => $"- {d.Dimension}: {d.ValueA} vs {d.ValueB} (Critical: {d.IsCriticalIncompatibility}) - {d.Explanation}"))}"
            : "";

        var prompt2 = $$"""
            You are the Compatibility RAG workflow executor for industrial fans and motor drives (ebm-papst domain).
            Evaluate compatibility and direct replacement viability between the components or models mentioned in the query.
            Examine dimensions like airflow direction, voltage, motor technology, diameter, protocol, baud rate, signal type, physical interface, mounting, etc.

            CRITICAL RULES:
            - For axial fans, airflow direction A vs V (even vs odd 12th digit in product code) blurs 180 degrees reversed airflow. This makes direct substitution INCOMPATIBLE.
            - S and W are A-axial fans with accessories (S = guard grille, W = wall ring).
            - R, K, G, D are centrifugal fans with distinct mounting/housings.
            {{ebmPromptContext}}

            Return a structured JSON object strictly matching this schema:
            {
              "checks": [
                {
                  "dimension": "dimension checked (e.g. airflow_direction, voltage, diameter)",
                  "sourceValue": "source value or spec",
                  "targetRequirement": "target requirement or spec",
                  "status": "COMPATIBLE" or "INCOMPATIBLE" or "CONDITIONAL" or "UNKNOWN",
                  "reason": "explanation"
                }
              ],
              "status": "COMPATIBLE" or "INCOMPATIBLE" or "CONDITIONAL" or "UNKNOWN",
              "reason": "overall explanation"
            }

            Context chunks:
            {{string.Join("\n\n", allChunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Query: {{question}}
            """;

        var jsonResponse = await _llmService.GenerateCompletionAsync(prompt2, requireJson: true);
        CompatibilityResult? compatibilityResult = null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            compatibilityResult = JsonSerializer.Deserialize<CompatibilityResult>(jsonResponse, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Compatibility RAG output: {ex.Message}");
        }

        // Merge deterministic ebm domain checks into compatibility checks
        if (ebmEval != null)
        {
            var existingChecks = compatibilityResult?.Checks ?? new List<CompatibilityCheck>();
            
            foreach (var diff in ebmEval.Differences)
            {
                if (!existingChecks.Any(c => string.Equals(c.Dimension, diff.Dimension, StringComparison.OrdinalIgnoreCase)))
                {
                    existingChecks.Insert(0, new CompatibilityCheck(
                        Dimension: diff.Dimension,
                        SourceValue: diff.ValueA,
                        TargetRequirement: diff.ValueB,
                        Status: diff.IsCriticalIncompatibility ? "INCOMPATIBLE" : "CONDITIONAL",
                        Reason: diff.Explanation
                    ));
                }
            }

            var overallStatus = ebmEval.CriticalBlockers.Count > 0 
                ? "INCOMPATIBLE" 
                : (compatibilityResult?.Status ?? "COMPATIBLE");

            var overallReason = ebmEval.CriticalBlockers.Count > 0
                ? $"{ebmEval.Summary} {compatibilityResult?.Reason}".Trim()
                : (compatibilityResult?.Reason ?? ebmEval.Summary);

            compatibilityResult = new CompatibilityResult(existingChecks, overallStatus, overallReason, ebmEval);
        }

        var textResponse = "### Compatibility Analysis\n\n";
        if (compatibilityResult != null)
        {
            if (ebmEval?.CriticalBlockers.Count > 0)
            {
                textResponse += "### ⚠️ Kritisk Inkompatibilitet (ebm-papst Domæne)\n";
                foreach (var blocker in ebmEval.CriticalBlockers)
                {
                    textResponse += $"- ❌ **{blocker}**\n";
                }
                textResponse += "\n";
            }

            textResponse += $"**Overall Status: {compatibilityResult.Status}**\n";
            textResponse += $"**Reason**: {compatibilityResult.Reason}\n\n";
            textResponse += "#### Dimensions Checked:\n";
            foreach (var check in compatibilityResult.Checks)
            {
                textResponse += $"- **{check.Dimension}**: {check.Status}\n";
                textResponse += $"  - Source Value: {check.SourceValue}\n";
                textResponse += $"  - Target Requirement: {check.TargetRequirement}\n";
                textResponse += $"  - Reason: {check.Reason}\n";
            }
        }
        else
        {
            textResponse += jsonResponse;
        }

        return (textResponse, allChunks, compatibilityResult);
    }
}

public class DiagnosticWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public DiagnosticWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var allChunks = new List<DocumentChunk>();
        var entities = governorResult.Entities ?? new List<EntityInfo>();

        if (entities.Count > 0)
        {
            var searchTasks = entities.Select(entity =>
            {
                var query = $"{entity.Name} troubleshooting diagnostic error".Trim();
                return vectorStore.SearchAsync(query);
            }).ToList();

            var searchResults = await Task.WhenAll(searchTasks);
            foreach (var entityChunks in searchResults)
            {
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = initialSearchTask != null
                ? await initialSearchTask
                : await vectorStore.SearchAsync(question);
            allChunks.AddRange(fallbackChunks);
        }

        allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

        var prompt = $$"""
            You are the Diagnostic RAG workflow executor for troubleshooting technical issues.
            Identify documented causes, diagnostic checks, expected results, and troubleshooting steps.
            Distinguish between:
            - documented cause
            - derived possibility
            - unsupported possibility

            Return a structured JSON object strictly matching this schema:
            {
              "causes": [
                {
                  "cause": "cause description",
                  "status": "SUPPORTED" or "DERIVED" or "UNSUPPORTED",
                  "evidence": ["source document references"],
                  "check": "how to verify",
                  "expectedResult": "expected value or state"
                }
              ],
              "troubleshootingSteps": [
                "step 1",
                "step 2"
              ]
            }

            Context chunks:
            {{string.Join("\n\n", allChunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Query: {{question}}
            """;

        var jsonResponse = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
        DiagnosticResult? diagnosticResult = null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            diagnosticResult = JsonSerializer.Deserialize<DiagnosticResult>(jsonResponse, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Diagnostic RAG output: {ex.Message}");
        }

        var textResponse = "### Diagnostic Analysis\n\n";
        if (diagnosticResult != null)
        {
            textResponse += "#### Potential Causes:\n";
            foreach (var cause in diagnosticResult.Causes)
            {
                textResponse += $"- **{cause.Cause}** ({cause.Status})\n";
                textResponse += $"  - Check: {cause.Check}\n";
                textResponse += $"  - Expected: {cause.ExpectedResult}\n";
                if (cause.Evidence != null && cause.Evidence.Count > 0)
                {
                    textResponse += $"  - Evidence: {string.Join(", ", cause.Evidence)}\n";
                }
            }

            textResponse += "\n#### Troubleshooting Steps:\n";
            foreach (var step in diagnosticResult.TroubleshootingSteps)
            {
                textResponse += $"- {step}\n";
            }
        }
        else
        {
            textResponse += jsonResponse;
        }

        return (textResponse, allChunks, diagnosticResult);
    }
}

public class CalculationWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public CalculationWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        // For calculations, search for formulas and parameters
        var chunks = (initialSearchTask != null ? await initialSearchTask : await vectorStore.SearchAsync(question)).ToList();

        var prompt = $$"""
            You are the Calculation RAG workflow executor.
            Your job is to identify the parameters and formulas required from technical documents, and perform the calculation.
            Only perform calculations based on documented parameters.

            Return a JSON object or clear markdown representing the calculation, including:
            - Formula
            - Inputs (with source document references)
            - Step-by-step math
            - Final result
            - CRITICAL LANGUAGE REQUIREMENT: You MUST ALWAYS write the response and explanations in the EXACT same language as the user's question.

            Context chunks:
            {{string.Join("\n\n", chunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Query: {{question}}
            """;

        var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: false);
        return (response, chunks, null);
    }
}

public class DesignWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public DesignWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var chunks = (initialSearchTask != null ? await initialSearchTask : await vectorStore.SearchAsync(question)).ToList();
        var answer = await _llmService.GenerateResponseAsync(question, chunks);
        return (answer, chunks, null);
    }
}

public class ClarificationWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public ClarificationWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var prompt = $"The user asked: '{question}'. This is incomplete or unclear. Clarification reason: '{governorResult.ClarificationReason}'. Please ask the user to clarify or provide the missing details. CRITICAL LANGUAGE REQUIREMENT: You MUST formulate your response in the EXACT same language as the user's question.";
        var answer = await _llmService.GenerateCompletionAsync(prompt, requireJson: false);
        return (answer, new List<DocumentChunk>(), null);
    }
}

public class OverviewWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly IDocumentRepository? _documentRepository;

    public OverviewWorkflowExecutor(
        ILlmService llmService,
        IEbmProductCodeParser? ebmParser = null,
        IDocumentRepository? documentRepository = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
        _documentRepository = documentRepository;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var (allDocsDict, _) = await CatalogCacheHelper.GetCatalogDocumentsAndProductsAsync(vectorStore, _documentRepository, _ebmParser);

        // Search for relevant introductory / summary chunks
        var sampleChunks = (await vectorStore.SearchAsync(question, limit: 10)).ToList();

        var catalogItems = new List<CatalogItem>();
        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in allDocsDict.Values)
        {
            var docName = !string.IsNullOrWhiteSpace(doc.FileName) ? doc.FileName : doc.Id;
            var extracted = _ebmParser.ExtractProductsFromText(docName);

            if (extracted.Count > 0)
            {
                foreach (var prod in extracted)
                {
                    if (seenModels.Add(prod.CleanCode))
                    {
                        var category = prod.FanFamily switch
                        {
                            FanFamilyType.Axial => "Aksialventilatorer (Axial Fans)",
                            FanFamilyType.Centrifugal => "Centrifugalventilatorer & RadiPac (Centrifugal Fans)",
                            FanFamilyType.CompactOrSpecial => "Kompaktblæsere & DC-ventilatorer (Compact Fans)",
                            _ => "Øvrige ventilatorer og moduler"
                        };

                        catalogItems.Add(new CatalogItem(
                            ModelName: prod.RawCode,
                            Category: category,
                            Technology: prod.MotorDescription,
                            DiameterMm: prod.ImpellerDiameterMm,
                            Airflow: prod.AirflowDescription,
                            DocumentSource: docName,
                            ParsedInfo: prod
                        ));
                    }
                }
            }
            else
            {
                var cleanName = System.IO.Path.GetFileNameWithoutExtension(docName);
                if (seenModels.Add(cleanName))
                {
                    catalogItems.Add(new CatalogItem(
                        ModelName: cleanName,
                        Category: "Dokumenterede modeller & datablade",
                        Technology: "Se datablad for motorspecifikationer",
                        DiameterMm: null,
                        Airflow: "Se datablad",
                        DocumentSource: docName,
                        ParsedInfo: null
                    ));
                }
            }
        }

        // Also extract from chunks
        foreach (var chunk in sampleChunks)
        {
            var chunkProducts = _ebmParser.ExtractProductsFromText(chunk.Text);
            foreach (var prod in chunkProducts)
            {
                if (seenModels.Add(prod.CleanCode))
                {
                    var category = prod.FanFamily switch
                    {
                        FanFamilyType.Axial => "Aksialventilatorer (Axial Fans)",
                        FanFamilyType.Centrifugal => "Centrifugalventilatorer & RadiPac (Centrifugal Fans)",
                        FanFamilyType.CompactOrSpecial => "Kompaktblæsere & DC-ventilatorer (Compact Fans)",
                        _ => "Øvrige ventilatorer og moduler"
                    };

                    catalogItems.Add(new CatalogItem(
                        ModelName: prod.RawCode,
                        Category: category,
                        Technology: prod.MotorDescription,
                        DiameterMm: prod.ImpellerDiameterMm,
                        Airflow: prod.AirflowDescription,
                        DocumentSource: chunk.FileName ?? chunk.DocumentId,
                        ParsedInfo: prod
                    ));
                }
            }
        }

        var categorizedGroups = catalogItems
            .GroupBy(i => i.Category)
            .ToDictionary(g => g.Key, g => g.ToList());

        var catalogResult = new CatalogOverviewResult(
            TotalDocuments: allDocsDict.Count,
            TotalModels: catalogItems.Count,
            Categories: categorizedGroups
        );

        var isDanish = question.Contains("hvilk", StringComparison.OrdinalIgnoreCase) ||
                       question.Contains("hvad", StringComparison.OrdinalIgnoreCase) ||
                       question.Contains("findes", StringComparison.OrdinalIgnoreCase) ||
                       question.Contains("modeller", StringComparison.OrdinalIgnoreCase) ||
                       question.Contains("alle", StringComparison.OrdinalIgnoreCase);

        var draftSb = new StringBuilder();
        if (isDanish)
        {
            draftSb.AppendLine("### ebm-papst Ventilator- og Produktoversigt\n");
            draftSb.AppendLine($"Systemet indeholder **{catalogResult.TotalModels} modeller** fordelt over **{catalogResult.TotalDocuments} datablade/dokumenter**:\n");

            foreach (var (category, items) in catalogResult.Categories)
            {
                draftSb.AppendLine($"#### {category}");
                foreach (var item in items)
                {
                    var diamStr = item.DiameterMm.HasValue ? $", Ø{item.DiameterMm} mm" : "";
                    var airflowStr = !string.IsNullOrWhiteSpace(item.Airflow) ? $", {item.Airflow}" : "";
                    draftSb.AppendLine($"- **{item.ModelName}**: {item.Technology}{diamStr}{airflowStr} *(Kilde: {item.DocumentSource})*");
                }
                draftSb.AppendLine();
            }

            draftSb.AppendLine("#### Typenøgle og Parametre");
            draftSb.AppendLine("- **Ventilatortype**: 1. bogstav (A = Aksial grundmodel, S = med beskyttelsesgitter, W = i vægring, R = Løst centrifugalhjul, K = RadiPac i ramme, G/D = Sneglehus).");
            draftSb.AppendLine("- **Motorteknologi**: 2.-3. tegn (3G = EC energieffektiv motor; AC angiver poltal og 1~ 230V / 3~ 400V).");
            draftSb.AppendLine("- **Diameter**: 4.-6. ciffer angiver impellerdiameter i mm.");
            draftSb.AppendLine("- **Luftretning (Aksial)**: 12. ciffer (lige = Luftretning A, ulige = Luftretning V).");
        }
        else
        {
            draftSb.AppendLine("### ebm-papst Ventilator and Product Overview\n");
            draftSb.AppendLine($"The system currently indexes **{catalogResult.TotalModels} ventilator models** across **{catalogResult.TotalDocuments} technical datasheets/documents**:\n");

            foreach (var (category, items) in catalogResult.Categories)
            {
                draftSb.AppendLine($"#### {category}");
                foreach (var item in items)
                {
                    var diamStr = item.DiameterMm.HasValue ? $", Ø{item.DiameterMm} mm" : "";
                    var airflowStr = !string.IsNullOrWhiteSpace(item.Airflow) ? $", {item.Airflow}" : "";
                    draftSb.AppendLine($"- **{item.ModelName}**: {item.Technology}{diamStr}{airflowStr} *(Source: {item.DocumentSource})*");
                }
                draftSb.AppendLine();
            }

            draftSb.AppendLine("#### Product Designation Key & Parameters");
            draftSb.AppendLine("- **Fan Type**: 1st char (A = Axial base, S = with guard grille, W = in wall ring, R = Motorized impeller, K = RadiPac in bracket, G/D = Scroll housing).");
            draftSb.AppendLine("- **Motor Technology**: 2nd-3rd chars (3G = EC energy-efficient motor; AC indicates pole count and 1~ 230V / 3~ 400V).");
            draftSb.AppendLine("- **Diameter**: 4th-6th chars specify impeller diameter in mm.");
            draftSb.AppendLine("- **Airflow Direction (Axial)**: 12th char (even = Airflow A, odd = Airflow V).");
        }

        var structuredDraft = draftSb.ToString().Trim();
        return (structuredDraft, sampleChunks, catalogResult);
    }
}
