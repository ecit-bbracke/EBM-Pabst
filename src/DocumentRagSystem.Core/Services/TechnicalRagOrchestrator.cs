using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public interface ITechnicalRagOrchestrator
{
    Task<(string FinalAnswer, List<DocumentChunk> ContextChunks, ExecutionTrace Trace)> ProcessQueryAsync(
        string question,
        IVectorStore vectorStore
    );

    Task<(string FinalAnswer, List<DocumentChunk> ContextChunks, ExecutionTrace Trace)> ProcessQueryAsync(
        string question,
        IVectorStore vectorStore,
        string? conversationId,
        ConversationState? initialConversationState = null
    );
}

public class TechnicalRagOrchestrator : ITechnicalRagOrchestrator
{
    private readonly IInputGovernor _inputGovernor;
    private readonly IWorkflowRouter _workflowRouter;
    private readonly IEvidenceEvaluator _evidenceEvaluator;
    private readonly IAnswerComposer _answerComposer;
    private readonly IOutputGovernor _outputGovernor;
    private readonly ILlmService _llmService;
    private readonly IConversationalQueryRefiner _queryRefiner;
    private readonly IConversationStateStore _stateStore;

    // Workflow executors dictionary
    private readonly Dictionary<WorkflowType, IWorkflowExecutor> _executors;

    public int MaxRetrievalIterations { get; set; } = 3;
    public int MaxRegenerationAttempts { get; set; } = 2;

