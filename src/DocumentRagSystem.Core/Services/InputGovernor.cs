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

public interface IInputGovernor
{
    Task<InputGovernorResult> GovernInputAsync(string question);
    Task<InputGovernorResult> GovernInputAsync(string effectiveQuestion, string? originalQuestion, ConversationContext? conversationContext);
}

public class InputGovernor : IInputGovernor
{
    private readonly ILlmService _llmService;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly ILogger<InputGovernor>? _logger;

    public InputGovernor(
        ILlmService llmService, 
        IEbmProductCodeParser? ebmParser = null,
        ILogger<InputGovernor>? logger = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
        _logger = logger;
    }

    public Task<InputGovernorResult> GovernInputAsync(string question)
    {
        return GovernInputAsync(question, question, null);
    }

    public async Task<InputGovernorResult> GovernInputAsync(
        string effectiveQuestion,
        string? originalQuestion,
        ConversationContext? conversationContext
    )
    {
        if (string.IsNullOrWhiteSpace(effectiveQuestion))
            throw new ArgumentNullException(nameof(effectiveQuestion));

        var stopwatch = Stopwatch.StartNew();

        var combinedText = string.IsNullOrWhiteSpace(originalQuestion) || string.Equals(effectiveQuestion, originalQuestion, StringComparison.OrdinalIgnoreCase)
            ? effectiveQuestion
            : $"{effectiveQuestion} {originalQuestion}";

        var extractedProducts = _ebmParser.ExtractProductsFromText(combinedText);

        // Fast-path deterministic short-circuit for well-defined domain queries
        if (TryFastPathDeterministicClassification(combinedText, extractedProducts, conversationContext, out var fastResult))
        {
            stopwatch.Stop();
            _logger?.LogInformation(
                "[InputGovernor] Fast-path deterministic classification completed in {DurationMs}ms (Intent: {Intent}, Confidence: {Confidence}, Entities: {EntityCount}).",
                stopwatch.ElapsedMilliseconds, fastResult.Intent, fastResult.Confidence, fastResult.Entities?.Count ?? 0);
            return fastResult;
        }

        var contextDescription = "";
        if (conversationContext != null)
        {
            var contextParts = new List<string>();
            if (conversationContext.ActiveEntities != null && conversationContext.ActiveEntities.Count > 0)
            {
                contextParts.Add($"Active entities: {string.Join(", ", conversationContext.ActiveEntities.Select(e => $"{e.Name} ({e.Type})"))}");
            }
            if (conversationContext.ActiveConstraints != null && conversationContext.ActiveConstraints.Count > 0)
            {
                contextParts.Add($"Active constraints: {string.Join(", ", conversationContext.ActiveConstraints.Select(c => $"{c.Attribute} {c.Operator} {c.Value} {c.Unit}".Trim()))}");
            }
            if (conversationContext.CandidateSet != null && conversationContext.CandidateSet.Count > 0)
            {
                contextParts.Add($"Candidate set: {string.Join(", ", conversationContext.CandidateSet)}");
            }
            if (contextParts.Count > 0)
            {
                contextDescription = $"\n\nConversational Context:\n{string.Join("\n", contextParts)}";
            }
        }

        var prompt = $$"""
            You are the Input Governor for a highly specialized Technical RAG Orchestrator for ventilation and industrial drive systems (ebm-papst domain).
            Your sole job is to classify the user's effective technical query and extract entities and constraints.
            Do NOT attempt to answer the user's technical question. Only perform the analysis.

            Domain Knowledge:
            - 12-character ebm-papst product numbers (e.g. A6E450AP0201, K3G560PC0401, S4E350AN0130):
              - 1st char: A (axial base), S (axial with guard grille), W (axial in wall ring); R (centrifugal 1-inlet impeller), K (centrifugal in bracket/RadiPac), G (centrifugal in scroll), D (centrifugal dual-inlet in scroll).
              - 2nd-3rd chars: 3G (EC motor technology), or AC poles+phase (e.g. 6E = 6-pole 1-phase AC, 4D = 4-pole 3-phase AC).
              - 4th-6th chars: Impeller diameter in mm (e.g. 450 = Ø450 mm).
              - 12th char (Axial fans): Even digit (0, 2, 4, 6, 8) = Airflow direction A; Odd digit (1, 3, 5, 7, 9) = Airflow direction V.
            - Other product numbering: 8300 series (replaces K3G), 4114/6314/3258 compact fans, and 10-digit part numbers (e.g. 9694300352, 9295420021).

            You must categorize the query into one of the following intents:
            - SPEC_LOOKUP: Retrieve single specifications or technical attributes of a single product/model.
            - PROCEDURE: Steps on how to perform an action or run/install/configure a device.
            - COMPARISON: Compare multiple entities, models, or product numbers.
            - COMPATIBILITY: Evaluate if multiple devices/models work together, can replace each other, or fit technical constraints.
            - TROUBLESHOOTING: Diagnosing problems, faults, or symptoms.
            - DESIGN: Questions asking to design a setup, system, or configuration.
            - CALCULATION: Asking to calculate some value or parameter.
            - OVERVIEW: Questions asking to list, inventory, summarize, or discover what fans, models, ventilators, or products exist in the database or catalog (e.g. "What ventilators are there?", "Hvilke ventilatorer findes der?", "List all available fans", "What models do you have?").
            - CLARIFICATION: General queries or queries requiring user input before technical retrieval can proceed.

            Return a JSON object conforming exactly to this schema:
            {
              "intent": "INTENT",
              "confidence": 0.95,
              "entities": [
                {
                  "type": "fan/controller/sensor/device/etc",
                  "name": "exact name of entity"
                }
              ],
              "requested_attributes": ["list of requested technical attributes, e.g. voltage, airflow_direction, diameter, technology"],
              "constraints": ["list of explicit technical constraints mentioned"],
              "clarification_required": false,
              "clarification_reason": null
            }
            {{contextDescription}}

            Effective Technical Question:
            {{effectiveQuestion}}
            """;

        _logger?.LogInformation(
            "[InputGovernor] Executing intent classification prompt ({PromptLength} chars) for question: \"{Question}\".\nPrompt:\n{Prompt}",
            prompt.Length, effectiveQuestion, prompt);

        try
        {
            var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
            stopwatch.Stop();
            
            // Clean up possible markdown code block fences if present in LLM output
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
            var result = JsonSerializer.Deserialize<InputGovernorResult>(cleanedResponse, options);
            if (result != null)
            {
                // Enrich result with deterministically parsed ebm products
                var entities = result.Entities ?? new List<EntityInfo>();
                var requestedAttrs = result.RequestedAttributes ?? new List<string>();

                foreach (var prod in extractedProducts)
                {
                    if (!entities.Any(e => string.Equals(e.Name, prod.RawCode, StringComparison.OrdinalIgnoreCase) || 
                                           string.Equals(e.Name, prod.CleanCode, StringComparison.OrdinalIgnoreCase)))
                    {
                        var entityType = prod.FanFamily switch
                        {
                            FanFamilyType.Axial => "axial_fan",
                            FanFamilyType.Centrifugal => "centrifugal_fan",
                            _ => "fan"
                        };
                        entities.Add(new EntityInfo(entityType, prod.RawCode));
                    }
                }

                var isReplacementQuery = combinedText.Contains("erstat", StringComparison.OrdinalIgnoreCase) ||
                                         combinedText.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
                                         combinedText.Contains("substitut", StringComparison.OrdinalIgnoreCase) ||
                                         combinedText.Contains("udskift", StringComparison.OrdinalIgnoreCase) ||
                                         combinedText.Contains("alternativ", StringComparison.OrdinalIgnoreCase);

                var finalIntent = result.Intent;
                if (isReplacementQuery && extractedProducts.Count >= 1 && (finalIntent == "SPEC_LOOKUP" || finalIntent == "PROCEDURE"))
                {
                    finalIntent = "COMPATIBILITY";
                }

                if (extractedProducts.Count >= 1 && (finalIntent == "COMPARISON" || finalIntent == "COMPATIBILITY"))
                {
                    if (!requestedAttrs.Contains("airflow_direction"))
                        requestedAttrs.Add("airflow_direction");
                    if (!requestedAttrs.Contains("technology"))
                        requestedAttrs.Add("technology");
                }

                _logger?.LogInformation(
                    "[InputGovernor] Classification completed in {DurationMs}ms. Intent: {Intent}, Confidence: {Confidence}, Entities: {EntityCount}, ClarificationRequired: {ClarificationRequired}.",
                    stopwatch.ElapsedMilliseconds, finalIntent, result.Confidence, entities.Count, result.ClarificationRequired);

                return result with 
                { 
                    Intent = finalIntent,
                    Entities = entities, 
                    RequestedAttributes = requestedAttrs,
                    ParsedEbmProducts = extractedProducts 
                };
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogWarning(ex, "[InputGovernor] Error after {DurationMs}ms: {Message}. Falling back to default.", stopwatch.ElapsedMilliseconds, ex.Message);
            Console.WriteLine($"Error in InputGovernor: {ex.Message}. Falling back to default.");
        }

        // Safe fallback
        var isOverviewFallback = extractedProducts.Count == 0 && (
            combinedText.Contains("what ventilator", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("what fan", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("which ventilator", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("which fan", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("hvilke ventilator", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("hvilke blæser", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("hvad findes", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("list all", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("alle ventilator", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("alle modeller", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("hvilke modeller", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("what models", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("overview", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("katalog", StringComparison.OrdinalIgnoreCase)
        );

        var isReplacementFallback = combinedText.Contains("erstat", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("substitut", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("udskift", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("alternativ", StringComparison.OrdinalIgnoreCase);

        var fallbackEntities = extractedProducts.Select(p => new EntityInfo(p.FanFamily == FanFamilyType.Axial ? "axial_fan" : "fan", p.RawCode)).ToList();
        var fallbackIntent = isOverviewFallback 
            ? "OVERVIEW" 
            : (isReplacementFallback ? "COMPATIBILITY" : (extractedProducts.Count >= 2 ? "COMPARISON" : "SPEC_LOOKUP"));

        return new InputGovernorResult(
            Intent: fallbackIntent,
            Confidence: 0.5,
            Entities: fallbackEntities,
            RequestedAttributes: new(),
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: extractedProducts
        );
    }

    private static bool TryFastPathDeterministicClassification(
        string combinedText,
        List<EbmProductInfo> extractedProducts,
        ConversationContext? conversationContext,
        out InputGovernorResult result)
    {
        result = null!;
        var text = combinedText.Trim();

        // 1. Overview intent (asking for available ventilators/fans/catalog/inventory)
        if (extractedProducts.Count == 0 && (conversationContext == null || conversationContext.ActiveEntities == null || conversationContext.ActiveEntities.Count == 0))
        {
            if (IsOverviewQuery(text))
            {
                result = new InputGovernorResult(
                    Intent: "OVERVIEW",
                    Confidence: 1.0,
                    Entities: new List<EntityInfo>(),
                    RequestedAttributes: new List<string>(),
                    Constraints: new List<string>(),
                    ClarificationRequired: false,
                    ClarificationReason: null,
                    ParsedEbmProducts: new List<EbmProductInfo>()
                );
                return true;
            }
        }

        // 2. Compatibility & Replacement intent (when 1 or more EbmProduct codes are present with clear replacement/compatibility keywords)
        if (extractedProducts.Count >= 1)
        {
            var isReplacement = text.Contains("erstat", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("substitut", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("udskift", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("alternativ", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("kompatib", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("compatib", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("passer til", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("work with", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("fungere med", StringComparison.OrdinalIgnoreCase);

            if (isReplacement)
            {
                var entities = extractedProducts.Select(p => new EntityInfo(
                    p.FanFamily == FanFamilyType.Axial ? "axial_fan" : (p.FanFamily == FanFamilyType.Centrifugal ? "centrifugal_fan" : "fan"),
                    p.RawCode
                )).ToList();

                var requestedAttrs = new List<string> { "replacement", "airflow_direction", "technology", "diameter" };
                result = new InputGovernorResult(
                    Intent: "COMPATIBILITY",
                    Confidence: 1.0,
                    Entities: entities,
                    RequestedAttributes: requestedAttrs,
                    Constraints: new List<string>(),
                    ClarificationRequired: false,
                    ClarificationReason: null,
                    ParsedEbmProducts: extractedProducts
                );
                return true;
            }
        }

        // 3. Comparison intent (when 2 or more EbmProduct codes are present with comparison keywords or explicit comparison)
        if (extractedProducts.Count >= 2)
        {
            var isComparison = text.Contains("sammenlign", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("compare", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("forskellen", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("difference", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains(" vs ", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains(" mod ", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains(" kontra ", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains(" eller ", StringComparison.OrdinalIgnoreCase);

            if (isComparison)
            {
                var entities = extractedProducts.Select(p => new EntityInfo(
                    p.FanFamily == FanFamilyType.Axial ? "axial_fan" : (p.FanFamily == FanFamilyType.Centrifugal ? "centrifugal_fan" : "fan"),
                    p.RawCode
                )).ToList();

                var requestedAttrs = new List<string> { "airflow_direction", "technology", "diameter", "voltage" };
                result = new InputGovernorResult(
                    Intent: "COMPARISON",
                    Confidence: 1.0,
                    Entities: entities,
                    RequestedAttributes: requestedAttrs,
                    Constraints: new List<string>(),
                    ClarificationRequired: false,
                    ClarificationReason: null,
                    ParsedEbmProducts: extractedProducts
                );
                return true;
            }
        }

        return false;
    }

    private static bool IsOverviewQuery(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("what ventilator") ||
               lower.Contains("what fan") ||
               lower.Contains("which ventilator") ||
               lower.Contains("which fan") ||
               lower.Contains("hvilke ventilator") ||
               lower.Contains("hvilke blæser") ||
               lower.Contains("hvad findes der") ||
               lower.Contains("list all") ||
               lower.Contains("alle ventilator") ||
               lower.Contains("alle modeller") ||
               lower.Contains("hvilke modeller") ||
               lower.Contains("what models") ||
               lower.Contains("overview") ||
               lower.Contains("katalog") ||
               lower.Contains("hvad har vi af ventilatorer") ||
               lower.Contains("oversigt over ventilatorer");
    }
}

public enum WorkflowType
{
    SimpleRag,
    Comparison,
    Compatibility,
    Diagnostic,
    Calculation,
    Design,
    Clarification,
    Overview
}

public interface IWorkflowRouter
{
    WorkflowType RouteWorkflow(InputGovernorResult governorResult);
}

public class WorkflowRouter : IWorkflowRouter
{
    public WorkflowType RouteWorkflow(InputGovernorResult governorResult)
    {
        return governorResult.Intent.ToUpperInvariant() switch
        {
            "SPEC_LOOKUP" => WorkflowType.SimpleRag,
            "PROCEDURE" => WorkflowType.SimpleRag,
            "COMPARISON" => WorkflowType.Comparison,
            "COMPATIBILITY" => WorkflowType.Compatibility,
            "TROUBLESHOOTING" => WorkflowType.Diagnostic,
            "CALCULATION" => WorkflowType.Calculation,
            "DESIGN" => WorkflowType.Design,
            "CLARIFICATION" => WorkflowType.Clarification,
            "OVERVIEW" => WorkflowType.Overview,
            "CATALOG" => WorkflowType.Overview,
            _ => WorkflowType.SimpleRag
        };
    }
}
