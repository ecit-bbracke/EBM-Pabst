using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DocumentRagSystem.Core.Models;

public static class TurnRelationship
{
    public const string Continues = "CONTINUES";
    public const string Refines = "REFINES";
    public const string Modifies = "MODIFIES";
    public const string NewTopic = "NEW_TOPIC";
    public const string Reset = "RESET";
}

public static class ConversationFactStatus
{
    public const string UserConstraint = "USER_CONSTRAINT";
    public const string RetrievedFact = "RETRIEVED_FACT";
    public const string DerivedFact = "DERIVED_FACT";
    public const string PreviousResult = "PREVIOUS_RESULT";
    public const string UnverifiedModelStatement = "UNVERIFIED_MODEL_STATEMENT";
}

public record ConversationEntity(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("entity_id")] string? EntityId = null
);

public record ConversationConstraint(
    [property: JsonPropertyName("attribute")] string Attribute,
    [property: JsonPropertyName("operator")] string Operator,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("unit")] string? Unit = null,
    [property: JsonPropertyName("source_turn")] int SourceTurn = 1,
    [property: JsonPropertyName("status")] string Status = ConversationFactStatus.UserConstraint
);

public record CandidateSet(
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("members")] List<string> Members,
    [property: JsonPropertyName("source_turn")] int SourceTurn = 1
);

public record ConversationFact(
    [property: JsonPropertyName("attribute")] string Attribute,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source_id")] string? SourceId = null,
    [property: JsonPropertyName("chunk_id")] string? ChunkId = null,
    [property: JsonPropertyName("source_turn")] int SourceTurn = 1,
    [property: JsonPropertyName("derivation_notes")] string? DerivationNotes = null,
    [property: JsonPropertyName("entity_name")] string? EntityName = null
);

public record ConversationState(
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("active_entities")] List<ConversationEntity> ActiveEntities,
    [property: JsonPropertyName("active_constraints")] List<ConversationConstraint> ActiveConstraints,
    [property: JsonPropertyName("candidate_set")] CandidateSet? CandidateSet = null,
    [property: JsonPropertyName("validated_facts")] List<ConversationFact>? ValidatedFacts = null,
    [property: JsonPropertyName("derived_facts")] List<ConversationFact>? DerivedFacts = null,
    [property: JsonPropertyName("unresolved_references")] List<string>? UnresolvedReferences = null,
    [property: JsonPropertyName("last_intent")] string? LastIntent = null,
    [property: JsonPropertyName("last_effective_question")] string? LastEffectiveQuestion = null,
    [property: JsonPropertyName("topic_version")] int TopicVersion = 1,
    [property: JsonPropertyName("turn_count")] int TurnCount = 0
)
{
    public static ConversationState CreateNew(string? conversationId = null, string topic = "general")
    {
        return new ConversationState(
            ConversationId: conversationId ?? Guid.NewGuid().ToString(),
            Topic: topic,
            ActiveEntities: new List<ConversationEntity>(),
            ActiveConstraints: new List<ConversationConstraint>(),
            CandidateSet: null,
            ValidatedFacts: new List<ConversationFact>(),
            DerivedFacts: new List<ConversationFact>(),
            UnresolvedReferences: new List<string>(),
            LastIntent: null,
            LastEffectiveQuestion: null,
            TopicVersion: 1,
            TurnCount: 0
        );
    }
}

public record ConstraintReplacement(
    [property: JsonPropertyName("old_attribute")] string OldAttribute,
    [property: JsonPropertyName("old_value")] string? OldValue,
    [property: JsonPropertyName("new_constraint")] ConversationConstraint NewConstraint
);

public record QueryRefinementResult(
    [property: JsonPropertyName("original_question")] string OriginalQuestion,
    [property: JsonPropertyName("relationship_to_previous_turn")] string RelationshipToPreviousTurn,
    [property: JsonPropertyName("resolved_question")] string ResolvedQuestion,
    [property: JsonPropertyName("effective_question")] string EffectiveQuestion,
    [property: JsonPropertyName("active_entities")] List<ConversationEntity> ActiveEntities,
    [property: JsonPropertyName("active_constraints")] List<ConversationConstraint> ActiveConstraints,
    [property: JsonPropertyName("candidate_set")] List<string>? CandidateSet = null,
    [property: JsonPropertyName("constraints_added")] List<ConversationConstraint>? ConstraintsAdded = null,
    [property: JsonPropertyName("constraints_removed")] List<ConversationConstraint>? ConstraintsRemoved = null,
    [property: JsonPropertyName("constraints_replaced")] List<ConstraintReplacement>? ConstraintsReplaced = null,
    [property: JsonPropertyName("references_resolved")] List<string>? ReferencesResolved = null,
    [property: JsonPropertyName("context_used")] List<int>? ContextUsed = null,
    [property: JsonPropertyName("clarification_required")] bool ClarificationRequired = false,
    [property: JsonPropertyName("clarification_reason")] string? ClarificationReason = null,
    [property: JsonPropertyName("new_topic")] bool NewTopic = false
);

public record ConversationContext(
    [property: JsonPropertyName("active_entities")] List<ConversationEntity>? ActiveEntities = null,
    [property: JsonPropertyName("active_constraints")] List<ConversationConstraint>? ActiveConstraints = null,
    [property: JsonPropertyName("candidate_set")] List<string>? CandidateSet = null,
    [property: JsonPropertyName("reusable_facts")] List<ConversationFact>? ReusableFacts = null
);

public record QueryRefinementTrace(
    [property: JsonPropertyName("relationship")] string Relationship,
    [property: JsonPropertyName("resolved_question")] string ResolvedQuestion,
    [property: JsonPropertyName("effective_question")] string EffectiveQuestion,
    [property: JsonPropertyName("constraints_added")] List<ConversationConstraint>? ConstraintsAdded = null,
    [property: JsonPropertyName("constraints_removed")] List<ConversationConstraint>? ConstraintsRemoved = null,
    [property: JsonPropertyName("constraints_replaced")] List<ConstraintReplacement>? ConstraintsReplaced = null,
    [property: JsonPropertyName("references_resolved")] List<string>? ReferencesResolved = null
);
