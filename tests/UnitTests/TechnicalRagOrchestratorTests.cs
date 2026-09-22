using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class TechnicalRagOrchestratorTests
{
    [Fact]
    public async Task InputGovernor_ShouldClassifyIntentCorrectly()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "intent": "COMPATIBILITY",
              "confidence": 0.94,
              "entities": [
                { "type": "controller", "name": "ABC-500" },
                { "type": "sensor", "name": "XYZ-20" }
              ],
              "requested_attributes": ["supply_voltage", "signal_type"],
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        var governor = new InputGovernor(mockLlm.Object);

        // Act
        var result = await governor.GovernInputAsync("Can controller ABC-500 work with sensor XYZ-20?");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("COMPATIBILITY", result.Intent);
        Assert.Equal(0.94, result.Confidence);
        Assert.Equal(2, result.Entities.Count);
        Assert.Equal("ABC-500", result.Entities[0].Name);
    }

    [Fact]
    public void WorkflowRouter_ShouldRouteCorrectly()
    {
        // Arrange
        var router = new WorkflowRouter();
        var govResult = new InputGovernorResult(
            Intent: "TROUBLESHOOTING",
            Confidence: 0.9,
            Entities: new(),
            RequestedAttributes: new(),
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null
        );

        // Act
        var route = router.RouteWorkflow(govResult);

        // Assert
        Assert.Equal(WorkflowType.Diagnostic, route);
    }

    [Fact]
    public async Task EvidenceEvaluator_ShouldClassifyClaimsCorrectly()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            [
              {
                "claim": "Maximum voltage is 24V",
                "status": "SUPPORTED",
                "reason": "Explicitly stated in manual.",
                "sources": ["manual.pdf"]
              },
              {
                "claim": "Approved for outdoor use",
                "status": "MISSING",
                "reason": "No outdoor approval found in the provided documentation.",
                "sources": []
              }
            ]
            """);

        var evaluator = new EvidenceEvaluator(mockLlm.Object);
        var chunks = new[] { new DocumentChunk("c1", "doc1", "Voltage range: 12-24V", 0) };

        // Act
        var result = await evaluator.EvaluateEvidenceAsync("What is the voltage?", "The maximum voltage is 24V. It is also approved for outdoor use.", chunks);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("SUPPORTED", result[0].Status);
        Assert.Equal("MISSING", result[1].Status);
    }

    [Fact]
    public async Task TechnicalRagOrchestrator_ShouldExecuteIterativeRetrieval_WhenEvidenceIsMissing()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        
        // 1. Input Governor mock response
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""
            {
              "intent": "SPEC_LOOKUP",
              "confidence": 0.9,
              "entities": [],
              "requested_attributes": [],
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        // 2. First draft generation
        mockLlm.Setup(x => x.GenerateResponseAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DocumentChunk>>()))
            .ReturnsAsync("First draft response.");

        // 3. First evidence evaluation (claims that a claim is MISSING)
        mockLlm.SetupSequence(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Evidence Layer")), true))
            .ReturnsAsync("""
            [
              {
                "claim": "Required missing info",
                "status": "MISSING",
                "reason": "Missing from first retrieval.",
                "sources": []
              }
            ]
            """) // First evaluation
            .ReturnsAsync("""
            [
              {
                "claim": "Required missing info",
                "status": "SUPPORTED",
                "reason": "Found in second retrieval.",
                "sources": ["manual.pdf"]
              }
            ]
            """); // Second evaluation (now resolved)

        // 4. Targeted query generation
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("missing or conflicting")), false))
            .ReturnsAsync("targeted query");

        // 5. Answer composer & Output Governor approval
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("Final Composed Answer");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.SetupSequence(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "doc1", "Initial text", 0) })
            .ReturnsAsync(new[] { new DocumentChunk("c2", "doc1", "Targeted retrieved text", 1) });

        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object
        );

        // Act
        var (answer, chunks, trace) = await orchestrator.ProcessQueryAsync("How do I fix error 4?", mockVectorStore.Object);

        // Assert
        Assert.Equal("Final Composed Answer", answer);
        Assert.Equal(2, chunks.Count); // Initial chunk + Targeted chunk
        Assert.Equal("SimpleRag", trace.Workflow);
        Assert.True(trace.Retrievals.Count >= 2); // Verify that a second retrieval occurred
    }

    [Fact]
    public async Task OutputGovernor_ShouldRejectUnsupportedClaims()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "approved": false,
              "issues": [
                {
                  "type": "UNSUPPORTED_CLAIM",
                  "claim": "The device is waterproof",
                  "severity": "HIGH"
                }
              ],
              "action": "REGENERATE"
            }
            """);

        var governor = new OutputGovernor(mockLlm.Object);
        var chunks = new[] { new DocumentChunk("c1", "doc1", "IP20 rating (dry indoor use only)", 0) };

        // Act
        var result = await governor.ValidateOutputAsync("Is the unit outdoor proof?", "Yes, the device is waterproof and outdoor proof.", chunks);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Approved);
        Assert.Equal("REGENERATE", result.Action);
        Assert.Single(result.Issues);
        Assert.Equal("UNSUPPORTED_CLAIM", result.Issues[0].Type);
    }
}
