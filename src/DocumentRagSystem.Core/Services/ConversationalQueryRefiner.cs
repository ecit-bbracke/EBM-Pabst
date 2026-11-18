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

public class ConversationalQueryRefiner : IConversationalQueryRefiner
{
    private readonly ILlmClient _llmClient;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly ILogger<ConversationalQueryRefiner>? _logger;

    private static readonly HashSet<int> KnownEbmDiameters = new(DimensionalQueryExpander.KnownEbmDimensions);

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
6. Keep effective_question in the exact same language as latest user message (if language cannot be detected, use Danish).
7. DIMENSIONAL & COLLOQUIAL MAPPING: When users specify approximate, colloquial, or non-standard fan dimensions (such as 120mm, 121mm, 118mm, 120x120) or colloquial terms ('computerblæser', 'kabinetblæser', 'PC fan'), map and expand the effective_question to include the official ebm-papst standard dimensions (119 mm / 119x119 mm) and standard product category ('axial compact fan' / 'kompaktblæser', series 4400/4300).
8. ENGINEERING UNITS & OPERATIONAL PARAMETERS: When users specify airflow in non-metric or alternative units like CFM or l/s, convert and include both the original unit and standard metric m³/h (e.g. 100 CFM ≈ 170 m³/h; 50 l/s = 180 m³/h). When users specify pressure in mmVS, mm H2O, or in. wg, include the Pascal (Pa) equivalent (10 mmVS ≈ 100 Pa; 1 in. wg ≈ 250 Pa). When harsh, wet, or outdoor environments are mentioned, include IP54/IP68.
9. DANISH HVAC & INDUSTRIAL JARGON: When users use Danish industry expressions (e.g. 'pillefyr/røggasblæser', 'tavleblæser', 'sugende/trykkende blæser', 'kraftstrøm', 'emhætteblæser', 'kølemøbelblæser', 'vægramme', 'trinløs styring'), understand and translate the application context into standard ebm-papst engineering terms (e.g. 'flue gas blower R2E/G2E', 'control cabinet cooling', 'airflow direction V (suction) / A (blowing)', '400V 3-phase', 'wall ring W-series', '0-10V/PWM stepless EC control') in the effective_question.
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

