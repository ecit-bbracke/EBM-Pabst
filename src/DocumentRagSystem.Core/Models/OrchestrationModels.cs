using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DocumentRagSystem.Core.Models;

public record EntityInfo(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name
);

public record InputGovernorResult(
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("entities")] List<EntityInfo> Entities,
    [property: JsonPropertyName("requested_attributes")] List<string> RequestedAttributes,
    [property: JsonPropertyName("constraints")] List<string> Constraints,
    [property: JsonPropertyName("clarification_required")] bool ClarificationRequired,
    [property: JsonPropertyName("clarification_reason")] string? ClarificationReason,
    [property: JsonPropertyName("parsed_ebm_products")] List<EbmProductInfo>? ParsedEbmProducts = null
);

public record AttributeValue(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("source")] string Source
);

public record EntityComparisonData(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("attributes")] Dictionary<string, AttributeValue> Attributes
);

public record ComparisonResult(
    [property: JsonPropertyName("entities")] Dictionary<string, EntityComparisonData> Entities,
    [property: JsonPropertyName("ebm_evaluation")] EbmComparisonEvaluation? EbmEvaluation = null
);

public record CompatibilityCheck(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("source_value")] string SourceValue,
    [property: JsonPropertyName("target_requirement")] string TargetRequirement,
    [property: JsonPropertyName("status")] string Status, // COMPATIBLE, INCOMPATIBLE, CONDITIONAL, UNKNOWN
    [property: JsonPropertyName("reason")] string Reason
);

public record CompatibilityResult(
    [property: JsonPropertyName("checks")] List<CompatibilityCheck> Checks,
    [property: JsonPropertyName("status")] string Status, // COMPATIBLE, INCOMPATIBLE, CONDITIONAL, UNKNOWN
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("ebm_evaluation")] EbmComparisonEvaluation? EbmEvaluation = null
);

public record CandidateCause(
    [property: JsonPropertyName("cause")] string Cause,
    [property: JsonPropertyName("status")] string Status, // SUPPORTED, DERIVED, UNSUPPORTED
    [property: JsonPropertyName("evidence")] List<string> Evidence,
    [property: JsonPropertyName("check")] string Check,
    [property: JsonPropertyName("expected_result")] string ExpectedResult
);

public record DiagnosticResult(
    [property: JsonPropertyName("causes")] List<CandidateCause> Causes,
    [property: JsonPropertyName("troubleshooting_steps")] List<string> TroubleshootingSteps
);

public record EvidenceClaim(
    [property: JsonPropertyName("claim")] string Claim,
    [property: JsonPropertyName("status")] string Status, // SUPPORTED, DERIVED, CONFLICTING, MISSING
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("sources")] List<string> Sources
);

public record OutputGovernorIssue(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("claim")] string Claim,
    [property: JsonPropertyName("severity")] string Severity
);

public record OutputGovernorResult(
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("issues")] List<OutputGovernorIssue> Issues,
    [property: JsonPropertyName("action")] string Action // APPROVE, REGENERATE, RETRIEVE_MORE, REQUEST_CLARIFICATION, FAIL_SAFE
);

public record ExecutionTrace(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("input_governor")] InputGovernorResult? InputGovernor,
    [property: JsonPropertyName("workflow")] string Workflow,
    [property: JsonPropertyName("retrievals")] List<RetrievalTrace> Retrievals,
    [property: JsonPropertyName("evidence")] List<EvidenceClaim> Evidence,
    [property: JsonPropertyName("draft_answer")] string DraftAnswer,
    [property: JsonPropertyName("output_governor")] OutputGovernorResult? OutputGovernor,
    [property: JsonPropertyName("final_answer")] string FinalAnswer,
    [property: JsonPropertyName("original_question")] string? OriginalQuestion = null,
    [property: JsonPropertyName("conversation_state_before")] ConversationState? ConversationStateBefore = null,
    [property: JsonPropertyName("query_refinement")] QueryRefinementTrace? QueryRefinement = null,
    [property: JsonPropertyName("conversation_state_after")] ConversationState? ConversationStateAfter = null
);

public record RetrievalTrace(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("results")] List<string> Results
);
