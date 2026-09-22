using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Embeddings;
using DocumentRagSystem.Infrastructure.Llm;
using DocumentRagSystem.Infrastructure.VectorStores;

namespace DocumentRagSystem.UnitTests;

public class TimingAndPromptLoggingTests
{
    private class TestLogger<T> : ILogger<T>
    {
        public List<string> LoggedMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            LoggedMessages.Add(message);
        }
    }

    [Fact]
    public async Task Orchestrator_ShouldPopulateTimingsAndTotalDuration_InExecutionTrace()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(m => m.GenerateCompletionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync("""
            {
              "intent": "SPEC_LOOKUP",
              "confidence": 0.95,
              "entities": [{"type": "fan", "name": "K3G560"}],
              "requested_attributes": ["voltage"],
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        mockLlm.Setup(m => m.GenerateResponseAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DocumentChunk>>()))
            .ReturnsAsync("The voltage is 400 VAC.");

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(v => v.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<DocumentChunk>
            {
                new DocumentChunk("chunk1", "doc1", "Voltage is 400 VAC", 0, "k3g560.pdf")
            });

        var logger = new TestLogger<TechnicalRagOrchestrator>();
        var inputGovernor = new InputGovernor(mockLlm.Object);
        var workflowRouter = new WorkflowRouter();
        var evidenceEvaluator = new EvidenceEvaluator(mockLlm.Object);
        var answerComposer = new AnswerComposer(mockLlm.Object);
        var outputGovernor = new OutputGovernor(mockLlm.Object);

        var orchestrator = new TechnicalRagOrchestrator(
            inputGovernor,
            workflowRouter,
            evidenceEvaluator,
            answerComposer,
            outputGovernor,
            mockLlm.Object,
            logger: logger
        );

        // Act
        var (answer, chunks, trace) = await orchestrator.ProcessQueryAsync("What is the voltage of K3G560?", mockVectorStore.Object);

        // Assert
        Assert.NotNull(trace);
        Assert.NotNull(trace.Timings);
        Assert.True(trace.Timings.ContainsKey("StateLoad"));
        Assert.True(trace.Timings.ContainsKey("QueryRefinement"));
        Assert.True(trace.Timings.ContainsKey("InputGovernor"));
        Assert.True(trace.Timings.ContainsKey("WorkflowExecution"));
        Assert.True(trace.Timings.ContainsKey("Total"));
        Assert.NotNull(trace.TotalDurationMs);
        Assert.True(trace.TotalDurationMs >= 0);

        // Verify summary logged
        Assert.Contains(logger.LoggedMessages, m => m.Contains("RAG PIPELINE TIMING BREAKDOWN"));
        Assert.Contains(logger.LoggedMessages, m => m.Contains("Total Duration"));
    }

    [Fact]
    public async Task GeminiLlmService_ShouldLogPromptAndTiming_OnGenerateCompletion()
    {
        // Arrange
        var logger = new TestLogger<GeminiLlmService>();
        var service = new GeminiLlmService(null, logger: logger);

        // Act
        var result = await service.GenerateCompletionAsync("Tell me about K3G560 fan", requireJson: false);

        // Assert
        Assert.NotNull(result);
        Assert.Contains(logger.LoggedMessages, m => m.Contains("Starting LLM [GenerateCompletion]") && m.Contains("Tell me about K3G560 fan"));
        Assert.Contains(logger.LoggedMessages, m => m.Contains("completed in") && m.Contains("ms"));
    }

    [Fact]
    public async Task GeminiEmbeddingService_ShouldLogTiming_OnGenerateEmbedding()
    {
        // Arrange
        var logger = new TestLogger<GeminiEmbeddingService>();
        var service = new GeminiEmbeddingService(null, logger: logger);

        // Act
        var embedding = await service.GenerateEmbeddingAsync("Test fan search query");

        // Assert
        Assert.NotNull(embedding);
        Assert.Equal(768, embedding.Length);
        Assert.Contains(logger.LoggedMessages, m => m.Contains("[GeminiEmbedding]") && m.Contains("ms"));
    }

    [Fact]
    public async Task InputGovernor_And_EvidenceEvaluator_ShouldLogPromptsAndTimings()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(m => m.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "intent": "SPEC_LOOKUP",
              "confidence": 0.95,
              "entities": [],
              "requested_attributes": [],
              "constraints": [],
              "clarification_required": false
            }
            """);

        var govLogger = new TestLogger<InputGovernor>();
        var inputGovernor = new InputGovernor(mockLlm.Object, logger: govLogger);

        // Act
        var govResult = await inputGovernor.GovernInputAsync("What is the diameter?");

        // Assert
        Assert.NotNull(govResult);
        Assert.Contains(govLogger.LoggedMessages, m => m.Contains("[InputGovernor] Executing intent classification prompt"));
        Assert.Contains(govLogger.LoggedMessages, m => m.Contains("[InputGovernor] Classification completed in") && m.Contains("ms"));
    }

    [Fact]
    public async Task Orchestrator_ShouldRecordPromptTraces_WhenTargetedIterativeRetrievalOccurs()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(m => m.GenerateCompletionAsync(It.Is<string>(p => p.Contains("Input Governor")), true))
            .ReturnsAsync("""
            {
              "intent": "SPEC_LOOKUP",
              "confidence": 0.95,
              "entities": [{"type": "fan", "name": "A6E450"}],
              "requested_attributes": ["voltage"],
              "constraints": [],
              "clarification_required": false
            }
            """);

        mockLlm.Setup(m => m.GenerateCompletionAsync(It.Is<string>(p => p.Contains("missing or conflicting technical claim")), false))
            .ReturnsAsync("A6E450 operating voltage specification");

        mockLlm.Setup(m => m.GenerateCompletionAsync(It.Is<string>(p => p.Contains("Output Governor")), true))
            .ReturnsAsync("""
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }
            """);

        mockLlm.Setup(m => m.GenerateResponseAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DocumentChunk>>()))
            .ReturnsAsync("The voltage is 230 VAC.");

        var mockEvaluator = new Mock<IEvidenceEvaluator>();
        // First iteration returns MISSING claim to trigger iterative retrieval
        mockEvaluator.SetupSequence(e => e.EvaluateEvidenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<DocumentChunk>>()))
            .ReturnsAsync(new List<EvidenceClaim>
            {
                new EvidenceClaim("Operating voltage is 230 VAC", "MISSING", "Not found in initial chunk", new List<string>())
            })
            .ReturnsAsync(new List<EvidenceClaim>
            {
                new EvidenceClaim("Operating voltage is 230 VAC", "SUPPORTED", "Found in chunk2", new List<string> { "chunk2" })
            });

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.SetupSequence(v => v.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<DocumentChunk> { new DocumentChunk("c1", "d1", "Fan A6E450", 0) })
            .ReturnsAsync(new List<DocumentChunk> { new DocumentChunk("c2", "d2", "Operating voltage 230 VAC", 1) });

        var inputGovernor = new InputGovernor(mockLlm.Object);
        var workflowRouter = new WorkflowRouter();
        var answerComposer = new AnswerComposer(mockLlm.Object);
        var outputGovernor = new OutputGovernor(mockLlm.Object);

        var orchestrator = new TechnicalRagOrchestrator(
            inputGovernor,
            workflowRouter,
            mockEvaluator.Object,
            answerComposer,
            outputGovernor,
            mockLlm.Object
        );

        // Act
        var (_, _, trace) = await orchestrator.ProcessQueryAsync("What is the voltage of A6E450?", mockVectorStore.Object);

        // Assert
        Assert.NotNull(trace.PromptTraces);
        Assert.Contains(trace.PromptTraces, pt => pt.Stage == "IterativeRetrieval" && pt.PromptName.StartsWith("TargetedQuery"));
        Assert.True(trace.Timings?.ContainsKey("EvidenceEval_Iter1"));
        Assert.True(trace.Timings?.ContainsKey("VectorSearch_Iter1"));
    }
}
