using System;
using System.Text.Json;
using System.Threading.Tasks;
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

    public InputGovernor(ILlmService llmService, IEbmProductCodeParser? ebmParser = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
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

        var combinedText = string.IsNullOrWhiteSpace(originalQuestion) || string.Equals(effectiveQuestion, originalQuestion, StringComparison.OrdinalIgnoreCase)
            ? effectiveQuestion
            : $"{effectiveQuestion} {originalQuestion}";

        var extractedProducts = _ebmParser.ExtractProductsFromText(combinedText);

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

        try
        {
            var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
            
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

                if (extractedProducts.Count >= 2 && (result.Intent == "COMPARISON" || result.Intent == "COMPATIBILITY" || result.Intent == "SPEC_LOOKUP"))
                {
                    if (!requestedAttrs.Contains("airflow_direction"))
                        requestedAttrs.Add("airflow_direction");
                    if (!requestedAttrs.Contains("technology"))
                        requestedAttrs.Add("technology");
                }

                return result with 
                { 
                    Entities = entities, 
                    RequestedAttributes = requestedAttrs,
                    ParsedEbmProducts = extractedProducts 
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in InputGovernor: {ex.Message}. Falling back to default.");
        }

        // Safe fallback
        var fallbackEntities = extractedProducts.Select(p => new EntityInfo(p.FanFamily == FanFamilyType.Axial ? "axial_fan" : "fan", p.RawCode)).ToList();
        return new InputGovernorResult(
            Intent: extractedProducts.Count >= 2 ? "COMPARISON" : "SPEC_LOOKUP",
            Confidence: 0.5,
            Entities: fallbackEntities,
            RequestedAttributes: new(),
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: extractedProducts
        );
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
    Clarification
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
            _ => WorkflowType.SimpleRag
        };
    }
}
