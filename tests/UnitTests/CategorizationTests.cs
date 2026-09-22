using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class CategorizationTests
{
    [Theory]
    [InlineData("What is the input voltage of ABC-500?", "SPEC_LOOKUP")]
    [InlineData("How do I install the K3G560 fan?", "PROCEDURE")]
    [InlineData("Compare Model K3G560 and Model K3G400 parameters.", "COMPARISON")]
    [InlineData("Can the ABC-500 controller be wired directly to the XYZ-20 sensor?", "COMPATIBILITY")]
    [InlineData("Why does the device show a blinking red light and error code 4?", "TROUBLESHOOTING")]
    [InlineData("Calculate the total electrical load for three fans at 230V and 1.5A.", "CALCULATION")]
    [InlineData("Design a multi-fan ventilation system for a 100sqm room.", "DESIGN")]
    [InlineData("Hello, can you help me with something general?", "CLARIFICATION")]
    public async Task InputGovernor_ShouldCorrectlyMapDifferentUserQuestionsToExpectedIntents(string question, string expectedIntent)
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync($$"""
            {
              "intent": "{{expectedIntent}}",
              "confidence": 0.98,
              "entities": [],
              "requested_attributes": [],
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        var governor = new InputGovernor(mockLlm.Object);

        // Act
        var result = await governor.GovernInputAsync(question);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedIntent, result.Intent);
        Assert.True(result.Confidence > 0.9);
    }

    [Fact]
    public async Task InputGovernor_ShouldCorrectlyExtractEntitiesAndAttributes()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "intent": "COMPATIBILITY",
              "confidence": 0.95,
              "entities": [
                { "type": "controller", "name": "ABC-500" },
                { "type": "sensor", "name": "XYZ-20" }
              ],
              "requested_attributes": ["supply_voltage", "signal_type", "communication_protocol"],
              "constraints": ["must operate below 50 degrees C"],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        var governor = new InputGovernor(mockLlm.Object);

        // Act
        var result = await governor.GovernInputAsync("Is ABC-500 compatible with XYZ-20 sensor under 50C?");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("COMPATIBILITY", result.Intent);
        Assert.Equal(2, result.Entities.Count);
        Assert.Equal("ABC-500", result.Entities[0].Name);
        Assert.Equal("controller", result.Entities[0].Type);
        Assert.Equal("XYZ-20", result.Entities[1].Name);
        Assert.Equal("sensor", result.Entities[1].Type);
        Assert.Contains("supply_voltage", result.RequestedAttributes);
        Assert.Contains("must operate below 50 degrees C", result.Constraints);
    }

    [Fact]
    public async Task InputGovernor_ShouldIdentifyWhenClarificationIsRequired()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "intent": "CLARIFICATION",
              "confidence": 0.99,
              "entities": [],
              "requested_attributes": [],
              "constraints": [],
              "clarification_required": true,
              "clarification_reason": "User did not specify which controller or sensor model they are referring to."
            }
            """);

        var governor = new InputGovernor(mockLlm.Object);

        // Act
        var result = await governor.GovernInputAsync("Is my controller compatible with my sensor?");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("CLARIFICATION", result.Intent);
        Assert.True(result.ClarificationRequired);
        Assert.Equal("User did not specify which controller or sensor model they are referring to.", result.ClarificationReason);
    }

    [Theory]
    [InlineData("```json\n{\n  \"intent\": \"SPEC_LOOKUP\",\n  \"confidence\": 0.95,\n  \"entities\": [],\n  \"requested_attributes\": [],\n  \"constraints\": [],\n  \"clarification_required\": false,\n  \"clarification_reason\": null\n}\n```")]
    [InlineData("```\n{\n  \"intent\": \"SPEC_LOOKUP\",\n  \"confidence\": 0.95,\n  \"entities\": [],\n  \"requested_attributes\": [],\n  \"constraints\": [],\n  \"clarification_required\": false,\n  \"clarification_reason\": null\n}\n```")]
    [InlineData("   {\n  \"intent\": \"SPEC_LOOKUP\",\n  \"confidence\": 0.95,\n  \"entities\": [],\n  \"requested_attributes\": [],\n  \"constraints\": [],\n  \"clarification_required\": false,\n  \"clarification_reason\": null\n}   ")]
    public async Task InputGovernor_ShouldParseMarkdownFencedJsonResponsesCorrectly(string responseString)
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync(responseString);

        var governor = new InputGovernor(mockLlm.Object);

        // Act
        var result = await governor.GovernInputAsync("What is the power of K3G560?");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SPEC_LOOKUP", result.Intent);
        Assert.Equal(0.95, result.Confidence);
    }

    [Fact]
    public async Task InputGovernor_ShouldFallbackToSpecLookup_WhenLlmReturnsMalformedJson()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("This is totally invalid JSON or a plain text error message from LLM");

        var governor = new InputGovernor(mockLlm.Object);

        // Act
        var result = await governor.GovernInputAsync("What is the power of a general device without product number?");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SPEC_LOOKUP", result.Intent);
        Assert.Equal(0.5, result.Confidence);
        Assert.Empty(result.Entities);
        Assert.False(result.ClarificationRequired);
    }

    [Fact]
    public async Task InputGovernor_ShouldExtractAndEnrichEbmProductsFromQuestion()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "intent": "COMPARISON",
              "confidence": 0.95,
              "entities": [],
              "requested_attributes": [],
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        var governor = new InputGovernor(mockLlm.Object);

        // Act
        var result = await governor.GovernInputAsync("Sammenlign A6E450-AP02-01 og A6E450-AP02-02");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("COMPARISON", result.Intent);
        Assert.NotNull(result.ParsedEbmProducts);
        Assert.Equal(2, result.ParsedEbmProducts.Count);
        Assert.Contains(result.Entities, e => e.Name.Contains("A6E450"));
        Assert.Contains(result.RequestedAttributes, a => a == "airflow_direction");
    }

    [Theory]
    [InlineData("SPEC_LOOKUP", WorkflowType.SimpleRag)]
    [InlineData("PROCEDURE", WorkflowType.SimpleRag)]
    [InlineData("COMPARISON", WorkflowType.Comparison)]
    [InlineData("COMPATIBILITY", WorkflowType.Compatibility)]
    [InlineData("TROUBLESHOOTING", WorkflowType.Diagnostic)]
    [InlineData("CALCULATION", WorkflowType.Calculation)]
    [InlineData("DESIGN", WorkflowType.Design)]
    [InlineData("CLARIFICATION", WorkflowType.Clarification)]
    [InlineData("UNKNOWN_INTENT_FALLBACK", WorkflowType.SimpleRag)]
    public void WorkflowRouter_ShouldDeterministicallyRouteIntentsToCorrectWorkflows(string intent, WorkflowType expectedWorkflow)
    {
        // Arrange
        var router = new WorkflowRouter();
        var govResult = new InputGovernorResult(
            Intent: intent,
            Confidence: 0.9,
            Entities: new(),
            RequestedAttributes: new(),
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null
        );

        // Act
        var result = router.RouteWorkflow(govResult);

        // Assert
        Assert.Equal(expectedWorkflow, result);
    }
}
