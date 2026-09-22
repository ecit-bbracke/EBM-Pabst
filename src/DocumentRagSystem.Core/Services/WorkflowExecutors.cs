using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public interface IWorkflowExecutor
{
    Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore
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
        IVectorStore vectorStore
    )
    {
        var chunks = (await vectorStore.SearchAsync(question)).ToList();
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
        IVectorStore vectorStore
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
            foreach (var entity in entities)
            {
                var attrs = governorResult.RequestedAttributes != null ? string.Join(" ", governorResult.RequestedAttributes) : "";
                var query = $"{entity.Name} specifications {attrs}".Trim();
                var entityChunks = await vectorStore.SearchAsync(query);
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = await vectorStore.SearchAsync(question);
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

    public CompatibilityWorkflowExecutor(ILlmService llmService, IEbmProductCodeParser? ebmParser = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore
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
            foreach (var entity in entities)
            {
                var attrs = governorResult.RequestedAttributes != null ? string.Join(" ", governorResult.RequestedAttributes) : "";
                var query = $"{entity.Name} compatibility {attrs}".Trim();
                var entityChunks = await vectorStore.SearchAsync(query);
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = await vectorStore.SearchAsync(question);
            allChunks.AddRange(fallbackChunks);
        }

        allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

        var ebmPromptContext = ebmEval != null
            ? $"\n\nDeterministic ebm-papst Domain Evaluation:\nSummary: {ebmEval.Summary}\nCritical Blockers:\n{string.Join("\n", ebmEval.CriticalBlockers.Select(b => $"- {b}"))}\nDifferences:\n{string.Join("\n", ebmEval.Differences.Select(d => $"- {d.Dimension}: {d.ValueA} vs {d.ValueB} (Critical: {d.IsCriticalIncompatibility}) - {d.Explanation}"))}"
            : "";

        var prompt = $$"""
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

        var jsonResponse = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
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
        IVectorStore vectorStore
    )
    {
        var allChunks = new List<DocumentChunk>();
        var entities = governorResult.Entities ?? new List<EntityInfo>();

        if (entities.Count > 0)
        {
            foreach (var entity in entities)
            {
                var query = $"{entity.Name} troubleshooting diagnostic error".Trim();
                var entityChunks = await vectorStore.SearchAsync(query);
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = await vectorStore.SearchAsync(question);
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
        IVectorStore vectorStore
    )
    {
        // For calculations, search for formulas and parameters
        var chunks = (await vectorStore.SearchAsync(question)).ToList();

        var prompt = $$"""
            You are the Calculation RAG workflow executor.
            Your job is to identify the parameters and formulas required from technical documents, and perform the calculation.
            Only perform calculations based on documented parameters.

            Return a JSON object or clear markdown representing the calculation, including:
            - Formula
            - Inputs (with source document references)
            - Step-by-step math
            - Final result

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
        IVectorStore vectorStore
    )
    {
        var chunks = (await vectorStore.SearchAsync(question)).ToList();
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
        IVectorStore vectorStore
    )
    {
        var prompt = $"The user's question: '{question}' is incomplete or unclear. Clarification reason: '{governorResult.ClarificationReason}'. Please ask the user to clarify or provide the missing details.";
        var answer = await _llmService.GenerateCompletionAsync(prompt, requireJson: false);
        return (answer, new List<DocumentChunk>(), null);
    }
}
