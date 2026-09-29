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
    private readonly ILlmClient _llmClient;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly ILogger<InputGovernor>? _logger;

    public InputGovernor(
        ILlmClient llmClient, 
        IEbmProductCodeParser? ebmParser = null,
        ILogger<InputGovernor>? logger = null)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
        _logger = logger;
    }

    public InputGovernor(
        ILlmClientResolver resolver,
        IEbmProductCodeParser? ebmParser = null,
        ILogger<InputGovernor>? logger = null)
        : this(resolver?.Resolve(LlmPurpose.InputGovernor) ?? throw new ArgumentNullException(nameof(resolver)), ebmParser, logger)
    {
    }

    public InputGovernor(
        ILlmService llmService, 
        IEbmProductCodeParser? ebmParser = null,
        ILogger<InputGovernor>? logger = null)
        : this(new LlmServiceToClientAdapter(llmService), ebmParser, logger)
    {
    }

    public Task<InputGovernorResult> GovernInputAsync(string question)
    {
        return GovernInputAsync(question, question, null);
    }

    private const string SystemInstruction = """
You are the Input Governor for an industrial ventilation & drive system (ebm-papst domain).
Classify the user's technical question into ONE intent:
- SPEC_LOOKUP (specs/attributes of a single fan/model)
- PROCEDURE (steps to install/configure/run)
- COMPARISON (comparing 2+ models/products)
- COMPATIBILITY (interchangeability/replacements/matching rules)
- TROUBLESHOOTING (faults/errors/symptoms)
- DESIGN (system design/setup)
- CALCULATION (formulas/math)
- OVERVIEW (catalog/inventory/list of models)
- CLARIFICATION (needs clarification)

Output JSON:
{
  "intent": "INTENT",
  "confidence": 0.95,
  "entities": [{"type": "fan/controller/sensor/etc", "name": "exact name"}],
  "requested_attributes": ["voltage", "airflow_direction", "diameter"],
  "constraints": [],
  "clarification_required": false,
  "clarification_reason": null
}
""";

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
                contextDescription = $"Conversational Context:\n{string.Join("\n", contextParts)}\n";
            }
        }

        var userPrompt = $"{contextDescription}Effective Technical Question: {effectiveQuestion}".Trim();

        _logger?.LogInformation(
            "[InputGovernor] Executing intent classification prompt ({PromptLength} chars) for question: \"{Question}\".\nPrompt:\n{Prompt}",
            userPrompt.Length, effectiveQuestion, userPrompt);

        try
        {
            var req = LlmRequest.FromPrompt(
                userPrompt, 
                requireJson: true, 
                systemInstruction: SystemInstruction, 
                temperature: 0.0, 
                maxOutputTokens: 256);

            var structuredRes = await _llmClient.GenerateStructuredAsync<InputGovernorResult>(req);
            stopwatch.Stop();
            
            var result = structuredRes.Value;
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
