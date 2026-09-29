using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Configuration;
using DocumentRagSystem.Infrastructure.Embeddings;
using DocumentRagSystem.Infrastructure.Llm;
using Xunit;
using Xunit.Abstractions;

namespace DocumentRagSystem.UnitTests;

/// <summary>
/// Live integration and benchmark tests for Google Gemini (Embeddings and LLM),
/// dynamically retrieving the API key from .NET User Secrets or environment variables.
/// </summary>
[Trait("Category", "GeminiLiveIntegration")]
public class GeminiLiveQualityAndSpeedTests
{
    private readonly ITestOutputHelper _output;
    private readonly string? _apiKey;

    public GeminiLiveQualityAndSpeedTests(ITestOutputHelper output)
    {
        _output = output;
        _apiKey = TestConfigurationHelper.GetGeminiApiKey();

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _output.WriteLine("[Gemini Live] No GEMINI_API_KEY found in User Secrets or environment. Tests will run in mock mode.");
        }
        else
        {
            _output.WriteLine($"[Gemini Live] API key resolved: {AppConfigurationHelper.MaskApiKey(_apiKey)}");
        }
    }

    [Fact]
    public async Task Live_GeminiEmbeddingService_GeneratesRealEmbeddings()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _output.WriteLine("[Gemini Embedding] Skipping live test because API key is not configured.");
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var embeddingService = new GeminiEmbeddingService(_apiKey, "gemini-embedding-001", httpClient);

        var sw = Stopwatch.StartNew();
        var embedding = await embeddingService.GenerateEmbeddingAsync("RadiPac centrifugal fan with EC motor and integrated electronics");
        sw.Stop();

        _output.WriteLine($"[Gemini Embedding] Generated {embedding.Length}-dim vector in {sw.ElapsedMilliseconds}ms.");

        Assert.NotNull(embedding);
        Assert.Equal(768, embedding.Length);
        Assert.True(embedding.Any(v => v != 0f), "Embedding vector should contain non-zero float values.");
    }

    [Fact]
    public async Task Live_GeminiLlmClient_IntentTaxonomyAndSpeed()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _output.WriteLine("[Gemini LLM] Skipping live test because API key is not configured.");
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new GeminiLlmClient(_apiKey, "gemini-3.6-flash", httpClient: httpClient);
        var governor = new InputGovernor(client);

        var cases = new (string Question, string ExpectedIntent)[]
        {
            ("What is the nominal supply voltage for K3G560?", "SPEC_LOOKUP"),
            ("Compare A6E450 and S4E350 airflow characteristics", "COMPARISON"),
            ("Can A6E450AP0201 replace A6E450AP0202 directly?", "COMPATIBILITY"),
            ("Fan stops immediately on startup with fault code 3", "TROUBLESHOOTING"),
            ("List all available RadiPac fan models", "OVERVIEW")
        };

        int correct = 0;
        var latencies = new List<long>();

        foreach (var (q, expected) in cases)
        {
            var sw = Stopwatch.StartNew();
            var res = await governor.GovernInputAsync(q);
            sw.Stop();
            latencies.Add(sw.ElapsedMilliseconds);

            bool isMatch = string.Equals(res.Intent, expected, StringComparison.OrdinalIgnoreCase);
            if (isMatch) correct++;

            _output.WriteLine($"[Gemini InputGovernor] Q: \"{q}\" -> Intent: {res.Intent} (Expected: {expected}, Match: {isMatch}, Latency: {sw.ElapsedMilliseconds}ms)");
        }

        double accuracy = (double)correct / cases.Length * 100.0;
        _output.WriteLine($"===> Gemini InputGovernor Accuracy: {accuracy:F1}%, Avg Latency: {latencies.Average():F0}ms, Median: {latencies.OrderBy(x => x).ElementAt(latencies.Count / 2)}ms");

        Assert.True(accuracy >= 80.0, $"Expected at least 80% intent accuracy from Gemini, got {accuracy}%");
    }

    [Fact]
    public async Task Live_GeminiLlmClient_StructuredJsonCompletion()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _output.WriteLine("[Gemini JSON] Skipping live test because API key is not configured.");
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new GeminiLlmClient(_apiKey, "gemini-3.6-flash", httpClient: httpClient);

        var request = LlmRequest.FromPrompt(
            "Extract technical attributes for fan model K3G560-PW04-01: 3~ 380-480 VAC, 50/60 Hz, 3.10 kW, 5.0 A, 1850 rpm.",
            requireJson: true,
            temperature: 0.0,
            systemInstruction: "You are a technical data extraction agent. Respond only with valid JSON.");

        var sw = Stopwatch.StartNew();
        var result = await client.GenerateTextAsync(request);
        sw.Stop();

        _output.WriteLine($"[Gemini Structured JSON] Duration: {sw.ElapsedMilliseconds}ms, Metadata Duration: {result.Metadata.DurationMs}ms, Tokens: in={result.Metadata.InputTokens}, out={result.Metadata.OutputTokens}");
        _output.WriteLine($"Result: {result.Content}");

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.Content));
        Assert.True(result.Metadata.StructuredOutputValid, "Output should be valid JSON.");

        using var doc = JsonDocument.Parse(result.Content);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Live_Gemini_OutputGovernor_SafetyHazardRejection()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _output.WriteLine("[Gemini OutputGovernor] Skipping live test because API key is not configured.");
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new GeminiLlmClient(_apiKey, "gemini-3.6-flash", httpClient: httpClient);
        var governor = new OutputGovernor(client);

        var chunks = new List<DocumentChunk>
        {
            new("c1", "doc1", "A6E450AP0201 is an axial fan with Airflow direction V. A6E450AP0202 is an axial fan with Airflow direction A (intake over struts).", 0)
        };

        var sw = Stopwatch.StartNew();
        var res = await governor.ValidateOutputAsync(
            "Can A6E450AP0201 replace A6E450AP0202?",
            "Yes, A6E450AP0201 is a direct 100% plug-and-play drop-in replacement for A6E450AP0202 without any airflow direction changes.",
            chunks);
        sw.Stop();

        _output.WriteLine($"[Gemini OutputGovernor] Latency: {sw.ElapsedMilliseconds}ms, Approved: {res.Approved}, Action: {res.Action}, Issues: {res.Issues.Count}");

        Assert.True(!res.Approved || res.Action != "APPROVE", "Output Governor should reject claim claiming opposite airflow fans are drop-in replacements.");
    }
}
