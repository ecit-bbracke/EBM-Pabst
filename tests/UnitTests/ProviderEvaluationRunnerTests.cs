using System;
using System.Net.Http;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Evaluation;
using DocumentRagSystem.Infrastructure.Llm;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class ProviderEvaluationRunnerTests
{
    [Fact]
    public async Task SideBySideEvaluator_ShouldProduceComparisonReportAcrossProviders()
    {
        // Arrange
        var apiKey = TestConfigurationHelper.GetGeminiApiKey();
        var options = new LlmOptions
        {
            EvaluationMode = true,
            DefaultProvider = "Gemini",
            Gemini = new GeminiOptions { ApiKey = apiKey, LlmModel = "gemini-3.6-flash" },
            Local = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "local-model" }
        };

        var geminiClient = new GeminiLlmClient(apiKey, "gemini-3.6-flash");
        var localClient = new LocalLlmClient(options.Local, new HttpClient());
        var resolver = new LlmClientResolver(options, geminiClient, localClient);

        var evaluator = new ProviderSideBySideEvaluator(resolver);

        // Act
        var report = await evaluator.RunAsync(new[] { "Gemini" });

        // Assert
        Assert.NotNull(report);
        Assert.Single(report.Providers);
        Assert.NotEmpty(report.Metrics);

        var mdTable = report.GenerateMarkdownTable();
        Assert.Contains("Side-by-Side LLM Provider Evaluation Report", mdTable);
        Assert.Contains("InputGovernor", mdTable);
        Assert.Contains("QueryRefiner", mdTable);
        Assert.Contains("EvidenceEvaluator", mdTable);
        Assert.Contains("OutputGovernor", mdTable);
        Assert.Contains("AnswerComposer", mdTable);
    }
}
