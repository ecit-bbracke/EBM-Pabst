using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class LatexMathFormattingTests
{
    [Fact]
    public void CleanLatexMath_ConvertsUserToleranceAndSubscriptExample_Correctly()
    {
        // Arrange - exact example reported by user
        var rawInput = " 10 VDC $\\pm 3%$ ($I_{\\text{max}} = 1,1\\text{ mA}$). ";

        // Act
        var result = Program.CleanLatexMath(rawInput);

        // Assert
        result.Should().Be(" 10 VDC ± 3% (I_max = 1,1 mA). ");
    }

    [Fact]
    public void CleanLatexMath_ConvertsCommonEngineeringSymbols()
    {
        // Arrange
        var rawInput = "Luftmængde $q_v \\approx 1200\\text{ m}^3\\text{/h}$ ved $\\Delta p \\ge 150\\text{ Pa}$, $T \\le 60\\degree C$, $C = 4\\mu F$";

        // Act
        var result = Program.CleanLatexMath(rawInput);

        // Assert
        result.Should().Contain("≈ 1200 m³/h");
        result.Should().Contain("Δ p ≥ 150 Pa");
        result.Should().Contain("≤ 60° C");
        result.Should().Contain("4µ F");
        result.Should().NotContain("$");
        result.Should().NotContain("\\text");
    }

    [Fact]
    public async Task AnswerComposer_PromptExplicitlyForbidsLatex()
    {
        // Arrange
        var mockLlm = new Mock<ILlmClient>();
        string capturedPrompt = "";

        var metadata = new LlmExecutionMetadata("Mock", "mock-model", 5, 10, 10, true, 1);

        mockLlm.Setup(x => x.GenerateTextAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) =>
            {
                capturedPrompt = req.Messages.Last().Content;
            })
            .ReturnsAsync(new LlmTextResult("ok", metadata));

        var composer = new AnswerComposer(mockLlm.Object);
        var govResult = new InputGovernorResult("SPEC_LOOKUP", 0.95, new List<EntityInfo>(), new List<string>(), new List<string>(), false, null);

        // Act
        await composer.ComposeAnswerAsync("Hvad er tolerancen?", govResult, new List<EvidenceClaim>(), new List<DocumentChunk>());

        // Assert
        capturedPrompt.Should().Contain("NO LATEX");
        capturedPrompt.Should().Contain("± 3%");
    }

    [Fact]
    public void CitationDto_SerializesWithCamelCaseProperties_IncludingIndex()
    {
        // Arrange
        var citation = new DocumentRagSystem.WebApi.DTOs.CitationDto(
            ChunkId: "10327430_chunk_4",
            DocumentId: "10327430",
            Text: "sample text",
            Index: 4,
            filename: "Art_8300102253-Doc_10327430.pdf",
            filepath: "/uploads/test.pdf"
        );

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize(citation);

        // Assert
        json.Should().Contain("\"index\":4", "Property 'Index' must serialize to camelCase 'index' so UI extracts page numbers correctly.");
        json.Should().Contain("\"chunkId\":\"10327430_chunk_4\"");
        json.Should().Contain("\"documentId\":\"10327430\"");
    }
}
