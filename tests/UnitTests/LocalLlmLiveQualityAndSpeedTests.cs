using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Llm;
using Xunit;
using Xunit.Abstractions;

namespace DocumentRagSystem.UnitTests;

/// <summary>
/// Live integration tests benchmarking the quality and speed of an actual Local LLM server running at
/// http://localhost:8080/v1/chat/completions (or overridden via LOCAL_LLM_URL / LOCAL_LLM_MODEL environment variables).
/// Skips automatically if the server is not reachable, ensuring standard CI builds always pass without requiring a GPU.
/// </summary>
[Trait("Category", "LocalLlmIntegration")]
public class LocalLlmLiveQualityAndSpeedTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _baseUrl;
    private readonly string _model;

    public LocalLlmLiveQualityAndSpeedTests(ITestOutputHelper output)
    {
        _output = output;
        _baseUrl = Environment.GetEnvironmentVariable("LOCAL_LLM_URL") ?? "http://localhost:8080";
        _model = Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL") ?? "local-model";
    }

    private async Task<LocalLlmClient?> CreateLiveClientIfAvailableAsync()
    {
        using var testClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var pingUrl = _baseUrl.TrimEnd('/') + "/v1/models";
            var resp = await testClient.GetAsync(pingUrl);
            if (!resp.IsSuccessStatusCode)
            {
                _output.WriteLine($"[Live Local LLM] Server at {_baseUrl} returned {resp.StatusCode}. Skipping live benchmark.");
                return null;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[Live Local LLM] Server at {_baseUrl} not reachable ({ex.Message}). Skipping live benchmark.");
            return null;
        }

        var options = new LocalLlmOptions
        {
            BaseUrl = _baseUrl,
            Model = _model,
            TimeoutSeconds = 60,
            MaxStructuredRetries = 1
        };

        var httpClient = new HttpClient { BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/") };
        return new LocalLlmClient(options, httpClient);
    }

    [Fact]
    public async Task Live_InputGovernor_IntentTaxonomyQualityAndSpeed()
    {
        var client = await CreateLiveClientIfAvailableAsync();
        if (client == null) return;

        var governor = new InputGovernor(client);

        var cases = new (string Question, string ExpectedIntent)[]
        {
            ("What is the nominal voltage for K3G560?", "SPEC_LOOKUP"),
            ("Compare A6E450 and S4E350 airflow characteristics", "COMPARISON"),
            ("Can A6E450AP0201 replace A6E450AP0202?", "COMPATIBILITY"),
            ("Fan stops immediately with error code 3", "TROUBLESHOOTING"),
            ("List all available fan models in the system", "OVERVIEW")
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

            _output.WriteLine($"[Live InputGovernor] Question: \"{q}\" -> Intent: {res.Intent} (Expected: {expected}, Match: {isMatch}, Latency: {sw.ElapsedMilliseconds}ms, Entities: {res.Entities?.Count ?? 0})");
        }

        double accuracy = (double)correct / cases.Length * 100.0;
        _output.WriteLine($"===> InputGovernor Accuracy: {accuracy:F1}%, Avg Latency: {latencies.Average():F0}ms, Median: {latencies.OrderBy(x=>x).ElementAt(latencies.Count/2)}ms");
        Assert.True(accuracy >= 80.0, $"Expected at least 80% intent accuracy from local model, got {accuracy}%");
    }

    [Fact]
    public async Task Live_ConversationalQueryRefiner_ReferenceResolutionQualityAndSpeed()
    {
        var client = await CreateLiveClientIfAvailableAsync();
        if (client == null) return;

        var refiner = new ConversationalQueryRefiner(client);

        var priorState = new ConversationState(
            ConversationId: Guid.NewGuid().ToString(),
            Topic: "fan_selection",
            ActiveEntities: new List<ConversationEntity> { new("axial_fan", "A6E450AP0201") },
            ActiveConstraints: new List<ConversationConstraint> { new("supply_voltage", "equals", "230 VAC", "VAC", 1) },
            TurnCount: 1,
            LastEffectiveQuestion: "What is the airflow for A6E450AP0201?"
        );

        var sw = Stopwatch.StartNew();
        var res = await refiner.RefineQueryAsync("What if we use 400 VAC instead?", priorState);
        sw.Stop();

        _output.WriteLine($"[Live QueryRefiner] Latency: {sw.ElapsedMilliseconds}ms, Relationship: {res.RelationshipToPreviousTurn}, Effective: \"{res.EffectiveQuestion}\"");
        Assert.NotNull(res.EffectiveQuestion);
        Assert.Contains("400", res.EffectiveQuestion);
    }

    [Fact]
    public async Task Live_OutputGovernor_SafetyHazardRejectionQualityAndSpeed()
    {
        var client = await CreateLiveClientIfAvailableAsync();
        if (client == null) return;

        var governor = new OutputGovernor(client);
        var chunks = new List<DocumentChunk>
        {
            new("c1", "doc1", "A6E450AP0201 is an axial fan with Airflow direction V. A6E450AP0202 is an axial fan with Airflow direction A.", 0)
        };

        // Safety contradiction test: Claiming opposite airflow fans are interchangeable
        var sw = Stopwatch.StartNew();
        var res = await governor.ValidateOutputAsync(
            "Can A6E450AP0201 replace A6E450AP0202?",
            "Yes, A6E450AP0201 is a direct drop-in replacement for A6E450AP0202 without any changes.",
            chunks);
        sw.Stop();

        _output.WriteLine($"[Live OutputGovernor] Safety Test Latency: {sw.ElapsedMilliseconds}ms, Approved: {res.Approved}, Action: {res.Action}, Issues: {res.Issues?.Count ?? 0}");
        // Must either reject or request regeneration/failsafe due to airflow direction mismatch
        Assert.True(!res.Approved || res.Action != "APPROVE", "Output Governor should reject direct drop-in claim for opposite airflow fans.");
    }

    [Fact]
    public async Task Live_Streaming_TimeToFirstTokenAndThroughputSpeed()
    {
        var client = await CreateLiveClientIfAvailableAsync();
        if (client == null) return;

        var prompt = "List 3 advantages of EC fans over AC fans in industrial ventilation. Be concise.";
        var req = LlmRequest.FromPrompt(prompt, temperature: 0.2);

        var totalSw = Stopwatch.StartNew();
        var ttftSw = Stopwatch.StartNew();
        long ttftMs = -1;
        int tokenCount = 0;
        var sb = new StringBuilder();

        await foreach (var token in client.StreamTextAsync(req))
        {
            if (ttftMs < 0)
            {
                ttftMs = ttftSw.ElapsedMilliseconds;
            }
            tokenCount++;
            sb.Append(token);
        }
        totalSw.Stop();

        double tokensPerSec = totalSw.Elapsed.TotalSeconds > 0 ? tokenCount / totalSw.Elapsed.TotalSeconds : 0;
        _output.WriteLine($"[Live Streaming Benchmark] TTFT: {ttftMs}ms, Total Duration: {totalSw.ElapsedMilliseconds}ms, Chunks: {tokenCount}, Throughput: ~{tokensPerSec:F1} tokens/sec");
        _output.WriteLine($"Response Preview: {sb.ToString().Trim()}");

        Assert.True(tokenCount > 0, "Expected to receive streamed tokens from local LLM.");
        Assert.True(ttftMs >= 0, "TTFT should be measured.");
    }
}
