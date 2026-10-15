using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class GreetingWorkflowAndScalingTests
{
    [Theory]
    [InlineData("hejsa")]
    [InlineData("Hejsa!")]
    [InlineData("hej")]
    [InlineData("hallo")]
    [InlineData("goddag")]
    [InlineData("godmorgen")]
    [InlineData("hi")]
    [InlineData("hello")]
    [InlineData("hey")]
    [InlineData("tak")]
    [InlineData("mange tak")]
    [InlineData("hvem er du?")]
    [InlineData("hvad kan du hjælpe med?")]
    public async Task InputGovernor_FastPath_ShouldClassifyGreetingWithoutLlmRoundTrip(string question)
    {
        // Arrange
        var mockLlm = new Mock<ILlmClient>();
        var ebmParser = new EbmProductCodeParser();
        var governor = new InputGovernor(mockLlm.Object, ebmParser);

        // Act
        var result = await governor.GovernInputAsync(question);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("GREETING", result.Intent);
        Assert.Equal(1.0, result.Confidence);
        Assert.Empty(result.Entities);
        Assert.Empty(result.RequestedAttributes);
        Assert.False(result.ClarificationRequired);

        // Verify 0 LLM calls were made (pure fast path short circuit)
        mockLlm.Verify(x => x.GenerateTextAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InputGovernor_FastPath_ShouldNotClassifyGreetingWhenProductCodePresent()
    {
        // Arrange - user says "hejsa" but also specifies an ebm product code
        var mockLlm = new Mock<ILlmClient>();
        var govVal = new InputGovernorResult(
            Intent: "SPEC_LOOKUP",
            Confidence: 0.95,
            Entities: new List<EntityInfo> { new("fan", "K3G560PC0401") },
            RequestedAttributes: new List<string> { "voltage" },
            Constraints: new List<string>(),
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: new List<EbmProductInfo>()
        );
        mockLlm.Setup(x => x.GenerateStructuredAsync<InputGovernorResult>(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmStructuredResult<InputGovernorResult>(govVal, new LlmExecutionMetadata("gemini", "gemini-3.6-flash", 10, 10, 20, true, 1)));

        var ebmParser = new EbmProductCodeParser();
        var governor = new InputGovernor(mockLlm.Object, ebmParser);

        // Act
        var result = await governor.GovernInputAsync("Hejsa! Hvad er spændingen for K3G560PC0401?");

        // Assert
        Assert.NotNull(result);
        // Because a product was extracted, it did not take the pure greeting fast path
        Assert.Equal("SPEC_LOOKUP", result.Intent);
        Assert.NotEmpty(result.ParsedEbmProducts);
    }

    [Fact]
    public void WorkflowRouter_ShouldRouteGreetingToGreetingWorkflow()
    {
        // Arrange
        var router = new WorkflowRouter();
        var govResult = new InputGovernorResult(
            Intent: "GREETING",
            Confidence: 1.0,
            Entities: new List<EntityInfo>(),
            RequestedAttributes: new List<string>(),
            Constraints: new List<string>(),
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: new List<EbmProductInfo>()
        );

        // Act
        var workflow = router.RouteWorkflow(govResult);

        // Assert
        Assert.Equal(WorkflowType.Greeting, workflow);
    }

    [Theory]
    [InlineData("hejsa", true)]
    [InlineData("hej med dig", true)]
    [InlineData("hallo", true)]
    [InlineData("tak for hjælpen", true)]
    [InlineData("hvem er du?", true)]
    [InlineData("hello", false)]
    [InlineData("hi there", false)]
    [InlineData("who are you?", false)]
    public async Task GreetingWorkflowExecutor_ShouldReturnFriendlyResponseAndZeroChunks(string prompt, bool expectDanish)
    {
        // Arrange
        var executor = new GreetingWorkflowExecutor();
        var mockVectorStore = new Mock<IVectorStore>();
        var govResult = new InputGovernorResult(
            Intent: "GREETING",
            Confidence: 1.0,
            Entities: new List<EntityInfo>(),
            RequestedAttributes: new List<string>(),
            Constraints: new List<string>(),
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: new List<EbmProductInfo>()
        );

        // Act
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(prompt, govResult, mockVectorStore.Object);

        // Assert
        Assert.NotNull(draftResponse);
        Assert.NotEmpty(draftResponse);
        Assert.Empty(contextChunks); // Must return 0 chunks to prevent RAG overhead and citation scans

        if (expectDanish)
        {
            Assert.True(draftResponse.Contains("Hej", StringComparison.OrdinalIgnoreCase) ||
                        draftResponse.Contains("ebm-papst", StringComparison.OrdinalIgnoreCase) ||
                        draftResponse.Contains("Selv tak", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.True(draftResponse.Contains("Hello", StringComparison.OrdinalIgnoreCase) ||
                        draftResponse.Contains("assistant", StringComparison.OrdinalIgnoreCase) ||
                        draftResponse.Contains("welcome", StringComparison.OrdinalIgnoreCase));
        }

        // Vector store must NEVER be queried for a greeting
        mockVectorStore.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task TechnicalRagOrchestrator_ProcessQueryStreamAsync_ShouldHandleGreetingInstantly()
    {
        // Arrange
        var mockLlm = new Mock<ILlmClient>();
        var ebmParser = new EbmProductCodeParser();
        var governor = new InputGovernor(mockLlm.Object, ebmParser);
        var router = new WorkflowRouter();
        var mockEvidence = new Mock<IEvidenceEvaluator>();
        var mockComposer = new Mock<IAnswerComposer>();
        var mockOutputGov = new Mock<IOutputGovernor>();
        var mockVectorStore = new Mock<IVectorStore>();

        var orchestrator = new TechnicalRagOrchestrator(
            governor,
            router,
            mockEvidence.Object,
            mockComposer.Object,
            mockOutputGov.Object,
            mockLlm.Object,
            ebmParser
        );

        // Act
        var events = new List<OrchestrationStreamEvent>();
        await foreach (var evt in orchestrator.ProcessQueryStreamAsync("hejsa", mockVectorStore.Object, "conv-123"))
        {
            events.Add(evt);
        }

        // Assert
        Assert.Contains(events, e => e.EventType == "citations" && (e.Chunks == null || e.Chunks.Count == 0));
        var chunkEvent = events.FirstOrDefault(e => e.EventType == "chunk");
        Assert.NotNull(chunkEvent);
        Assert.NotEmpty(chunkEvent.Text);
        Assert.Contains("Hej", chunkEvent.Text);

        var doneEvent = events.FirstOrDefault(e => e.EventType == "done");
        Assert.NotNull(doneEvent);
        Assert.NotNull(doneEvent.Trace);
        Assert.Equal(WorkflowType.Greeting.ToString(), doneEvent.Trace.Workflow);

        // Verify heavy RAG steps were bypassed for greeting
        mockEvidence.Verify(x => x.EvaluateEvidenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<DocumentChunk>>()), Times.Never);
        mockComposer.Verify(x => x.StreamAnswerAsync(It.IsAny<string>(), It.IsAny<InputGovernorResult>(), It.IsAny<List<EvidenceClaim>>(), It.IsAny<List<DocumentChunk>>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DanishDocumentResolver_WithLargeCatalog_ShouldQueryByArticleIdWithoutFullScan()
    {
        // Arrange - Simulate 43,000 documents in the repository
        var mockRepo = new Mock<IDocumentRepository>();
        var targetArticleId = "9293512011";

        var englishDoc = new Document(
            Id: "doc-1",
            FileName: "9293512011_3258_J_2H3PU-011_PDB_EN.PDF",
            FilePath: "/uploads/9293512011_3258_J_2H3PU-011_PDB_EN.PDF",
            UploadedAt: DateTime.UtcNow,
            Language: "en",
            ArticleId: targetArticleId
        );

        var danishDoc = new Document(
            Id: "doc-2",
            FileName: "9293512011_3258_J_2H3PU-011_PDB_DA.PDF",
            FilePath: "/uploads/9293512011_3258_J_2H3PU-011_PDB_DA.PDF",
            UploadedAt: DateTime.UtcNow,
            Language: "da",
            ArticleId: targetArticleId
        );

        // When queried by targeted ArticleId, returns only the matching docs
        mockRepo.Setup(x => x.GetDocumentsByArticleIdsAsync(It.Is<IEnumerable<string>>(ids => ids.Contains(targetArticleId))))
            .ReturnsAsync(new List<Document> { englishDoc, danishDoc });

        // Danish candidates cache
        mockRepo.Setup(x => x.GetDanishDocumentsAsync())
            .ReturnsAsync(new List<Document> { danishDoc });

        var resolver = new DanishDocumentResolver(
            repository: mockRepo.Object,
            codeParser: new EbmProductCodeParser()
        );

        var referencedChunks = new List<DocumentChunk>
        {
            new("chunk-1", "doc-1", "Airflow is 150 m3/h", 0, englishDoc.FileName, englishDoc.FilePath, ArticleId: targetArticleId)
        };

        // Act
        var result = (await resolver.FindDanishCompanionDocumentsAsync(referencedChunks)).ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal(danishDoc.FileName, result[0].FileName);

        // Targeted indexed query was called
        mockRepo.Verify(x => x.GetDocumentsByArticleIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Once);
        // GetAllDocumentsAsync was NOT called (avoiding 43,000 document full scan)
        mockRepo.Verify(x => x.GetAllDocumentsAsync(), Times.Never);
    }
}
