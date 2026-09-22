using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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

    async IAsyncEnumerable<OrchestrationStreamEvent> ProcessQueryStreamAsync(
        string question,
        IVectorStore vectorStore,
        string? conversationId,
        ConversationState? initialConversationState = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var (finalAnswer, chunks, trace) = await ProcessQueryAsync(question, vectorStore, conversationId, initialConversationState);
        yield return new OrchestrationStreamEvent("citations", null, chunks, trace.ConversationStateAfter?.ConversationId ?? conversationId);
        yield return new OrchestrationStreamEvent("chunk", finalAnswer);
        yield return new OrchestrationStreamEvent("done", null, chunks, trace.ConversationStateAfter?.ConversationId ?? conversationId, trace);
    }
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
    private readonly ILogger<TechnicalRagOrchestrator>? _logger;

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
        IConversationStateStore? stateStore = null,
        IDocumentRepository? documentRepository = null,
        ILogger<TechnicalRagOrchestrator>? logger = null
    )
    {
        _inputGovernor = inputGovernor ?? throw new ArgumentNullException(nameof(inputGovernor));
        _workflowRouter = workflowRouter ?? throw new ArgumentNullException(nameof(workflowRouter));
        _evidenceEvaluator = evidenceEvaluator ?? throw new ArgumentNullException(nameof(evidenceEvaluator));
        _answerComposer = answerComposer ?? throw new ArgumentNullException(nameof(answerComposer));
        _outputGovernor = outputGovernor ?? throw new ArgumentNullException(nameof(outputGovernor));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _logger = logger;

        var parser = ebmParser ?? new EbmProductCodeParser();
        _queryRefiner = queryRefiner ?? new ConversationalQueryRefiner(llmService, parser);
        _stateStore = stateStore ?? new InMemoryConversationStateStore();

        _executors = new Dictionary<WorkflowType, IWorkflowExecutor>
        {
            { WorkflowType.SimpleRag, new SimpleWorkflowExecutor(llmService) },
            { WorkflowType.Comparison, new ComparisonWorkflowExecutor(llmService, parser) },
            { WorkflowType.Compatibility, new CompatibilityWorkflowExecutor(llmService, parser, documentRepository) },
            { WorkflowType.Diagnostic, new DiagnosticWorkflowExecutor(llmService) },
            { WorkflowType.Calculation, new CalculationWorkflowExecutor(llmService) },
            { WorkflowType.Design, new DesignWorkflowExecutor(llmService) },
            { WorkflowType.Clarification, new ClarificationWorkflowExecutor(llmService) },
            { WorkflowType.Overview, new OverviewWorkflowExecutor(llmService, parser, documentRepository) }
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

        var totalStopwatch = Stopwatch.StartNew();
        var stageStopwatch = Stopwatch.StartNew();
        var timings = new Dictionary<string, long>();
        var promptTraces = new List<PromptTrace>();

        var effectiveConvId = conversationId ?? initialConversationState?.ConversationId ?? Guid.NewGuid().ToString();
        _logger?.LogInformation("Starting RAG query processing for question: \"{Question}\" (ConversationId: {ConversationId})", question, effectiveConvId);

        // 1. Load Conversation State
        var currentState = initialConversationState ?? await _stateStore.GetStateAsync(effectiveConvId) ?? ConversationState.CreateNew(effectiveConvId);
        var stateBefore = currentState;
        timings["StateLoad"] = stageStopwatch.ElapsedMilliseconds;
        _logger?.LogInformation("[Orchestrator] Conversation state loaded in {DurationMs}ms (Turn: {TurnCount}).", timings["StateLoad"], currentState.TurnCount);

        // 2. Run Conversational Query Refiner
        stageStopwatch.Restart();
        var refinement = await _queryRefiner.RefineQueryAsync(question, currentState);
        var effectiveQuestion = string.IsNullOrWhiteSpace(refinement.EffectiveQuestion) ? question : refinement.EffectiveQuestion;
        timings["QueryRefinement"] = stageStopwatch.ElapsedMilliseconds;
        _logger?.LogInformation("[Orchestrator] Query refinement completed in {DurationMs}ms. Effective question: \"{EffectiveQuestion}\".", timings["QueryRefinement"], effectiveQuestion);

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
            stageStopwatch.Restart();
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
            timings["ClarificationExecution"] = stageStopwatch.ElapsedMilliseconds;

            totalStopwatch.Stop();
            timings["Total"] = totalStopwatch.ElapsedMilliseconds;

            LogTimingSummary(question, effectiveQuestion, "Clarification", effectiveConvId, timings, totalStopwatch.ElapsedMilliseconds, promptTraces);

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
                ConversationStateAfter: currentState,
                Timings: timings,
                TotalDurationMs: totalStopwatch.ElapsedMilliseconds,
                PromptTraces: promptTraces
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

        // 4. Start Input Governor and Initial Vector Search in Parallel
        stageStopwatch.Restart();
        var govResultTask = _inputGovernor.GovernInputAsync(effectiveQuestion, question, conversationContext);
        var initialSearchTask = vectorStore.SearchAsync(effectiveQuestion);

        var govResult = await govResultTask;
        timings["InputGovernor"] = stageStopwatch.ElapsedMilliseconds;
        _logger?.LogInformation("[Orchestrator] Input governor completed in {DurationMs}ms (Intent: {Intent}, Confidence: {Confidence}).", timings["InputGovernor"], govResult.Intent, govResult.Confidence);

        // 5. Workflow Router
        var workflowType = _workflowRouter.RouteWorkflow(govResult);
        var executor = _executors[workflowType];

        // 6. Execute Initial Workflow / First Retrieval (reusing parallelized initial search)
        stageStopwatch.Restart();
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(effectiveQuestion, govResult, vectorStore, initialSearchTask);
        timings["WorkflowExecution"] = stageStopwatch.ElapsedMilliseconds;
        _logger?.LogInformation("[Orchestrator] Workflow execution ({Workflow}) completed in {DurationMs}ms (Chunks: {ChunkCount}).", workflowType, timings["WorkflowExecution"], contextChunks.Count);

        retrievals.Add(new RetrievalTrace(
            Purpose: $"Initial retrieval for workflow: {workflowType}",
            Query: effectiveQuestion,
            Results: contextChunks.Select(c => c.Text).ToList()
        ));

        var allChunks = new List<DocumentChunk>(contextChunks);
        var currentDraft = draftResponse;
        List<EvidenceClaim> evidenceClaims = new();

        // 7. Iterative Retrieval & Evidence Evaluation Loop
        stageStopwatch.Restart();
        int iterations = 0;
        bool isDomainWorkflow = (workflowType == WorkflowType.Overview || workflowType == WorkflowType.Compatibility || workflowType == WorkflowType.Comparison);

        if (isDomainWorkflow)
        {
            _logger?.LogInformation("[Orchestrator] Bypassing LLM evidence evaluation for deterministic domain workflow: {WorkflowType}.", workflowType);
            evidenceClaims = ExtractDomainEvidenceClaims(workflowData, govResult);
            timings["EvidenceEvaluation"] = stageStopwatch.ElapsedMilliseconds;
            timings["EvidenceAndIterativeRetrievalTotal"] = stageStopwatch.ElapsedMilliseconds;
        }
        else
        {
            while (iterations < MaxRetrievalIterations)
            {
                iterations++;
                var evalStopwatch = Stopwatch.StartNew();

                // Evaluate Evidence
                evidenceClaims = await _evidenceEvaluator.EvaluateEvidenceAsync(effectiveQuestion, currentDraft, allChunks);
                evalStopwatch.Stop();
                timings[$"EvidenceEval_Iter{iterations}"] = evalStopwatch.ElapsedMilliseconds;

                // Check for missing/conflicting claims
                var missingClaims = evidenceClaims.Where(c => c.Status == "MISSING" || c.Status == "CONFLICTING").ToList();
                if (missingClaims.Count == 0)
                {
                    break;
                }

                // Perform targeted retrieval for the first missing/conflicting claim
                var targetClaim = missingClaims[0];
                var targetedQueryPrompt = $"Given the effective question '{effectiveQuestion}' and the missing or conflicting technical claim '{targetClaim.Claim}', write a single, precise keyword query to search a vector store for relevant documentation to resolve or support this claim.";
                
                var tqStopwatch = Stopwatch.StartNew();
                _logger?.LogInformation("[Orchestrator] Executing targeted retrieval query prompt for missing claim: \"{Claim}\".\nPrompt:\n{Prompt}", targetClaim.Claim, targetedQueryPrompt);
                var targetedQuery = await _llmService.GenerateCompletionAsync(targetedQueryPrompt);
                tqStopwatch.Stop();
                promptTraces.Add(new PromptTrace("IterativeRetrieval", $"TargetedQuery_Iter{iterations}", targetedQueryPrompt, targetedQuery, tqStopwatch.ElapsedMilliseconds));

                var searchStopwatch = Stopwatch.StartNew();
                var additionalChunks = await vectorStore.SearchAsync(targetedQuery);
                searchStopwatch.Stop();
                timings[$"VectorSearch_Iter{iterations}"] = searchStopwatch.ElapsedMilliseconds;

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

                // Re-run draft response generation with updated/larger context chunks for non-domain workflows
                if (workflowType == WorkflowType.SimpleRag || workflowType == WorkflowType.Diagnostic || workflowType == WorkflowType.Design)
                {
                    var redraftStopwatch = Stopwatch.StartNew();
                    currentDraft = await _llmService.GenerateResponseAsync(effectiveQuestion, allChunks);
                    redraftStopwatch.Stop();
                    timings[$"Redraft_Iter{iterations}"] = redraftStopwatch.ElapsedMilliseconds;
                    promptTraces.Add(new PromptTrace("IterativeRetrieval", $"Redraft_Iter{iterations}", $"Query: {effectiveQuestion}, Chunks: {allChunks.Count}", currentDraft, redraftStopwatch.ElapsedMilliseconds));
                }
            }
            timings["EvidenceAndIterativeRetrievalTotal"] = stageStopwatch.ElapsedMilliseconds;
            _logger?.LogInformation("[Orchestrator] Evidence evaluation and iterative retrieval completed in {DurationMs}ms ({IterationCount} iterations).", timings["EvidenceAndIterativeRetrievalTotal"], iterations);
        }

        // 8. Answer Composer
        stageStopwatch.Restart();
        string composedAnswer = (workflowType == WorkflowType.Overview)
            ? currentDraft
            : await _answerComposer.ComposeAnswerAsync(effectiveQuestion, govResult, evidenceClaims, allChunks, currentDraft, workflowData);
        timings["AnswerComposition"] = stageStopwatch.ElapsedMilliseconds;
        _logger?.LogInformation("[Orchestrator] Answer composition completed in {DurationMs}ms (Length: {Length} chars).", timings["AnswerComposition"], composedAnswer.Length);

        // 9. Output Governor Validation & Bounded Regeneration Loop
        stageStopwatch.Restart();
        int regenAttempts = 0;
        OutputGovernorResult? govOutputResult = null;
        string finalAnswer = composedAnswer;

        while (regenAttempts < MaxRegenerationAttempts)
        {
            var govTimer = Stopwatch.StartNew();
            govOutputResult = await _outputGovernor.ValidateOutputAsync(effectiveQuestion, finalAnswer, allChunks);
            govTimer.Stop();
            timings[$"OutputGovernorValidation_Attempt{regenAttempts + 1}"] = govTimer.ElapsedMilliseconds;

            if (govOutputResult.Approved || govOutputResult.Action == "APPROVE")
            {
                break;
            }

            if (govOutputResult.Action == "REQUEST_CLARIFICATION")
            {
                var clarifyPrompt = $"The user asked: '{effectiveQuestion}'. Please formulate a polite response in the EXACT same language as the user's question asking them to clarify or provide additional details/model numbers.";
                var clarifyTimer = Stopwatch.StartNew();
                _logger?.LogInformation("[Orchestrator] Executing OutputGovernor clarification prompt.\nPrompt:\n{Prompt}", clarifyPrompt);
                finalAnswer = await _llmService.GenerateCompletionAsync(clarifyPrompt, requireJson: false);
                clarifyTimer.Stop();
                promptTraces.Add(new PromptTrace("OutputGovernor", "RequestClarification", clarifyPrompt, finalAnswer, clarifyTimer.ElapsedMilliseconds));
                break;
            }

            regenAttempts++;

            if (regenAttempts >= MaxRegenerationAttempts && govOutputResult.Action == "FAIL_SAFE")
            {
                var failSafePrompt = $"The user asked: '{effectiveQuestion}'. Please formulate a polite response in the EXACT same language as the user's question explaining that the available search results contain conflicting or insufficient data to provide a safe verified answer, and advise them to contact technical support.";
                var failSafeTimer = Stopwatch.StartNew();
                _logger?.LogInformation("[Orchestrator] Executing OutputGovernor fail-safe prompt.\nPrompt:\n{Prompt}", failSafePrompt);
                finalAnswer = await _llmService.GenerateCompletionAsync(failSafePrompt, requireJson: false);
                failSafeTimer.Stop();
                promptTraces.Add(new PromptTrace("OutputGovernor", "FailSafe", failSafePrompt, finalAnswer, failSafeTimer.ElapsedMilliseconds));
                break;
            }

            // Otherwise, regenerate with Output Governor feedback
            var issues = govOutputResult.Issues ?? new List<OutputGovernorIssue>();
            var issuesList = issues.Count > 0
                ? string.Join("\n", issues.Select(i => $"- [{i.Type}] {i.Claim} ({i.Severity})"))
                : "Ensure the answer only makes claims strictly verified by the retrieved context chunks or ebm-papst domain rules.";

            var regenPrompt = $$"""
                You are the Answer Composer. Your previous response was rejected by the Output Governor due to the following issues:
                {{issuesList}}

                Please rewrite the response, resolving all these issues. Ensure every claim is fully supported by the retrieved documentation or domain rules. If information is not found in the context, clearly explain that it is not specified rather than making assumptions.
                CRITICAL LANGUAGE REQUIREMENT: You MUST write the ENTIRE response in the EXACT same language as the user's question.

                Original Question: {{effectiveQuestion}}
                Retrieved Context:
                {{string.Join("\n\n", allChunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}
                """;

            var regenTimer = Stopwatch.StartNew();
            _logger?.LogInformation("[Orchestrator] Executing regeneration prompt (Attempt {Attempt}).\nPrompt:\n{Prompt}", regenAttempts, regenPrompt);
            finalAnswer = await _llmService.GenerateCompletionAsync(regenPrompt, requireJson: false);
            regenTimer.Stop();
            timings[$"Regeneration_Attempt{regenAttempts}"] = regenTimer.ElapsedMilliseconds;
            promptTraces.Add(new PromptTrace("OutputGovernor", $"Regeneration_Attempt{regenAttempts}", regenPrompt, finalAnswer, regenTimer.ElapsedMilliseconds));
        }
        timings["OutputGovernorTotal"] = stageStopwatch.ElapsedMilliseconds;
        _logger?.LogInformation("[Orchestrator] Output governor and validation loop completed in {DurationMs}ms (Action: {Action}, RegenAttempts: {RegenAttempts}).", timings["OutputGovernorTotal"], govOutputResult?.Action, regenAttempts);

        // 10. Update Conversation State (Committed upon approval)
        stageStopwatch.Restart();
        var stateAfter = UpdateConversationState(currentState, refinement, govResult, evidenceClaims, effectiveQuestion);
        await _stateStore.SaveStateAsync(effectiveConvId, stateAfter);
        timings["StateSave"] = stageStopwatch.ElapsedMilliseconds;

        totalStopwatch.Stop();
        timings["Total"] = totalStopwatch.ElapsedMilliseconds;

        LogTimingSummary(question, effectiveQuestion, workflowType.ToString(), effectiveConvId, timings, totalStopwatch.ElapsedMilliseconds, promptTraces);

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
            ConversationStateAfter: stateAfter,
            Timings: timings,
            TotalDurationMs: totalStopwatch.ElapsedMilliseconds,
            PromptTraces: promptTraces
        );

        return (finalAnswer, allChunks, trace);
    }

    public async IAsyncEnumerable<OrchestrationStreamEvent> ProcessQueryStreamAsync(
        string question,
        IVectorStore vectorStore,
        string? conversationId,
        ConversationState? initialConversationState = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentNullException(nameof(question));

        var totalStopwatch = Stopwatch.StartNew();
        var stageStopwatch = Stopwatch.StartNew();
        var timings = new Dictionary<string, long>();
        var promptTraces = new List<PromptTrace>();

        var effectiveConvId = conversationId ?? initialConversationState?.ConversationId ?? Guid.NewGuid().ToString();
        _logger?.LogInformation("Starting streaming RAG query processing for question: \"{Question}\" (ConversationId: {ConversationId})", question, effectiveConvId);

        // 1. Load Conversation State
        var currentState = initialConversationState ?? await _stateStore.GetStateAsync(effectiveConvId) ?? ConversationState.CreateNew(effectiveConvId);
        var stateBefore = currentState;
        timings["StateLoad"] = stageStopwatch.ElapsedMilliseconds;

        // 2. Run Conversational Query Refiner
        stageStopwatch.Restart();
        var refinement = await _queryRefiner.RefineQueryAsync(question, currentState);
        var effectiveQuestion = string.IsNullOrWhiteSpace(refinement.EffectiveQuestion) ? question : refinement.EffectiveQuestion;
        timings["QueryRefinement"] = stageStopwatch.ElapsedMilliseconds;

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
            stageStopwatch.Restart();
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
            timings["ClarificationExecution"] = stageStopwatch.ElapsedMilliseconds;

            totalStopwatch.Stop();
            timings["Total"] = totalStopwatch.ElapsedMilliseconds;

            LogTimingSummary(question, effectiveQuestion, "Clarification", effectiveConvId, timings, totalStopwatch.ElapsedMilliseconds, promptTraces);

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
                ConversationStateAfter: currentState,
                Timings: timings,
                TotalDurationMs: totalStopwatch.ElapsedMilliseconds,
                PromptTraces: promptTraces
            );

            yield return new OrchestrationStreamEvent("citations", null, new List<DocumentChunk>(), effectiveConvId);
            yield return new OrchestrationStreamEvent("chunk", clarificationDraft);
            yield return new OrchestrationStreamEvent("done", null, new List<DocumentChunk>(), effectiveConvId, clarificationTrace);
            yield break;
        }

        var conversationContext = new ConversationContext(
            ActiveEntities: refinement.ActiveEntities,
            ActiveConstraints: refinement.ActiveConstraints,
            CandidateSet: refinement.CandidateSet ?? currentState.CandidateSet?.Members,
            ReusableFacts: currentState.ValidatedFacts
        );

        var retrievals = new List<RetrievalTrace>();

        // 4. Start Input Governor and Initial Vector Search in Parallel
        stageStopwatch.Restart();
        var govResultTask = _inputGovernor.GovernInputAsync(effectiveQuestion, question, conversationContext);
        var initialSearchTask = vectorStore.SearchAsync(effectiveQuestion);

        var govResult = await govResultTask;
        timings["InputGovernor"] = stageStopwatch.ElapsedMilliseconds;

        // 5. Workflow Router
        var workflowType = _workflowRouter.RouteWorkflow(govResult);
        var executor = _executors[workflowType];

        // 6. Execute Initial Workflow
        stageStopwatch.Restart();
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(effectiveQuestion, govResult, vectorStore, initialSearchTask);
        timings["WorkflowExecution"] = stageStopwatch.ElapsedMilliseconds;

        retrievals.Add(new RetrievalTrace(
            Purpose: $"Initial retrieval for workflow: {workflowType}",
            Query: effectiveQuestion,
            Results: contextChunks.Select(c => c.Text).ToList()
        ));

        var allChunks = new List<DocumentChunk>(contextChunks);

        // 7. Evidence evaluation
        stageStopwatch.Restart();
        bool isDomainWorkflow = (workflowType == WorkflowType.Overview || workflowType == WorkflowType.Compatibility || workflowType == WorkflowType.Comparison);
        List<EvidenceClaim> evidenceClaims;

        if (isDomainWorkflow)
        {
            _logger?.LogInformation("[Orchestrator] Bypassing LLM evidence evaluation for deterministic domain workflow: {WorkflowType}.", workflowType);
            evidenceClaims = ExtractDomainEvidenceClaims(workflowData, govResult);
            timings["EvidenceEvaluation"] = stageStopwatch.ElapsedMilliseconds;
        }
        else
        {
            evidenceClaims = await _evidenceEvaluator.EvaluateEvidenceAsync(effectiveQuestion, draftResponse, allChunks);
            timings["EvidenceEvaluation"] = stageStopwatch.ElapsedMilliseconds;
        }

        // Yield citations event as soon as retrieved chunks are ready
        yield return new OrchestrationStreamEvent("citations", null, allChunks, effectiveConvId);

        // 8. Stream the Answer in Real-Time
        stageStopwatch.Restart();
        var streamTtftStopwatch = Stopwatch.StartNew();
        bool firstChunk = false;
        var answerSb = new StringBuilder();

        if (workflowType == WorkflowType.Overview)
        {
            answerSb.Append(draftResponse);
            yield return new OrchestrationStreamEvent("chunk", draftResponse);
        }
        else
        {
            await foreach (var chunk in _answerComposer.StreamAnswerAsync(effectiveQuestion, govResult, evidenceClaims, allChunks, draftResponse, workflowData, cancellationToken))
            {
                if (!firstChunk)
                {
                    firstChunk = true;
                    timings["TimeToFirstToken"] = streamTtftStopwatch.ElapsedMilliseconds;
                    _logger?.LogInformation("[Orchestrator] First streamed token yielded in {DurationMs}ms.", timings["TimeToFirstToken"]);
                }
                answerSb.Append(chunk);
                yield return new OrchestrationStreamEvent("chunk", chunk);
            }
        }
        timings["AnswerStreamDuration"] = stageStopwatch.ElapsedMilliseconds;

        var finalAnswer = answerSb.ToString();
        var govOutputResult = new OutputGovernorResult(Approved: true, Issues: new List<OutputGovernorIssue>(), Action: "APPROVE");

        // 9. Update Conversation State
        stageStopwatch.Restart();
        var stateAfter = UpdateConversationState(currentState, refinement, govResult, evidenceClaims, effectiveQuestion);
        await _stateStore.SaveStateAsync(effectiveConvId, stateAfter);
        timings["StateSave"] = stageStopwatch.ElapsedMilliseconds;

        totalStopwatch.Stop();
        timings["Total"] = totalStopwatch.ElapsedMilliseconds;

        LogTimingSummary(question, effectiveQuestion, workflowType.ToString(), effectiveConvId, timings, totalStopwatch.ElapsedMilliseconds, promptTraces);

        var trace = new ExecutionTrace(
            Question: effectiveQuestion,
            InputGovernor: govResult,
            Workflow: workflowType.ToString(),
            Retrievals: retrievals,
            Evidence: evidenceClaims,
            DraftAnswer: draftResponse,
            OutputGovernor: govOutputResult,
            FinalAnswer: finalAnswer,
            OriginalQuestion: question,
            ConversationStateBefore: stateBefore,
            QueryRefinement: refinementTrace,
            ConversationStateAfter: stateAfter,
            Timings: timings,
            TotalDurationMs: totalStopwatch.ElapsedMilliseconds,
            PromptTraces: promptTraces
        );

        yield return new OrchestrationStreamEvent("done", null, allChunks, effectiveConvId, trace);
    }

    private void LogTimingSummary(
        string question,
        string effectiveQuestion,
        string workflow,
        string conversationId,
        Dictionary<string, long> timings,
        long totalDurationMs,
        List<PromptTrace> promptTraces
    )
    {
        if (_logger == null) return;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("==================== RAG PIPELINE TIMING BREAKDOWN ====================");
        sb.AppendLine($"Total Duration     : {totalDurationMs:N0} ms");
        sb.AppendLine($"Question           : \"{question}\"");
        if (!string.Equals(question, effectiveQuestion, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"Effective Question : \"{effectiveQuestion}\"");
        }
        sb.AppendLine($"Workflow           : {workflow}");
        sb.AppendLine($"Conversation ID    : {conversationId}");
        sb.AppendLine();
        sb.AppendLine("Stage Timings:");
        foreach (var kvp in timings)
        {
            double percentage = totalDurationMs > 0 ? (kvp.Value * 100.0 / totalDurationMs) : 0;
            sb.AppendLine($"  - {kvp.Key.PadRight(35)}: {kvp.Value,6:N0} ms ({percentage,5:F1}%)");
        }

        if (promptTraces != null && promptTraces.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Prompts Tracked ({promptTraces.Count}):");
            for (int i = 0; i < promptTraces.Count; i++)
            {
                var p = promptTraces[i];
                var preview = p.Prompt.Length <= 100 
                    ? p.Prompt.Replace("\r", "").Replace("\n", " ") 
                    : p.Prompt.Substring(0, 97).Replace("\r", "").Replace("\n", " ") + "...";
                sb.AppendLine($"  {i + 1}. [{p.Stage} / {p.PromptName}] {p.DurationMs:N0} ms ({p.Prompt.Length:N0} chars): \"{preview}\"");
            }
        }
        sb.AppendLine("=======================================================================");

        _logger.LogInformation("{TimingSummary}", sb.ToString());
        _logger.LogInformation(
            "RAG Pipeline finished in {TotalDurationMs}ms (Workflow: {Workflow}, ConversationId: {ConversationId}). Timings: {@Timings}",
            totalDurationMs, workflow, conversationId, timings);
    }

    private ConversationState UpdateConversationState(
        ConversationState currentState,
        QueryRefinementResult refinement,
        InputGovernorResult? govResult,
        List<EvidenceClaim>? evidenceClaims,
        string effectiveQuestion
    )
    {
        int newTurnCount = currentState.TurnCount + 1;
        var refinementEntities = refinement.ActiveEntities ?? new List<ConversationEntity>();
        var refinementConstraints = refinement.ActiveConstraints ?? new List<ConversationConstraint>();
        var currentEntities = currentState.ActiveEntities ?? new List<ConversationEntity>();
        var currentConstraints = currentState.ActiveConstraints ?? new List<ConversationConstraint>();
        var safeIntent = govResult?.Intent ?? "SPEC_LOOKUP";

        if (refinement.RelationshipToPreviousTurn == TurnRelationship.NewTopic || refinement.NewTopic)
        {
            var newEntities = refinementEntities.ToList();
            var newConstraints = refinementConstraints.ToList();
            var newCandidateSet = refinement.CandidateSet != null && refinement.CandidateSet.Count > 0
                ? new CandidateSet("entity", refinement.CandidateSet, newTurnCount)
                : null;

            return new ConversationState(
                ConversationId: currentState.ConversationId,
                Topic: newEntities.FirstOrDefault()?.Name ?? "general",
                ActiveEntities: newEntities,
                ActiveConstraints: newConstraints,
                CandidateSet: newCandidateSet,
                ValidatedFacts: ExtractValidatedFacts(evidenceClaims ?? new List<EvidenceClaim>(), newEntities, newTurnCount),
                DerivedFacts: ExtractDerivedFacts(evidenceClaims ?? new List<EvidenceClaim>(), newEntities, newTurnCount),
                UnresolvedReferences: new List<string>(),
                LastIntent: safeIntent,
                LastEffectiveQuestion: effectiveQuestion,
                TopicVersion: currentState.TopicVersion + 1,
                TurnCount: newTurnCount
            );
        }

        if (refinement.RelationshipToPreviousTurn == TurnRelationship.Reset)
        {
            var newEntities = refinementEntities.ToList();
            var newConstraints = refinementConstraints.ToList();

            return new ConversationState(
                ConversationId: currentState.ConversationId,
                Topic: currentState.Topic,
                ActiveEntities: newEntities,
                ActiveConstraints: newConstraints,
                CandidateSet: null,
                ValidatedFacts: new List<ConversationFact>(),
                DerivedFacts: new List<ConversationFact>(),
                UnresolvedReferences: new List<string>(),
                LastIntent: safeIntent,
                LastEffectiveQuestion: effectiveQuestion,
                TopicVersion: currentState.TopicVersion + 1,
                TurnCount: newTurnCount
            );
        }

        // CONTINUES / REFINES / MODIFIES
        // 1. Merge active entities
        var mergedEntities = new List<ConversationEntity>(currentEntities);
        foreach (var entity in refinementEntities)
        {
            if (!mergedEntities.Any(e => string.Equals(e.Name, entity.Name, StringComparison.OrdinalIgnoreCase)))
            {
                mergedEntities.Add(entity);
            }
        }

        // 2. Active constraints update (replacement, removal, addition)
        var updatedConstraints = new List<ConversationConstraint>(currentConstraints);

        // Remove constraints
        if (refinement.ConstraintsRemoved != null)
        {
            foreach (var removed in refinement.ConstraintsRemoved)
            {
                if (removed == null) continue;
                updatedConstraints.RemoveAll(c => string.Equals(c.Attribute, removed.Attribute, StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(c.Value, removed.Value, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Replace constraints
        if (refinement.ConstraintsReplaced != null)
        {
            foreach (var replacement in refinement.ConstraintsReplaced)
            {
                if (replacement == null) continue;
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
                if (added == null) continue;
                // If this is replacing an existing attribute with a new value, replace it
                updatedConstraints.RemoveAll(c => string.Equals(c.Attribute, added.Attribute, StringComparison.OrdinalIgnoreCase) && !string.Equals(c.Value, added.Value, StringComparison.OrdinalIgnoreCase));
                if (!updatedConstraints.Any(c => string.Equals(c.Attribute, added.Attribute, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Value, added.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    updatedConstraints.Add(added with { SourceTurn = newTurnCount });
                }
            }
        }

        // If refinement has explicit active constraints, ensure they are present
        foreach (var active in refinementConstraints)
        {
            if (active == null) continue;
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
        var newValidated = ExtractValidatedFacts(evidenceClaims ?? new List<EvidenceClaim>(), mergedEntities, newTurnCount);
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

        var newDerived = ExtractDerivedFacts(evidenceClaims ?? new List<EvidenceClaim>(), mergedEntities, newTurnCount);
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
            LastIntent: safeIntent,
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

    private static List<EvidenceClaim> ExtractDomainEvidenceClaims(object? workflowData, InputGovernorResult? govResult)
    {
        var claims = new List<EvidenceClaim>();

        if (workflowData is CompatibilityResult compatResult)
        {
            if (compatResult.ReplacementAnalysis != null)
            {
                var src = compatResult.ReplacementAnalysis.SourceProduct;
                claims.Add(new EvidenceClaim(
                    Claim: $"{src.RawCode}: {src.FanTypeDescription}, {src.MotorDescription}, Ø{src.ImpellerDiameterMm}mm, {src.AirflowDescription}",
                    Status: "SUPPORTED",
                    Reason: "Deterministically decoded from ebm-papst product code via EbmProductCodeParser.",
                    Sources: new List<string> { "ebm-papst Typenøgle" }
                ));

                if (compatResult.ReplacementAnalysis.TheoreticalPatterns != null)
                {
                    foreach (var theo in compatResult.ReplacementAnalysis.TheoreticalPatterns)
                    {
                        claims.Add(new EvidenceClaim(
                            Claim: $"Anbefalet serieerstatning: {theo.PatternType} ({theo.SuggestedModelOrPrefix}) - {theo.Description}",
                            Status: "DERIVED",
                            Reason: string.Join("; ", theo.Requirements ?? new List<string>()),
                            Sources: new List<string> { "ebm-papst Series Conventions" }
                        ));
                    }
                }
            }

            if (compatResult.EbmEvaluation != null)
            {
                var eval = compatResult.EbmEvaluation;
                claims.Add(new EvidenceClaim(
                    Claim: $"{eval.ProductA.RawCode}: {eval.ProductA.FanTypeDescription}, {eval.ProductA.MotorDescription}, Ø{eval.ProductA.ImpellerDiameterMm}mm, {eval.ProductA.AirflowDescription}",
                    Status: "SUPPORTED",
                    Reason: "Deterministically decoded from ebm-papst product code.",
                    Sources: new List<string> { "ebm-papst Typenøgle" }
                ));
                claims.Add(new EvidenceClaim(
                    Claim: $"{eval.ProductB.RawCode}: {eval.ProductB.FanTypeDescription}, {eval.ProductB.MotorDescription}, Ø{eval.ProductB.ImpellerDiameterMm}mm, {eval.ProductB.AirflowDescription}",
                    Status: "SUPPORTED",
                    Reason: "Deterministically decoded from ebm-papst product code.",
                    Sources: new List<string> { "ebm-papst Typenøgle" }
                ));
                foreach (var diff in eval.Differences)
                {
                    claims.Add(new EvidenceClaim(
                        Claim: $"{diff.Dimension}: {diff.ValueA} vs {diff.ValueB}",
                        Status: diff.IsCriticalIncompatibility ? "CONFLICTING" : "SUPPORTED",
                        Reason: diff.Explanation,
                        Sources: new List<string> { "ebm-papst Domain Comparison" }
                    ));
                }
            }

            if (compatResult.Checks != null)
            {
                foreach (var check in compatResult.Checks)
                {
                    var status = check.Status == "INCOMPATIBLE" ? "CONFLICTING" :
                                 (check.Status == "COMPATIBLE" ? "SUPPORTED" : "DERIVED");
                    claims.Add(new EvidenceClaim(
                        Claim: $"{check.Dimension}: {check.SourceValue} vs {check.TargetRequirement}",
                        Status: status,
                        Reason: check.Reason,
                        Sources: new List<string> { "ebm-papst Domain Specifications" }
                    ));
                }
            }
        }
        else if (workflowData is ComparisonResult compResult)
        {
            if (compResult.EbmEvaluation != null)
            {
                var eval = compResult.EbmEvaluation;
                claims.Add(new EvidenceClaim(
                    Claim: $"{eval.ProductA.RawCode}: {eval.ProductA.FanTypeDescription}, {eval.ProductA.MotorDescription}, Ø{eval.ProductA.ImpellerDiameterMm}mm, {eval.ProductA.AirflowDescription}",
                    Status: "SUPPORTED",
                    Reason: "Deterministically decoded from ebm-papst product code via EbmProductCodeParser.",
                    Sources: new List<string> { "ebm-papst Typenøgle" }
                ));
                claims.Add(new EvidenceClaim(
                    Claim: $"{eval.ProductB.RawCode}: {eval.ProductB.FanTypeDescription}, {eval.ProductB.MotorDescription}, Ø{eval.ProductB.ImpellerDiameterMm}mm, {eval.ProductB.AirflowDescription}",
                    Status: "SUPPORTED",
                    Reason: "Deterministically decoded from ebm-papst product code via EbmProductCodeParser.",
                    Sources: new List<string> { "ebm-papst Typenøgle" }
                ));
                foreach (var diff in eval.Differences)
                {
                    claims.Add(new EvidenceClaim(
                        Claim: $"{diff.Dimension}: {diff.ValueA} vs {diff.ValueB}",
                        Status: diff.IsCriticalIncompatibility ? "CONFLICTING" : "SUPPORTED",
                        Reason: diff.Explanation,
                        Sources: new List<string> { "ebm-papst Domain Comparison" }
                    ));
                }
            }

            if (compResult.Entities != null)
            {
                foreach (var entity in compResult.Entities)
                {
                    foreach (var attr in entity.Value.Attributes)
                    {
                        claims.Add(new EvidenceClaim(
                            Claim: $"{entity.Key} {attr.Key}: {attr.Value.Value}",
                            Status: "SUPPORTED",
                            Reason: $"Extracted from {attr.Value.Source}",
                            Sources: new List<string> { attr.Value.Source }
                        ));
                    }
                }
            }
        }
        else if (workflowData is CatalogOverviewResult catResult)
        {
            claims.Add(new EvidenceClaim(
                Claim: $"System catalog contains {catResult.TotalModels} ventilator models across {catResult.TotalDocuments} datasheets.",
                Status: "SUPPORTED",
                Reason: "Deterministically indexed from documents and product codes via EbmProductCodeParser.",
                Sources: new List<string> { "Document Catalog" }
            ));
        }

        if (govResult?.ParsedEbmProducts != null && govResult.ParsedEbmProducts.Count > 0)
        {
            foreach (var prod in govResult.ParsedEbmProducts)
            {
                var claimText = $"{prod.RawCode}: {prod.FanTypeDescription}, {prod.MotorDescription}, Ø{prod.ImpellerDiameterMm}mm, {prod.AirflowDescription}";
                if (!claims.Any(c => c.Claim == claimText))
                {
                    claims.Add(new EvidenceClaim(
                        Claim: claimText,
                        Status: "SUPPORTED",
                        Reason: "Deterministically decoded from ebm-papst product code via EbmProductCodeParser.",
                        Sources: new List<string> { "ebm-papst Typenøgle" }
                    ));
                }
            }
        }

        return claims;
    }
}

public class AnswerComposerException : Exception
{
    public AnswerComposerException(string message) : base(message) { }
}

