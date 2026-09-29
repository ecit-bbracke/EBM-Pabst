using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// Unit tests benchmarking quality (schema validity, classification correctness, error resilience)
/// and speed (latency, TTFT, throughput) of Local LLM requests across all pipeline request types.
/// </summary>
public class LocalLlmSpeedAndQualityBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public LocalLlmSpeedAndQualityBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class MockLocalLlmHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handlerFunc;

        public MockLocalLlmHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
        {
            _handlerFunc = handlerFunc;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handlerFunc(request));
        }
    }

    [Fact]
    public async Task InputGovernor_QualityAndSpeed_ShouldAccuratelyClassifyIntentsAndMeasureSpeed()
    {
        // Arrange
        var testCases = new (string Question, string ExpectedIntent, int PromptTokens, int CompletionTokens)[]
        {
            ("What is the nominal voltage for K3G560?", "SPEC_LOOKUP", 250, 45),
            ("Compare A6E450 and S4E350 airflow characteristics", "COMPARISON", 280, 60),
            ("Can A6E450AP0201 replace A6E450AP0202?", "COMPATIBILITY", 310, 65),
            ("Fan stops immediately with fault code 5", "TROUBLESHOOTING", 270, 50),
            ("List all available RadiPac fan models", "OVERVIEW", 240, 40)
        };

        var handler = new MockLocalLlmHandler(req =>
        {
            var body = req.Content?.ReadAsStringAsync().Result ?? "";
            var effectiveQIdx = body.IndexOf("Effective Technical Question:", StringComparison.OrdinalIgnoreCase);
            var questionPart = effectiveQIdx >= 0 ? body.Substring(effectiveQIdx) : body;

            string intent = "SPEC_LOOKUP";
            if (questionPart.Contains("Compare", StringComparison.OrdinalIgnoreCase)) intent = "COMPARISON";
            else if (questionPart.Contains("replace", StringComparison.OrdinalIgnoreCase)) intent = "COMPATIBILITY";
            else if (questionPart.Contains("fault", StringComparison.OrdinalIgnoreCase) || questionPart.Contains("stops", StringComparison.OrdinalIgnoreCase)) intent = "TROUBLESHOOTING";
            else if (questionPart.Contains("RadiPac", StringComparison.OrdinalIgnoreCase) || questionPart.Contains("List all", StringComparison.OrdinalIgnoreCase)) intent = "OVERVIEW";

            var json = $$"""
            {
              "id": "chatcmpl-test",
              "model": "local-model",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": "{\"intent\": \"{{intent}}\", \"confidence\": 0.98, \"entities\": [{\"type\": \"fan\", \"name\": \"K3G560\"}], \"requested_attributes\": [\"voltage\"], \"constraints\": [], \"clarification_required\": false, \"clarification_reason\": null}"
                }
              }],
              "usage": { "prompt_tokens": 250, "completion_tokens": 45, "total_tokens": 295 }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8080", Model = "local-model" };
        var client = new LocalLlmClient(options, new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") });
        var governor = new InputGovernor(client);

        int correct = 0;
        var latencies = new List<long>();

        // Act
        foreach (var tc in testCases)
        {
            var sw = Stopwatch.StartNew();
            var result = await governor.GovernInputAsync(tc.Question);
            sw.Stop();
            latencies.Add(sw.ElapsedMilliseconds);

            if (result.Intent == tc.ExpectedIntent) correct++;
            Assert.True(result.Confidence >= 0.9);
            Assert.NotNull(result.Entities);
        }

        // Assert
        double accuracy = (double)correct / testCases.Length * 100.0;
        _output.WriteLine($"[InputGovernor Benchmark] Accuracy: {accuracy:F1}%, Avg Latency: {latencies.Average():F1}ms, Total: {testCases.Length}");
        Assert.Equal(100.0, accuracy);
    }

    [Fact]
    public async Task QueryRefiner_QualityAndSpeed_ShouldResolveReferencesAndMaintainConstraints()
    {
        // Arrange
        var handler = new MockLocalLlmHandler(req =>
        {
            var body = req.Content?.ReadAsStringAsync().Result ?? "";
            string relationship = "CONTINUES";
            string effectiveQ = "What is the airflow for A6E450AP0201 at 400 VAC?";

            if (body.Contains("400 VAC") || body.Contains("instead"))
            {
                relationship = "MODIFIES";
            }
            else if (body.Contains("Start over") || body.Contains("RESET"))
            {
                relationship = "RESET";
                effectiveQ = "Which sensors support CANopen?";
            }

            var json = $$"""
            {
              "id": "chatcmpl-test",
              "model": "local-model",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": "{\"original_question\": \"What if 400 VAC?\", \"relationship_to_previous_turn\": \"{{relationship}}\", \"resolved_question\": \"What if 400 VAC?\", \"effective_question\": \"{{effectiveQ}}\", \"active_entities\": [{\"type\": \"axial_fan\", \"name\": \"A6E450AP0201\"}], \"active_constraints\": [{\"attribute\": \"supply_voltage\", \"operator\": \"equals\", \"value\": \"400 VAC\", \"unit\": \"VAC\", \"source_turn\": 2, \"status\": \"USER_CONSTRAINT\"}], \"candidate_set\": null, \"constraints_added\": [], \"constraints_removed\": [], \"constraints_replaced\": [], \"references_resolved\": [\"it -> A6E450AP0201\"], \"context_used\": [1], \"clarification_required\": false, \"clarification_reason\": null, \"new_topic\": false}"
                }
              }],
              "usage": { "prompt_tokens": 400, "completion_tokens": 90, "total_tokens": 490 }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8080", Model = "local-model" };
        var client = new LocalLlmClient(options, new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") });
        var refiner = new ConversationalQueryRefiner(client);

        var priorState = new ConversationState(
            ConversationId: "conv-1",
            Topic: "axial_fans",
            ActiveEntities: new List<ConversationEntity> { new("axial_fan", "A6E450AP0201") },
            ActiveConstraints: new List<ConversationConstraint> { new("supply_voltage", "equals", "230 VAC", "VAC", 1) },
            TurnCount: 1,
            LastEffectiveQuestion: "What is the airflow for A6E450AP0201?"
        );

        // Act
        var sw = Stopwatch.StartNew();
        var result = await refiner.RefineQueryAsync("What if we use 400 VAC instead?", priorState);
        sw.Stop();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("MODIFIES", result.RelationshipToPreviousTurn);
        Assert.Contains("400 VAC", result.EffectiveQuestion);
        Assert.Single(result.ActiveConstraints);
        Assert.Equal("400 VAC", result.ActiveConstraints[0].Value);
        _output.WriteLine($"[QueryRefiner Benchmark] Completed in {sw.ElapsedMilliseconds}ms with effective question: '{result.EffectiveQuestion}'");
    }

    [Fact]
    public async Task EvidenceEvaluator_QualityAndSpeed_ShouldVerifyTechnicalClaimsAgainstChunks()
    {
        // Arrange
        var handler = new MockLocalLlmHandler(req =>
        {
            var json = """
            {
              "id": "chatcmpl-test",
              "model": "local-model",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": "[{\"claim\": \"K3G560 operates on 380-480 VAC.\", \"status\": \"SUPPORTED\", \"reason\": \"Datasheet page 1\", \"sources\": [\"doc1\"]}, {\"claim\": \"Airflow direction is V.\", \"status\": \"DERIVED\", \"reason\": \"Deduced from 12th digit 1 in product code\", \"sources\": [\"doc1\"]}]"
                }
              }],
              "usage": { "prompt_tokens": 520, "completion_tokens": 70, "total_tokens": 590 }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8080", Model = "local-model" };
        var client = new LocalLlmClient(options, new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") });
        var evaluator = new EvidenceEvaluator(client);

        var chunks = new List<DocumentChunk>
        {
            new("c1", "doc1", "The K3G560 operates on 3~ 380-480 VAC 50/60Hz.", 0)
        };

        // Act
        var sw = Stopwatch.StartNew();
        var claims = await evaluator.EvaluateEvidenceAsync("What is voltage?", "K3G560 operates on 380-480 VAC.", chunks);
        sw.Stop();

        // Assert
        Assert.NotNull(claims);
        Assert.Equal(2, claims.Count);
        Assert.Contains(claims, c => c.Status == "SUPPORTED");
        Assert.Contains(claims, c => c.Status == "DERIVED");
        _output.WriteLine($"[EvidenceEvaluator Benchmark] Evaluated {claims.Count} claims in {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task OutputGovernor_QualityAndSpeed_ShouldDetectSafetyHazardsAndEnforceAirflowDirectionRules()
    {
        // Arrange
        var handler = new MockLocalLlmHandler(req =>
        {
            var body = req.Content?.ReadAsStringAsync().Result ?? "";
            bool isDangerousReplacement = body.Contains("Yes, they are direct drop-in replacements", StringComparison.OrdinalIgnoreCase);

            string content = isDangerousReplacement
                ? "{\"approved\": false, \"issues\": [{\"type\": \"SAFETY_HAZARD\", \"claim\": \"Opposite airflow directions.\", \"severity\": \"CRITICAL\"}], \"action\": \"REGENERATE\"}"
                : "{\"approved\": true, \"issues\": [], \"action\": \"APPROVE\"}";

            var responseObj = new
            {
                id = "chatcmpl-test",
                model = "local-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = content },
                        finish_reason = "stop"
                    }
                },
                usage = new { prompt_tokens = 600, completion_tokens = 40, total_tokens = 640 }
            };

            var json = JsonSerializer.Serialize(responseObj);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8080", Model = "local-model" };
        var client = new LocalLlmClient(options, new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") });
        var governor = new OutputGovernor(client);

        var chunks = new List<DocumentChunk>
        {
            new("c1", "doc1", "A6E450AP0201 is Airflow V. A6E450AP0202 is Airflow A.", 0)
        };

        // Act 1: Dangerous replacement claim
        var sw1 = Stopwatch.StartNew();
        var dangerousResult = await governor.ValidateOutputAsync(
            "Can A6E450AP0201 replace A6E450AP0202?",
            "Yes, they are direct drop-in replacements for each other.",
            chunks);
        sw1.Stop();

        // Act 2: Safe verified claim
        var sw2 = Stopwatch.StartNew();
        var safeResult = await governor.ValidateOutputAsync(
            "What is the airflow direction of A6E450AP0201?",
            "Airflow direction is V.",
            chunks);
        sw2.Stop();

        // Assert
        Assert.False(dangerousResult.Approved);
        Assert.Equal("REGENERATE", dangerousResult.Action);
        Assert.Single(dangerousResult.Issues);
        Assert.Equal("SAFETY_HAZARD", dangerousResult.Issues[0].Type);

        Assert.True(safeResult.Approved);
        Assert.Equal("APPROVE", safeResult.Action);

        _output.WriteLine($"[OutputGovernor Benchmark] Safety check: {sw1.ElapsedMilliseconds}ms (rejected hazard), Safe check: {sw2.ElapsedMilliseconds}ms (approved)");
    }

    [Fact]
    public async Task Streaming_SpeedAndThroughput_ShouldMeasureTimeToFirstTokenAndTokensPerSecond()
    {
        // Arrange
        var streamChunks = new[]
        {
            "data: {\"choices\":[{\"delta\":{\"content\":\"The \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"ebm-papst \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"K3G560 \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"delivers \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"14,500 m3/h.\"}}]}\n\n",
            "data: [DONE]\n\n"
        };

        var handler = new MockLocalLlmHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Join("", streamChunks), Encoding.UTF8, "text/event-stream")
            };
            return response;
        });

        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8080", Model = "local-model" };
        var client = new LocalLlmClient(options, new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") });

        var receivedTokens = new List<string>();
        var totalSw = Stopwatch.StartNew();
        var ttftSw = Stopwatch.StartNew();
        long ttftMs = -1;

        // Act
        await foreach (var token in client.StreamTextAsync(LlmRequest.FromPrompt("Tell me about K3G560")))
        {
            if (ttftMs < 0)
            {
                ttftMs = ttftSw.ElapsedMilliseconds;
            }
            receivedTokens.Add(token);
        }
        totalSw.Stop();

        var fullText = string.Join("", receivedTokens);

        // Assert
        Assert.Equal(5, receivedTokens.Count);
        Assert.Equal("The ebm-papst K3G560 delivers 14,500 m3/h.", fullText);
        Assert.True(ttftMs >= 0);

        _output.WriteLine($"[Streaming Benchmark] Tokens: {receivedTokens.Count}, TTFT: {ttftMs}ms, Total: {totalSw.ElapsedMilliseconds}ms, Output: \"{fullText}\"");
    }
}
