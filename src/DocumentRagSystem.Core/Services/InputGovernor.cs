using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;

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
- SPEC_LOOKUP (specs/attributes, dimensions, data lookup, electrical voltage/power compatibility for a single model)
- PROCEDURE (steps to install/configure/run)
- COMPARISON (comparing 2+ models/products)
- COMPATIBILITY (interchangeability/replacements/matching rules between fan models)
- TROUBLESHOOTING (faults/errors/symptoms)
- DESIGN (system design/setup)
- CALCULATION (formulas/math)
- OVERVIEW (catalog/inventory/list of models)
- CLARIFICATION (needs clarification)
- GREETING (greetings, pleasantries, questions about assistant capabilities)

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

        // 0. Greeting intent (greetings / conversational pleasantries / bot capability questions without product codes)
        if (extractedProducts.Count == 0 && (conversationContext == null || conversationContext.ActiveEntities == null || conversationContext.ActiveEntities.Count == 0))
        {
            if (IsGreetingQuery(text))
            {
                result = new InputGovernorResult(
                    Intent: "GREETING",
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

            // If only 1 product is present, distinguish between a cross-product replacement search and an electrical/operating parameter compatibility check
            var isElectricalOrOperatingParameter =
                Regex.IsMatch(text, @"\b\d+\s*(?:v|vac|vdc|volt|hz|khz|w|kw|watt|a|ma|amp|ampere)\b", RegexOptions.IgnoreCase) ||
                text.Contains("spænding", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("voltage", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("strøm", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("current", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("effekt", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("power", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("frekvens", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("frequency", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("modbus", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("0-10v", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("0-10 v", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("pwm", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("potentiometer", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("tacho", StringComparison.OrdinalIgnoreCase);

            var isModelReplacement = isReplacement && (extractedProducts.Count >= 2 || !isElectricalOrOperatingParameter);

            if (isModelReplacement)
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

        // 4. Single-Product Intent (when exactly 1 EbmProduct code is detected and no conversational entity ambiguity)
        if (extractedProducts.Count == 1 && (conversationContext == null || conversationContext.ActiveEntities == null || conversationContext.ActiveEntities.Count <= 1))
        {
            var isTroubleshooting = text.Contains("fejl", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("fault", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("alarm", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("virker ikke", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("not working", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("defekt", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("problem", StringComparison.OrdinalIgnoreCase);

            var isProcedure = text.Contains("hvordan tilslut", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("hvordan monter", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("how to install", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("how to connect", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("ledningsdiagram", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("wiring diagram", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("montage", StringComparison.OrdinalIgnoreCase);

            var intent = isTroubleshooting ? "TROUBLESHOOTING" : (isProcedure ? "PROCEDURE" : "SPEC_LOOKUP");

            var entities = extractedProducts.Select(p => new EntityInfo(
                p.FanFamily == FanFamilyType.Axial ? "axial_fan" : (p.FanFamily == FanFamilyType.Centrifugal ? "centrifugal_fan" : "fan"),
                p.RawCode
            )).ToList();

            var requestedAttrs = new List<string>();
            if (text.Contains("spænding", StringComparison.OrdinalIgnoreCase) || text.Contains("voltage", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(text, @"\b\d+\s*(?:v|vac|vdc|volt)\b", RegexOptions.IgnoreCase))
                requestedAttrs.Add("voltage");
            if (text.Contains("strøm", StringComparison.OrdinalIgnoreCase) || text.Contains("current", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(text, @"\b\d+\s*(?:a|ma|amp|ampere)\b", RegexOptions.IgnoreCase))
                requestedAttrs.Add("current");
            if (text.Contains("effekt", StringComparison.OrdinalIgnoreCase) || text.Contains("power", StringComparison.OrdinalIgnoreCase) || text.Contains("watt", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(text, @"\b\d+\s*(?:w|kw|watt)\b", RegexOptions.IgnoreCase))
                requestedAttrs.Add("power");
            if (text.Contains("luft", StringComparison.OrdinalIgnoreCase) || text.Contains("airflow", StringComparison.OrdinalIgnoreCase) || text.Contains("m3/h", StringComparison.OrdinalIgnoreCase))
                requestedAttrs.Add("airflow");
            if (text.Contains("lyd", StringComparison.OrdinalIgnoreCase) || text.Contains("støj", StringComparison.OrdinalIgnoreCase) || text.Contains("sound", StringComparison.OrdinalIgnoreCase) || text.Contains("noise", StringComparison.OrdinalIgnoreCase) || text.Contains("db", StringComparison.OrdinalIgnoreCase))
                requestedAttrs.Add("sound_pressure");
            if (text.Contains("dimension", StringComparison.OrdinalIgnoreCase) || text.Contains("mål", StringComparison.OrdinalIgnoreCase) || text.Contains("diameter", StringComparison.OrdinalIgnoreCase) || text.Contains("størrelse", StringComparison.OrdinalIgnoreCase))
                requestedAttrs.Add("dimensions");
            if (text.Contains("modbus", StringComparison.OrdinalIgnoreCase) || text.Contains("0-10v", StringComparison.OrdinalIgnoreCase) || text.Contains("0-10 v", StringComparison.OrdinalIgnoreCase) || text.Contains("pwm", StringComparison.OrdinalIgnoreCase) || text.Contains("tacho", StringComparison.OrdinalIgnoreCase))
                requestedAttrs.Add("control_interface");

            result = new InputGovernorResult(
                Intent: intent,
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

    private static bool IsGreetingQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = text.Trim().TrimEnd('?', '!', '.', ',', ';', ':').Trim().ToLowerInvariant();

        if (clean is "hejsa" or "hej" or "hallo" or "dav" or "davs" or "goddag" or "godmorgen" or "godaften" or
                     "god dag" or "god morgen" or "god aften" or "hva så" or "hvad så" or
                     "hi" or "hello" or "hey" or "good morning" or "good afternoon" or "good evening" or "howdy" or "greetings" or
                     "yo" or "morn" or "mojn" or
                     "tak" or "mange tak" or "tusind tak" or "thanks" or "thank you" or "thx" or
                     "hvem er du" or "who are you" or "hvad kan du" or "hvad kan du hjælpe med" or "what can you do" or "what can you help with" or "help")
        {
            return true;
        }

        if (clean.Length <= 25 &&
            (clean.StartsWith("hejsa") || clean.StartsWith("hej ") || clean.StartsWith("hallo ") ||
             clean.StartsWith("hi ") || clean.StartsWith("hello ") || clean.StartsWith("hey ") ||
             clean.StartsWith("goddag") || clean.StartsWith("godmorgen") || clean.StartsWith("godaften")))
        {
            return true;
        }

        return false;
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
    Overview,
    Greeting
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
            "GREETING" => WorkflowType.Greeting,
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
