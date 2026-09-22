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

public class ConversationalQueryRefiner : IConversationalQueryRefiner
{
    private readonly ILlmService _llmService;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly ILogger<ConversationalQueryRefiner>? _logger;

    public ConversationalQueryRefiner(
        ILlmService llmService, 
        IEbmProductCodeParser? ebmParser = null,
        ILogger<ConversationalQueryRefiner>? logger = null)
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
        _logger = logger;
    }

    public async Task<QueryRefinementResult> RefineQueryAsync(
        string userMessage,
        ConversationState? conversationState
    )
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            throw new ArgumentNullException(nameof(userMessage));

        var stopwatch = Stopwatch.StartNew();
        var extractedProducts = _ebmParser.ExtractProductsFromText(userMessage);

        // If no prior conversation state or no active context, return clean single-turn refinement
        bool hasPriorContext = conversationState != null && 
            (conversationState.TurnCount > 0 || 
             conversationState.ActiveEntities.Count > 0 || 
             conversationState.ActiveConstraints.Count > 0 || 
             conversationState.CandidateSet != null || 
             !string.IsNullOrWhiteSpace(conversationState.LastEffectiveQuestion));

        if (!hasPriorContext)
        {
            var initialEntities = extractedProducts.Select(p => new ConversationEntity(
                p.FanFamily == FanFamilyType.Axial ? "axial_fan" : "fan",
                p.RawCode
            )).ToList();

            stopwatch.Stop();
            _logger?.LogInformation(
                "[QueryRefiner] Single-turn query detected (no prior context). Completed in {DurationMs}ms.",
                stopwatch.ElapsedMilliseconds);

            return new QueryRefinementResult(
                OriginalQuestion: userMessage,
                RelationshipToPreviousTurn: TurnRelationship.NewTopic,
                ResolvedQuestion: userMessage,
                EffectiveQuestion: userMessage,
                ActiveEntities: initialEntities,
                ActiveConstraints: new List<ConversationConstraint>(),
                CandidateSet: null,
                ConstraintsAdded: new List<ConversationConstraint>(),
                ConstraintsRemoved: new List<ConversationConstraint>(),
                ConstraintsReplaced: new List<ConstraintReplacement>(),
                ReferencesResolved: new List<string>(),
                ContextUsed: new List<int>(),
                ClarificationRequired: false,
                ClarificationReason: null,
                NewTopic: true
            );
        }

        var serializedState = JsonSerializer.Serialize(conversationState, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var prompt = $$"""
            You are the Conversational Query Refiner for a technical RAG system.
            Your job is to determine what the latest user message means in the context of the active conversation.
            You must NOT answer the technical question, do calculations, invent facts, or choose downstream execution workflows.
            Your sole job is query refinement and structured conversation state resolution.

            Core Principles:
            1. Preserve explicit user requirements.
            2. Carry forward previous constraints only when still relevant.
            3. Do NOT carry constraints across topic changes.
            4. Resolve references ("it", "that", "those", "them", "the first one", "the other one", "that controller", "those devices", "the remaining model", etc.) only when sufficiently clear.
            5. If an important reference is ambiguous (e.g. multiple plausible candidate entities and unclear which one is referred to), request clarification.
            6. Distinguish between:
               - CONTINUES: Refers to the same technical topic without materially changing constraints.
               - REFINES: Adds another requirement or narrows the current scope/candidate set.
               - MODIFIES: Changes (replaces) or removes an existing constraint (e.g., "What if we use 48 V instead?", "Ignore the IP67 requirement").
               - NEW_TOPIC: Changes to a different technical subject. Previous unrelated constraints must NOT leak into the new topic.
               - RESET: Explicitly asks to discard previous filtering/context (e.g., "Start over. Which controllers support PROFINET?").
            7. Constraint Replacement vs Addition:
               - If user specifies an alternative value for an existing constraint (e.g. "What about 48 V instead?" when supply voltage was 24 V), REPLACE the old constraint. Do NOT keep both unless the user asks for alternatives.
            8. Constraint Removal:
               - If user asks to ignore or remove a constraint (e.g. "Ignore IP67"), REMOVE it from active constraints.
            9. Candidate Sets:
               - Maintain and narrow candidate sets across turns.
            10. Build:
               - `resolved_question`: The user message with pronouns/references replaced with exact entity names.
               - `effective_question`: A fully self-contained, unambiguous technical question combining active entities, candidate set, and all still-active constraints.
            11. CRITICAL LANGUAGE RULE:
               - Keep `resolved_question`, `effective_question`, and `clarification_reason` in the EXACT same language as the user's latest message (e.g. English if English, Danish if Danish, German if German).

            Current Conversation State:
            {{serializedState}}

            Latest User Message:
            {{userMessage}}

            Return a JSON object conforming strictly to this schema:
            {
              "original_question": "Exact latest user message",
              "relationship_to_previous_turn": "CONTINUES | REFINES | MODIFIES | NEW_TOPIC | RESET",
              "resolved_question": "Question with references resolved",
              "effective_question": "Self-contained effective technical question with all active constraints and entities",
              "active_entities": [
                {
                  "type": "controller | fan | sensor | device | etc",
                  "name": "Entity name",
                  "role": "candidate | target | reference",
                  "entity_id": null
                }
              ],
              "active_constraints": [
                {
                  "attribute": "attribute_name (e.g. supply_voltage, protocol, ingress_protection, diameter)",
                  "operator": "supports | equals | equals_or_exceeds | <= | >=",
                  "value": "constraint value (e.g. Modbus TCP, 24 VDC, IP67)",
                  "unit": "VDC | mm | null",
                  "source_turn": 1,
                  "status": "USER_CONSTRAINT"
                }
              ],
              "candidate_set": ["List of candidate entity names currently in scope"],
              "constraints_added": [
                {
                  "attribute": "...",
                  "operator": "...",
                  "value": "...",
                  "unit": null,
                  "source_turn": 1,
                  "status": "USER_CONSTRAINT"
                }
              ],
              "constraints_removed": [
                {
                  "attribute": "...",
                  "operator": "...",
                  "value": "..."
                }
              ],
              "constraints_replaced": [
                {
                  "old_attribute": "supply_voltage",
                  "old_value": "24 VDC",
                  "new_constraint": {
                    "attribute": "supply_voltage",
                    "operator": "supports",
                    "value": "48 VDC",
                    "unit": "VDC",
                    "source_turn": 2,
                    "status": "USER_CONSTRAINT"
                  }
                }
              ],
              "references_resolved": ["that -> Controller C", "those -> Controller A, Controller B"],
              "context_used": [1, 2],
              "clarification_required": false,
              "clarification_reason": null,
              "new_topic": false
            }
            """;

        _logger?.LogInformation(
            "[QueryRefiner] Executing query refinement prompt ({PromptLength} chars) for conversation state (Turn: {TurnCount}).\nPrompt:\n{Prompt}",
            prompt.Length, conversationState?.TurnCount, prompt);

        try
        {
            var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
            stopwatch.Stop();

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

            var result = JsonSerializer.Deserialize<QueryRefinementResult>(cleanedResponse, options);
            if (result != null)
            {
                _logger?.LogInformation(
                    "[QueryRefiner] Completed in {DurationMs}ms. Relationship: {Relationship}, EffectiveQuestion: \"{EffectiveQuestion}\", ClarificationRequired: {ClarificationRequired}.",
                    stopwatch.ElapsedMilliseconds, result.RelationshipToPreviousTurn, result.EffectiveQuestion, result.ClarificationRequired);
                // Ensure entities contain extracted ebm products if any
                var activeEntities = result.ActiveEntities ?? new List<ConversationEntity>();
                foreach (var prod in extractedProducts)
                {
                    if (!activeEntities.Any(e => string.Equals(e.Name, prod.RawCode, StringComparison.OrdinalIgnoreCase) || 
                                                 string.Equals(e.Name, prod.CleanCode, StringComparison.OrdinalIgnoreCase)))
                    {
                        var entityType = prod.FanFamily switch
                        {
                            FanFamilyType.Axial => "axial_fan",
                            FanFamilyType.Centrifugal => "centrifugal_fan",
                            _ => "fan"
                        };
                        activeEntities.Add(new ConversationEntity(entityType, prod.RawCode));
                    }
                }

                var activeConstraints = result.ActiveConstraints ?? new List<ConversationConstraint>();
                var constraintsAdded = result.ConstraintsAdded ?? new List<ConversationConstraint>();
                var constraintsRemoved = result.ConstraintsRemoved ?? new List<ConversationConstraint>();
                var constraintsReplaced = result.ConstraintsReplaced ?? new List<ConstraintReplacement>();
                var referencesResolved = result.ReferencesResolved ?? new List<string>();
                var contextUsed = result.ContextUsed ?? new List<int>();

                // If relationship is NEW_TOPIC or RESET, ensure old constraints/entities don't unintentionally persist
                if (result.RelationshipToPreviousTurn == TurnRelationship.NewTopic || result.NewTopic)
                {
                    return result with
                    {
                        OriginalQuestion = userMessage,
                        ResolvedQuestion = string.IsNullOrWhiteSpace(result.ResolvedQuestion) ? userMessage : result.ResolvedQuestion,
                        EffectiveQuestion = string.IsNullOrWhiteSpace(result.EffectiveQuestion) ? userMessage : result.EffectiveQuestion,
                        ActiveEntities = activeEntities,
                        ActiveConstraints = activeConstraints,
                        ConstraintsAdded = constraintsAdded,
                        ConstraintsRemoved = constraintsRemoved,
                        ConstraintsReplaced = constraintsReplaced,
                        ReferencesResolved = referencesResolved,
                        ContextUsed = contextUsed,
                        NewTopic = true
                    };
                }

                if (result.RelationshipToPreviousTurn == TurnRelationship.Reset)
                {
                    return result with
                    {
                        OriginalQuestion = userMessage,
                        ResolvedQuestion = string.IsNullOrWhiteSpace(result.ResolvedQuestion) ? userMessage : result.ResolvedQuestion,
                        EffectiveQuestion = string.IsNullOrWhiteSpace(result.EffectiveQuestion) ? userMessage : result.EffectiveQuestion,
                        ActiveEntities = activeEntities,
                        ActiveConstraints = constraintsAdded,
                        CandidateSet = null,
                        ConstraintsAdded = constraintsAdded,
                        ConstraintsRemoved = constraintsRemoved,
                        ConstraintsReplaced = constraintsReplaced,
                        ReferencesResolved = referencesResolved,
                        ContextUsed = contextUsed,
                        NewTopic = false
                    };
                }

                return result with
                {
                    OriginalQuestion = userMessage,
                    ResolvedQuestion = string.IsNullOrWhiteSpace(result.ResolvedQuestion) ? userMessage : result.ResolvedQuestion,
                    EffectiveQuestion = string.IsNullOrWhiteSpace(result.EffectiveQuestion) ? userMessage : result.EffectiveQuestion,
                    ActiveEntities = activeEntities,
                    ActiveConstraints = activeConstraints,
                    ConstraintsAdded = constraintsAdded,
                    ConstraintsRemoved = constraintsRemoved,
                    ConstraintsReplaced = constraintsReplaced,
                    ReferencesResolved = referencesResolved,
                    ContextUsed = contextUsed
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in ConversationalQueryRefiner: {ex.Message}. Falling back to default.");
        }

        // Safe fallback
        return new QueryRefinementResult(
            OriginalQuestion: userMessage,
            RelationshipToPreviousTurn: TurnRelationship.Continues,
            ResolvedQuestion: userMessage,
            EffectiveQuestion: userMessage,
            ActiveEntities: extractedProducts.Select(p => new ConversationEntity(p.FanFamily == FanFamilyType.Axial ? "axial_fan" : "fan", p.RawCode)).ToList(),
            ActiveConstraints: conversationState?.ActiveConstraints ?? new List<ConversationConstraint>(),
            CandidateSet: conversationState?.CandidateSet?.Members,
            ConstraintsAdded: new List<ConversationConstraint>(),
            ConstraintsRemoved: new List<ConversationConstraint>(),
            ConstraintsReplaced: new List<ConstraintReplacement>(),
            ReferencesResolved: new List<string>(),
            ContextUsed: new List<int>(),
            ClarificationRequired: false,
            ClarificationReason: null,
            NewTopic: false
        );
    }
}
