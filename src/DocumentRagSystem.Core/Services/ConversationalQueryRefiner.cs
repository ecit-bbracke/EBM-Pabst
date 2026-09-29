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
    private readonly ILlmClient _llmClient;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly ILogger<ConversationalQueryRefiner>? _logger;

    public ConversationalQueryRefiner(
        ILlmClient llmClient, 
        IEbmProductCodeParser? ebmParser = null,
        ILogger<ConversationalQueryRefiner>? logger = null)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
        _logger = logger;
    }

    public ConversationalQueryRefiner(
        ILlmClientResolver resolver,
        IEbmProductCodeParser? ebmParser = null,
        ILogger<ConversationalQueryRefiner>? logger = null)
        : this(resolver?.Resolve(LlmPurpose.QueryRefiner) ?? throw new ArgumentNullException(nameof(resolver)), ebmParser, logger)
    {
    }

    public ConversationalQueryRefiner(
        ILlmService llmService, 
        IEbmProductCodeParser? ebmParser = null,
        ILogger<ConversationalQueryRefiner>? logger = null)
        : this(new LlmServiceToClientAdapter(llmService), ebmParser, logger)
    {
    }

    private const string SystemInstruction = """
You are the Conversational Query Refiner. Resolve pronouns and maintain active constraints across turns.
Output MINIMAL compact JSON. Omit empty arrays and null fields.
Schema:
{
  "relationship_to_previous_turn": "CONTINUES | REFINES | MODIFIES | NEW_TOPIC | RESET",
  "resolved_question": "...",
  "effective_question": "...",
  "active_entities": [{"type": "fan", "name": "..."}],
  "active_constraints": [{"attribute": "...", "operator": "equals", "value": "..."}],
  "new_topic": false
}
Rules:
1. CONTINUES: same topic, no constraint changes.
2. REFINES: narrows scope / adds constraint.
3. MODIFIES: replaces old constraint with new one.
4. NEW_TOPIC: new subject.
5. RESET: start over.
6. Keep effective_question in the exact same language as latest user message.
""";

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
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var userPrompt = $"Conversation State: {serializedState}\nLatest User Message: {userMessage}";

        _logger?.LogInformation(
            "[QueryRefiner] Executing query refinement prompt ({PromptLength} chars) for conversation state (Turn: {TurnCount}).\nPrompt:\n{Prompt}",
            userPrompt.Length, conversationState?.TurnCount, userPrompt);

        try
        {
            var req = LlmRequest.FromPrompt(
                userPrompt, 
                requireJson: true, 
                systemInstruction: SystemInstruction, 
                temperature: 0.0, 
                maxOutputTokens: 160);

            var structuredRes = await _llmClient.GenerateStructuredAsync<QueryRefinementResult>(req);
            stopwatch.Stop();

            var result = structuredRes.Value;
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