            var initialConstraints = new List<ConversationConstraint>();
            var voltageFilter = ElectricalQueryExpander.ExtractVoltageFilter(userMessage);
            if (voltageFilter != null)
            {
                initialConstraints.Add(new ConversationConstraint(
                    Attribute: "voltage",
                    Operator: "equals",
                    Value: voltageFilter.RawMatch,
                    Unit: voltageFilter.VoltageType,
                    SourceTurn: 1,
                    Status: "USER_CONSTRAINT"
                ));
            }

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
                ActiveConstraints: initialConstraints,
                CandidateSet: null,
                ConstraintsAdded: initialConstraints,
                ConstraintsRemoved: new List<ConversationConstraint>(),
                ConstraintsReplaced: new List<ConstraintReplacement>(),
                ReferencesResolved: new List<string>(),
                ContextUsed: new List<int>(),
                ClarificationRequired: false,
                ClarificationReason: null,
                NewTopic: true
            );
        }

        // Fast-path: Follow-up diameter refinement (e.g. "Ø280", "280", "280 mm", "Ø560", "121 mm")
        var cleanMsg = userMessage.Trim();
        var shortDiamMatch = Regex.Match(cleanMsg, @"^(?:Ø\s*|\b)(1[0-9]{2}|[2-9][0-9]{2})(?:\s*mm)?$", RegexOptions.IgnoreCase);
        if (conversationState != null && shortDiamMatch.Success && int.TryParse(shortDiamMatch.Groups[1].Value, out var targetDiam))
        {
            var resolved = DimensionalQueryExpander.ResolveNearestStandardSize(targetDiam, maxToleranceMm: 3);
            int standardDiam = resolved?.StandardSize ?? targetDiam;
            bool hasExplicitDiameterUnit = cleanMsg.Contains('Ø') || cleanMsg.Contains("mm", StringComparison.OrdinalIgnoreCase);
            bool isKnownEbmDiameter = KnownEbmDiameters.Contains(targetDiam) || resolved != null;

            if (hasExplicitDiameterUnit || isKnownEbmDiameter)
            {
                var baseQuestion = !string.IsNullOrWhiteSpace(conversationState.LastEffectiveQuestion)
                    ? conversationState.LastEffectiveQuestion
                    : "hvilke produkter kan vi tilbyde";

                var effectiveQ = (standardDiam != targetDiam)
                    ? $"{baseQuestion} på Ø{standardDiam} mm (standard for {targetDiam} mm)"
                    : $"{baseQuestion} på Ø{targetDiam} mm";

                var diamConstraint = new ConversationConstraint(
                    Attribute: "diameter",
                    Operator: "equals",
                    Value: $"{standardDiam} mm",
                    Unit: "mm",
                    SourceTurn: conversationState.TurnCount + 1,
                    Status: "USER_CONSTRAINT"
                );

                var combinedConstraints = new List<ConversationConstraint>(conversationState.ActiveConstraints);
                combinedConstraints.RemoveAll(c => c.Attribute.Equals("diameter", StringComparison.OrdinalIgnoreCase));
                combinedConstraints.Add(diamConstraint);

                stopwatch.Stop();
                _logger?.LogInformation(
                    "[QueryRefiner] Fast-path short diameter refinement detected ({Diameter} mm). Effective question: \"{EffectiveQuestion}\".",
                    targetDiam, effectiveQ);

                return new QueryRefinementResult(
                    OriginalQuestion: userMessage,
                    RelationshipToPreviousTurn: TurnRelationship.Refines,
                    ResolvedQuestion: effectiveQ,
                    EffectiveQuestion: effectiveQ,
                    ActiveEntities: conversationState.ActiveEntities,
                    ActiveConstraints: combinedConstraints,
                    CandidateSet: null,
                    ConstraintsAdded: new List<ConversationConstraint> { diamConstraint },
                    ConstraintsRemoved: new List<ConversationConstraint>(),
                    ConstraintsReplaced: new List<ConstraintReplacement>(),
                    ReferencesResolved: new List<string> { $"{userMessage.Trim()} -> diameter Ø{targetDiam} mm" },
                    ContextUsed: new List<int> { conversationState.TurnCount },
                    ClarificationRequired: false,
                    ClarificationReason: null,
                    NewTopic: false
                );
            }
        }

        // Fast-path: If user explicitly provided a new distinct ebm-papst product model and does not use ambiguous pronouns
        if (extractedProducts.Count > 0)
        {
            var msgLower = userMessage.ToLowerInvariant();
            var hasPronouns = msgLower.Contains(" den ") || msgLower.EndsWith(" den") ||
                              msgLower.Contains(" denne ") || msgLower.Contains(" dette ") ||
                              msgLower.Contains(" it ") || msgLower.Contains(" its ") ||
                              msgLower.Contains(" that ") || msgLower.Contains(" them ") ||
                              msgLower.Contains(" those ") || msgLower.Contains(" previous ");

            if (!hasPronouns)
            {
                var newEntities = extractedProducts.Select(p => new ConversationEntity(
                    p.FanFamily == FanFamilyType.Axial ? "axial_fan" : "fan",
                    p.RawCode
                )).ToList();

                stopwatch.Stop();
                _logger?.LogInformation(
                    "[QueryRefiner] Fast-path explicit product query detected without ambiguous pronouns. Completed in {DurationMs}ms.",
                    stopwatch.ElapsedMilliseconds);

                return new QueryRefinementResult(
                    OriginalQuestion: userMessage,
                    RelationshipToPreviousTurn: TurnRelationship.NewTopic,
                    ResolvedQuestion: userMessage,
                    EffectiveQuestion: userMessage,
                    ActiveEntities: newEntities,
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
                maxOutputTokens: 400);

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
