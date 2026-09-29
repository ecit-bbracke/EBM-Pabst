using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Llm;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class MultiProviderRagPipelineIntegrationTests
{
    private sealed class DynamicMockHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var contentTask = request.Content?.ReadAsStringAsync(cancellationToken);
            var reqBody = contentTask?.Result ?? "";

            string responseContent;
            if (reqBody.Contains("Input Governor") || reqBody.Contains("intent"))
            {
                responseContent = """
                {
                  "choices": [{
                    "index": 0,
                    "message": {
                      "role": "assistant",
                      "content": "{\"intent\": \"SPEC_LOOKUP\", \"confidence\": 0.95, \"entities\": [{\"type\": \"fan\", \"name\": \"K3G560\"}], \"requested_attributes\": [\"voltage\"], \"constraints\": [], \"clarification_required\": false, \"clarification_reason\": null}"
                    }
                  }],
                  "usage": { "prompt_tokens": 100, "completion_tokens": 30, "total_tokens": 130 }
                }
                """;
            }
            else if (reqBody.Contains("Conversational Query Refiner") || reqBody.Contains("relationship_to_previous_turn"))
            {
                responseContent = """
                {
                  "choices": [{
                    "index": 0,
                    "message": {
                      "role": "assistant",
                      "content": "{\"original_question\": \"What is voltage?\", \"relationship_to_previous_turn\": \"NEW_TOPIC\", \"resolved_question\": \"What is voltage?\", \"effective_question\": \"What is voltage of K3G560?\", \"active_entities\": [{\"type\": \"fan\", \"name\": \"K3G560\"}], \"active_constraints\": [], \"candidate_set\": null, \"constraints_added\": [], \"constraints_removed\": [], \"constraints_replaced\": [], \"references_resolved\": [], \"context_used\": [], \"clarification_required\": false, \"clarification_reason\": null, \"new_topic\": true}"
                    }
                  }],
                  "usage": { "prompt_tokens": 80, "completion_tokens": 25, "total_tokens": 105 }
                }
                """;
            }
            else if (reqBody.Contains("Output Governor") || reqBody.Contains("validate the proposed"))
            {
                responseContent = """
                {
                  "choices": [{
                    "index": 0,
                    "message": {
                      "role": "assistant",
                      "content": "{\"approved\": true, \"issues\": [], \"action\": \"APPROVE\"}"
                    }
                  }],
                  "usage": { "prompt_tokens": 70, "completion_tokens": 15, "total_tokens": 85 }
                }
                """;
            }
            else if (reqBody.Contains("Evidence Layer") || reqBody.Contains("SUPPORTED"))
            {
                responseContent = """
                {
                  "choices": [{
                    "index": 0,
                    "message": {
                      "role": "assistant",
                      "content": "[{\"claim\": \"K3G560 voltage is 400V\", \"status\": \"SUPPORTED\", \"reason\": \"Datasheet\", \"sources\": [\"chunk1\"]}]"
                    }
                  }],
                  "usage": { "prompt_tokens": 90, "completion_tokens": 20, "total_tokens": 110 }
                }
                """;
            }
            else
            {
                responseContent = """
                {
                  "choices": [{
                    "index": 0,
                    "message": { "role": "assistant", "content": "The supply voltage is 380-480 VAC." }
                  }],
                  "usage": { "prompt_tokens": 50, "completion_tokens": 10, "total_tokens": 60 }
                }
                """;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task Orchestrator_ShouldExecuteWithMixedProviders_AndTrackProviderMetadata()
    {
        // Arrange
        var options = new LlmOptions
        {
            DefaultProvider = "Gemini",
            Components = new Dictionary<string, LlmComponentOptions>
            {
                ["QueryRefiner"] = new() { Provider = "Local" },
                ["InputGovernor"] = new() { Provider = "Local" },
                ["EvidenceEvaluator"] = new() { Provider = "Local" },
                ["OutputGovernor"] = new() { Provider = "Local" },
                ["AnswerComposer"] = new() { Provider = "Gemini" }
            },
            Local = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "qwen2.5-7b" }
        };

        var handler = new DynamicMockHttpHandler();
        var localClient = new LocalLlmClient(options.Local, new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") });
        var geminiClient = new GeminiLlmClient(null, "gemini-3.6-flash");

        var resolver = new LlmClientResolver(options, geminiClient, localClient);

        var queryRefiner = new ConversationalQueryRefiner(resolver);
        var inputGovernor = new InputGovernor(resolver);
        var evidenceEvaluator = new EvidenceEvaluator(resolver);
        var outputGovernor = new OutputGovernor(resolver);
        var answerComposer = new AnswerComposer(resolver);
        var workflowRouter = new WorkflowRouter();
        var stateStore = new InMemoryConversationStateStore();

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.SearchAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<DocumentChunk>
            {
                new("chunk1", "doc1", "K3G560 operates on 380-480 VAC 50/60 Hz.", 0)
            });

        var orchestrator = new TechnicalRagOrchestrator(
            inputGovernor,
            workflowRouter,
            evidenceEvaluator,
            answerComposer,
            outputGovernor,
            resolver,
            queryRefiner: queryRefiner,
            stateStore: stateStore
        );

        // Act
        var result = await orchestrator.ProcessQueryAsync("What is the supply voltage of K3G560?", vectorStoreMock.Object);

        // Assert
        Assert.NotNull(result.FinalAnswer);
        Assert.NotNull(result.Trace);
        Assert.NotNull(result.Trace.InputGovernor);
        Assert.Equal("SPEC_LOOKUP", result.Trace.InputGovernor.Intent);
        Assert.NotNull(result.Trace.OutputGovernor);
        Assert.True(result.Trace.OutputGovernor.Approved);
        Assert.NotNull(result.Trace.Timings);
        Assert.True(result.Trace.Timings.ContainsKey("Total"));
    }
}