    public TechnicalRagOrchestrator(
        IInputGovernor inputGovernor,
        IWorkflowRouter workflowRouter,
        IEvidenceEvaluator evidenceEvaluator,
        IAnswerComposer answerComposer,
        IOutputGovernor outputGovernor,
        ILlmService llmService,
        IEbmProductCodeParser? ebmParser = null,
        IConversationalQueryRefiner? queryRefiner = null,
        IConversationStateStore? stateStore = null
    )
    {
        _inputGovernor = inputGovernor ?? throw new ArgumentNullException(nameof(inputGovernor));
        _workflowRouter = workflowRouter ?? throw new ArgumentNullException(nameof(workflowRouter));
        _evidenceEvaluator = evidenceEvaluator ?? throw new ArgumentNullException(nameof(evidenceEvaluator));
        _answerComposer = answerComposer ?? throw new ArgumentNullException(nameof(answerComposer));
        _outputGovernor = outputGovernor ?? throw new ArgumentNullException(nameof(outputGovernor));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));

        var parser = ebmParser ?? new EbmProductCodeParser();
        _queryRefiner = queryRefiner ?? new ConversationalQueryRefiner(llmService, parser);
        _stateStore = stateStore ?? new InMemoryConversationStateStore();

        _executors = new Dictionary<WorkflowType, IWorkflowExecutor>
        {
            { WorkflowType.SimpleRag, new SimpleWorkflowExecutor(llmService) },
            { WorkflowType.Comparison, new ComparisonWorkflowExecutor(llmService, parser) },
            { WorkflowType.Compatibility, new CompatibilityWorkflowExecutor(llmService, parser) },
            { WorkflowType.Diagnostic, new DiagnosticWorkflowExecutor(llmService) },
            { WorkflowType.Calculation, new CalculationWorkflowExecutor(llmService) },
            { WorkflowType.Design, new DesignWorkflowExecutor(llmService) },
            { WorkflowType.Clarification, new ClarificationWorkflowExecutor(llmService) }
        };
    }

    public Task<(string FinalAnswer, List<DocumentChunk> ContextChunks, ExecutionTrace Trace)> ProcessQueryAsync(
        string question,
        IVectorStore vectorStore
    )
    {
        return ProcessQueryAsync(question, vectorStore, null, null);
    }

    public async Task<(string FinalAnswer, List<DocumentChunk> ContextChunks, ExecutionTrace Trace)> ProcessQueryAsync(
        string question,
        IVectorStore vectorStore,
        string? conversationId,
        ConversationState? initialConversationState = null
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var effectiveConvId = conversationId ?? initialConversationState?.ConversationId ?? Guid.NewGuid().ToString();

        // 1. Load Conversation State
        var currentState = initialConversationState ?? await _stateStore.GetStateAsync(effectiveConvId) ?? ConversationState.CreateNew(effectiveConvId);
        var stateBefore = currentState;

        // 2. Run Conversational Query Refiner
        var refinement = await _queryRefiner.RefineQueryAsync(question, currentState);
        var effectiveQuestion = string.IsNullOrWhiteSpace(refinement.EffectiveQuestion) ? question : refinement.EffectiveQuestion;

        var refinementTrace = new QueryRefinementTrace(
            Relationship: refinement.RelationshipToPreviousTurn,
            ResolvedQuestion: refinement.ResolvedQuestion,
            EffectiveQuestion: effectiveQuestion,
            ConstraintsAdded: refinement.ConstraintsAdded,
            ConstraintsRemoved: refinement.ConstraintsRemoved,
            ConstraintsReplaced: refinement.ConstraintsReplaced,
            ReferencesResolved: refinement.ReferencesResolved
        );

        // 3. Handle Clarification if required by Query Refiner
        if (refinement.ClarificationRequired)
        {
            var clarificationGovResult = new InputGovernorResult(
                Intent: "CLARIFICATION",
                Confidence: 1.0,
                Entities: refinement.ActiveEntities.Select(e => new EntityInfo(e.Type, e.Name)).ToList(),
                RequestedAttributes: new List<string>(),
                Constraints: new List<string>(),
                ClarificationRequired: true,
                ClarificationReason: refinement.ClarificationReason ?? "Spørgsmålet er tvetydigt og kræver præcisering."
            );

            var clarificationExecutor = _executors[WorkflowType.Clarification];
            var (clarificationDraft, _, _) = await clarificationExecutor.ExecuteAsync(effectiveQuestion, clarificationGovResult, vectorStore);

            var clarificationTrace = new ExecutionTrace(
                Question: effectiveQuestion,
                InputGovernor: clarificationGovResult,
                Workflow: WorkflowType.Clarification.ToString(),
                Retrievals: new List<RetrievalTrace>(),
                Evidence: new List<EvidenceClaim>(),
                DraftAnswer: clarificationDraft,
                OutputGovernor: new OutputGovernorResult(Approved: true, Issues: new List<OutputGovernorIssue>(), Action: "APPROVE"),
                FinalAnswer: clarificationDraft,
                OriginalQuestion: question,
                ConversationStateBefore: stateBefore,
                QueryRefinement: refinementTrace,
                ConversationStateAfter: currentState
            );

            return (clarificationDraft, new List<DocumentChunk>(), clarificationTrace);
        }

        var conversationContext = new ConversationContext(
            ActiveEntities: refinement.ActiveEntities,
            ActiveConstraints: refinement.ActiveConstraints,
            CandidateSet: refinement.CandidateSet ?? currentState.CandidateSet?.Members,
            ReusableFacts: currentState.ValidatedFacts
        );

        var retrievals = new List<RetrievalTrace>();

        // 4. Input Governor with Effective Question + Conversational Context
        var govResult = await _inputGovernor.GovernInputAsync(effectiveQuestion, question, conversationContext);

        // 5. Workflow Router
        var workflowType = _workflowRouter.RouteWorkflow(govResult);
        var executor = _executors[workflowType];

        // 6. Execute Initial Workflow / First Retrieval
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(effectiveQuestion, govResult, vectorStore);

        retrievals.Add(new RetrievalTrace(
            Purpose: $"Initial retrieval for workflow: {workflowType}",
            Query: effectiveQuestion,
            Results: contextChunks.Select(c => c.Text).ToList()
        ));

        var allChunks = new List<DocumentChunk>(contextChunks);
        var currentDraft = draftResponse;
        List<EvidenceClaim> evidenceClaims = new();

        // 7. Iterative Retrieval & Evidence Evaluation Loop
        int iterations = 0;
        bool enoughEvidence = false;

        while (iterations < MaxRetrievalIterations && !enoughEvidence)
        {
            iterations++;

            // Evaluate Evidence
            evidenceClaims = await _evidenceEvaluator.EvaluateEvidenceAsync(effectiveQuestion, currentDraft, allChunks);

            // Check for missing/conflicting claims
            var missingClaims = evidenceClaims.Where(c => c.Status == "MISSING" || c.Status == "CONFLICTING").ToList();
            if (missingClaims.Count == 0)
            {
                enoughEvidence = true;
                break;
            }

            // Perform targeted retrieval for the first missing/conflicting claim
            var targetClaim = missingClaims[0];
            var targetedQueryPrompt = $"Given the effective question '{effectiveQuestion}' and the missing or conflicting technical claim '{targetClaim.Claim}', write a single, precise keyword query to search a vector store for relevant documentation to resolve or support this claim.";
            var targetedQuery = await _llmService.GenerateCompletionAsync(targetedQueryPrompt);

            var additionalChunks = await vectorStore.SearchAsync(targetedQuery);
            if (additionalChunks == null || !additionalChunks.Any())
            {
                // No more evidence can be found
                break;
            }

            // Merge additional chunks
            var initialCount = allChunks.Count;
            allChunks.AddRange(additionalChunks);
            allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

            retrievals.Add(new RetrievalTrace(
                Purpose: $"Iterative retrieval #{iterations} for claim: {targetClaim.Claim}",
                Query: targetedQuery,
                Results: additionalChunks.Select(c => c.Text).ToList()
            ));

            if (allChunks.Count == initialCount)
            {
                // No new chunks added
                break;
            }

            // Re-run draft response generation with updated/larger context chunks
            currentDraft = await _llmService.GenerateResponseAsync(effectiveQuestion, allChunks);
        }

        // 8. Answer Composer
        string composedAnswer = await _answerComposer.ComposeAnswerAsync(effectiveQuestion, govResult, evidenceClaims, allChunks);

        // 9. Output Governor Validation & Bounded Regeneration Loop
        int regenAttempts = 0;
        OutputGovernorResult? govOutputResult = null;
        string finalAnswer = composedAnswer;

        while (regenAttempts < MaxRegenerationAttempts)
        {
            govOutputResult = await _outputGovernor.ValidateOutputAsync(effectiveQuestion, finalAnswer, allChunks);

            if (govOutputResult.Approved || govOutputResult.Action == "APPROVE")
            {
                break;
            }

            if (govOutputResult.Action == "FAIL_SAFE")
            {
                finalAnswer = "Søgeresultaterne indeholder modstridende eller utilstrækkelige oplysninger til at kunne give et sikkert svar på dit spørgsmål. Kontakt venligst teknisk support.";
                break;
            }

            if (govOutputResult.Action == "REQUEST_CLARIFICATION")
            {
                finalAnswer = "Dit spørgsmål kræver yderligere præciseringer. Angiv venligst flere specifikationer eller modelnumre.";
                break;
            }

            // Otherwise, regenerate
            regenAttempts++;
            var issues = govOutputResult.Issues ?? new List<OutputGovernorIssue>();
            var issuesList = string.Join("\n", issues.Select(i => $"- [{i.Type}] {i.Claim} ({i.Severity})"));
            var regenPrompt = $$"""
                You are the Answer Composer. Your previous response was rejected by the Output Governor due to the following issues:
                {{issuesList}}

                Please rewrite the response, resolving all these issues. Ensure every claim is fully supported by the retrieved documentation.

                Original Question: {{effectiveQuestion}}
                Retrieved Context:
                {{string.Join("\n\n", allChunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}
                """;

            finalAnswer = await _llmService.GenerateCompletionAsync(regenPrompt, requireJson: false);
        }

        // 10. Update Conversation State (Committed upon approval)
        var stateAfter = UpdateConversationState(currentState, refinement, govResult, evidenceClaims, effectiveQuestion);
        await _stateStore.SaveStateAsync(effectiveConvId, stateAfter);

        var trace = new ExecutionTrace(
            Question: effectiveQuestion,
            InputGovernor: govResult,
            Workflow: workflowType.ToString(),
            Retrievals: retrievals,
            Evidence: evidenceClaims,
            DraftAnswer: currentDraft,
            OutputGovernor: govOutputResult,
            FinalAnswer: finalAnswer,
            OriginalQuestion: question,
            ConversationStateBefore: stateBefore,
            QueryRefinement: refinementTrace,
            ConversationStateAfter: stateAfter
        );

        return (finalAnswer, allChunks, trace);
    }

    private ConversationState UpdateConversationState(
        ConversationState currentState,
        QueryRefinementResult refinement,
        InputGovernorResult govResult,
        List<EvidenceClaim> evidenceClaims,
        string effectiveQuestion
    )
    {
        int newTurnCount = currentState.TurnCount + 1;

        if (refinement.RelationshipToPreviousTurn == TurnRelationship.NewTopic || refinement.NewTopic)
        {
            var newEntities = refinement.ActiveEntities.ToList();
            var newConstraints = refinement.ActiveConstraints.ToList();
            var newCandidateSet = refinement.CandidateSet != null && refinement.CandidateSet.Count > 0
                ? new CandidateSet("entity", refinement.CandidateSet, newTurnCount)
                : null;

            return new ConversationState(
                ConversationId: currentState.ConversationId,
                Topic: newEntities.FirstOrDefault()?.Name ?? "general",
                ActiveEntities: newEntities,
                ActiveConstraints: newConstraints,
                CandidateSet: newCandidateSet,
                ValidatedFacts: ExtractValidatedFacts(evidenceClaims, newEntities, newTurnCount),
                DerivedFacts: ExtractDerivedFacts(evidenceClaims, newEntities, newTurnCount),
                UnresolvedReferences: new List<string>(),
                LastIntent: govResult.Intent,
                LastEffectiveQuestion: effectiveQuestion,
                TopicVersion: currentState.TopicVersion + 1,
                TurnCount: newTurnCount
            );
        }

        if (refinement.RelationshipToPreviousTurn == TurnRelationship.Reset)
        {
            var newEntities = refinement.ActiveEntities.ToList();
            var newConstraints = refinement.ActiveConstraints.ToList();

            return new ConversationState(
                ConversationId: currentState.ConversationId,
                Topic: currentState.Topic,
                ActiveEntities: newEntities,
                ActiveConstraints: newConstraints,
                CandidateSet: null,
                ValidatedFacts: new List<ConversationFact>(),
                DerivedFacts: new List<ConversationFact>(),
                UnresolvedReferences: new List<string>(),
                LastIntent: govResult.Intent,
                LastEffectiveQuestion: effectiveQuestion,
                TopicVersion: currentState.TopicVersion + 1,
                TurnCount: newTurnCount
            );
        }

        // CONTINUES / REFINES / MODIFIES
        // 1. Merge active entities
        var mergedEntities = new List<ConversationEntity>(currentState.ActiveEntities);
        foreach (var entity in refinement.ActiveEntities)
        {
            if (!mergedEntities.Any(e => string.Equals(e.Name, entity.Name, StringComparison.OrdinalIgnoreCase)))
            {
                mergedEntities.Add(entity);
            }
        }

        // 2. Active constraints update (replacement, removal, addition)
        var updatedConstraints = new List<ConversationConstraint>(currentState.ActiveConstraints);

        // Remove constraints
        if (refinement.ConstraintsRemoved != null)
        {
            foreach (var removed in refinement.ConstraintsRemoved)
            {
                updatedConstraints.RemoveAll(c => string.Equals(c.Attribute, removed.Attribute, StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(c.Value, removed.Value, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Replace constraints
        if (refinement.ConstraintsReplaced != null)
        {
            foreach (var replacement in refinement.ConstraintsReplaced)
            {
                updatedConstraints.RemoveAll(c => string.Equals(c.Attribute, replacement.OldAttribute, StringComparison.OrdinalIgnoreCase) ||
                                                  (!string.IsNullOrWhiteSpace(replacement.OldValue) && string.Equals(c.Value, replacement.OldValue, StringComparison.OrdinalIgnoreCase)));
                if (replacement.NewConstraint != null && !updatedConstraints.Any(c => string.Equals(c.Attribute, replacement.NewConstraint.Attribute, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Value, replacement.NewConstraint.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    updatedConstraints.Add(replacement.NewConstraint with { SourceTurn = newTurnCount });
                }
            }
        }

        // Add added constraints
        if (refinement.ConstraintsAdded != null)
        {
            foreach (var added in refinement.ConstraintsAdded)
            {
                // If this is replacing an existing attribute with a new value, replace it
                updatedConstraints.RemoveAll(c => string.Equals(c.Attribute, added.Attribute, StringComparison.OrdinalIgnoreCase) && !string.Equals(c.Value, added.Value, StringComparison.OrdinalIgnoreCase));
                if (!updatedConstraints.Any(c => string.Equals(c.Attribute, added.Attribute, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Value, added.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    updatedConstraints.Add(added with { SourceTurn = newTurnCount });
                }
            }
        }

        // If refinement has explicit active constraints, ensure they are present
        foreach (var active in refinement.ActiveConstraints)
        {
            if (!updatedConstraints.Any(c => string.Equals(c.Attribute, active.Attribute, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Value, active.Value, StringComparison.OrdinalIgnoreCase)))
            {
                updatedConstraints.Add(active with { SourceTurn = newTurnCount });
            }
        }

        // 3. Candidate set update
        CandidateSet? updatedCandidateSet = currentState.CandidateSet;
        if (refinement.CandidateSet != null && refinement.CandidateSet.Count > 0)
        {
            updatedCandidateSet = new CandidateSet("entity", refinement.CandidateSet, newTurnCount);
        }

        // 4. Validated and Derived facts (bounded to recent 20)
        var newValidated = ExtractValidatedFacts(evidenceClaims, mergedEntities, newTurnCount);
        var mergedValidated = new List<ConversationFact>(currentState.ValidatedFacts ?? new List<ConversationFact>());
        foreach (var fact in newValidated)
        {
            if (!mergedValidated.Any(f => string.Equals(f.Attribute, fact.Attribute, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Value, fact.Value, StringComparison.OrdinalIgnoreCase)))
            {
                mergedValidated.Add(fact);
            }
        }
        if (mergedValidated.Count > 20)
        {
            mergedValidated = mergedValidated.TakeLast(20).ToList();
        }

        var newDerived = ExtractDerivedFacts(evidenceClaims, mergedEntities, newTurnCount);
        var mergedDerived = new List<ConversationFact>(currentState.DerivedFacts ?? new List<ConversationFact>());
        foreach (var fact in newDerived)
        {
            if (!mergedDerived.Any(f => string.Equals(f.Attribute, fact.Attribute, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Value, fact.Value, StringComparison.OrdinalIgnoreCase)))
            {
                mergedDerived.Add(fact);
            }
        }
        if (mergedDerived.Count > 10)
        {
            mergedDerived = mergedDerived.TakeLast(10).ToList();
        }

        return new ConversationState(
            ConversationId: currentState.ConversationId,
            Topic: currentState.Topic,
            ActiveEntities: mergedEntities,
            ActiveConstraints: updatedConstraints,
            CandidateSet: updatedCandidateSet,
            ValidatedFacts: mergedValidated,
            DerivedFacts: mergedDerived,
            UnresolvedReferences: new List<string>(),
            LastIntent: govResult.Intent,
            LastEffectiveQuestion: effectiveQuestion,
            TopicVersion: currentState.TopicVersion,
            TurnCount: newTurnCount
        );
    }

    private static List<ConversationFact> ExtractValidatedFacts(
        List<EvidenceClaim> evidenceClaims,
        List<ConversationEntity> activeEntities,
        int turnNumber
    )
    {
        var facts = new List<ConversationFact>();
        if (evidenceClaims == null) return facts;

        foreach (var claim in evidenceClaims.Where(c => c.Status == "SUPPORTED"))
        {
            facts.Add(new ConversationFact(
                Attribute: claim.Claim,
                Value: claim.Claim,
                Status: ConversationFactStatus.RetrievedFact,
                SourceId: claim.Sources?.FirstOrDefault(),
                ChunkId: null,
                SourceTurn: turnNumber,
                DerivationNotes: claim.Reason,
                EntityName: activeEntities.FirstOrDefault()?.Name
            ));
        }

        return facts;
    }

    private static List<ConversationFact> ExtractDerivedFacts(
        List<EvidenceClaim> evidenceClaims,
        List<ConversationEntity> activeEntities,
        int turnNumber
    )
    {
        var facts = new List<ConversationFact>();
        if (evidenceClaims == null) return facts;

        foreach (var claim in evidenceClaims.Where(c => c.Status == "DERIVED"))
        {
            facts.Add(new ConversationFact(
                Attribute: claim.Claim,
                Value: claim.Claim,
                Status: ConversationFactStatus.DerivedFact,
                SourceId: claim.Sources?.FirstOrDefault(),
                ChunkId: null,
                SourceTurn: turnNumber,
                DerivationNotes: claim.Reason,
                EntityName: activeEntities.FirstOrDefault()?.Name
            ));
        }

        return facts;
    }
}

public class AnswerComposerException : Exception
{
    public AnswerComposerException(string message) : base(message) { }
}

