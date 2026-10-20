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
using DocumentRagSystem.Infrastructure.HealthChecks;
using DocumentRagSystem.Infrastructure.Llm;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class LlmClientResolverAndFallbackTests
{
    private sealed class SimpleMockHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public SimpleMockHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public void Resolver_ShouldRouteComponentsToConfiguredProviders()
    {
        // Arrange
        var options = new LlmOptions
        {
            DefaultProvider = "Gemini",
            Components = new Dictionary<string, LlmComponentOptions>
            {
                ["InputGovernor"] = new() { Provider = "Local" },
                ["QueryRefiner"] = new() { Provider = "Local" },
                ["AnswerComposer"] = new() { Provider = "Gemini" }
            },
            Local = new LocalLlmOptions { BaseUrl = "http://localhost:8000" }
        };

        var geminiClient = new GeminiLlmClient(null, "gemini-3.6-flash");
        var localClient = new LocalLlmClient(options.Local, new HttpClient());
        var resolver = new LlmClientResolver(options, geminiClient, localClient);

        // Act
        var inputGovClient = resolver.Resolve(LlmPurpose.InputGovernor);
        var queryRefClient = resolver.Resolve(LlmPurpose.QueryRefiner);
        var answerCompClient = resolver.Resolve(LlmPurpose.AnswerComposer);
        var generalClient = resolver.Resolve(LlmPurpose.General);

        // Assert
        Assert.Equal("Local", inputGovClient.ProviderName);
        Assert.Equal("Local", queryRefClient.ProviderName);
        Assert.Equal("Gemini", answerCompClient.ProviderName);
        Assert.Equal("Gemini", generalClient.ProviderName);
    }

    [Fact]
    public async Task Fallback_ShouldTriggerGemini_WhenLocalFailsInProductionMode()
    {
        // Arrange
        var options = new LlmOptions
        {
            DefaultProvider = "Local",
            EvaluationMode = false,
            Fallback = new LlmFallbackOptions
            {
                Enabled = true,
                From = "Local",
                To = "Gemini"
            },
            Local = new LocalLlmOptions { BaseUrl = "http://localhost:8000" }
        };

        // Local client returns 500 error
        var localHandler = new SimpleMockHandler(HttpStatusCode.InternalServerError, "Service Unavailable");
        var localClient = new LocalLlmClient(options.Local, new HttpClient(localHandler) { BaseAddress = new Uri("http://localhost:8000") });

        // Gemini mock client returns success (mock mode)
        var geminiClient = new GeminiLlmClient(null, "gemini-3.6-flash");

        var resolver = new LlmClientResolver(options, geminiClient, localClient);
        var resolvedClient = resolver.Resolve(LlmPurpose.InputGovernor);

        // Act
        var result = await resolvedClient.GenerateTextAsync(LlmRequest.FromPrompt("Test fallback prompt"));

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Metadata.IsFallback);
        Assert.Equal("Local", result.Metadata.OriginalProvider);
        Assert.Equal("Gemini", result.Metadata.Provider);
        Assert.NotNull(result.Metadata.Attempts);
        Assert.Equal(2, result.Metadata.Attempts.Count);
        Assert.Equal("Local", result.Metadata.Attempts[0].Provider);
        Assert.Equal("failed", result.Metadata.Attempts[0].Status);
        Assert.Equal("Gemini", result.Metadata.Attempts[1].Provider);
        Assert.Equal("success", result.Metadata.Attempts[1].Status);
    }

    [Fact]
    public async Task Fallback_ShouldBeDisabled_InEvaluationMode()
    {
        // Arrange
        var options = new LlmOptions
        {
            DefaultProvider = "Local",
            EvaluationMode = true, // Evaluation mode ON -> NO FALLBACK
            Fallback = new LlmFallbackOptions
            {
                Enabled = true,
                From = "Local",
                To = "Gemini"
            },
            Local = new LocalLlmOptions { BaseUrl = "http://localhost:8000" }
        };

        var localHandler = new SimpleMockHandler(HttpStatusCode.InternalServerError, "Service Unavailable");
        var localClient = new LocalLlmClient(options.Local, new HttpClient(localHandler) { BaseAddress = new Uri("http://localhost:8000") });
        var geminiClient = new GeminiLlmClient(null, "gemini-3.6-flash");

        var resolver = new LlmClientResolver(options, geminiClient, localClient);
        var resolvedClient = resolver.Resolve(LlmPurpose.InputGovernor);

        // Act & Assert
        // Should throw LlmProviderUnavailableException instead of silently masking behind Gemini
        await Assert.ThrowsAsync<LlmProviderUnavailableException>(() =>
            resolvedClient.GenerateTextAsync(LlmRequest.FromPrompt("Test evaluation prompt")));
    }

    [Fact]
    public async Task LocalLlmHealthCheck_ShouldReturnHealthy_WhenEndpointAndModelAvailable()
    {
        // Arrange
        var handler = new SimpleMockHandler(HttpStatusCode.OK, """
        {
          "data": [
            { "id": "qwen2.5-7b", "object": "model" },
            { "id": "llama-3.1-8b", "object": "model" }
          ]
        }
        """);

        var clientFactoryMock = new Mock<IHttpClientFactory>();
        clientFactoryMock.Setup(f => f.CreateClient("LocalLlmHealthClient"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") });

        var options = Options.Create(new LlmOptions
        {
            Local = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "qwen2.5-7b" }
        });

        var healthCheck = new LocalLlmHealthCheck(options, clientFactoryMock.Object);

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("qwen2.5-7b", result.Description);
    }

    [Fact]
    public async Task LocalLlmHealthCheck_ShouldReturnDegraded_WhenModelNotAvailableInList()
    {
        // Arrange
        var handler = new SimpleMockHandler(HttpStatusCode.OK, """
        {
          "data": [
            { "id": "some-other-model", "object": "model" }
          ]
        }
        """);

        var clientFactoryMock = new Mock<IHttpClientFactory>();
        clientFactoryMock.Setup(f => f.CreateClient("LocalLlmHealthClient"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") });

        var options = Options.Create(new LlmOptions
        {
            Local = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "qwen2.5-7b" }
        });

        var healthCheck = new LocalLlmHealthCheck(options, clientFactoryMock.Object);

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task LocalLlmHealthCheck_ShouldReturnUnhealthy_WhenEndpointFails()
    {
        // Arrange
        var handler = new SimpleMockHandler(HttpStatusCode.ServiceUnavailable, "503 Unavailable");

        var clientFactoryMock = new Mock<IHttpClientFactory>();
        clientFactoryMock.Setup(f => f.CreateClient("LocalLlmHealthClient"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") });

        var options = Options.Create(new LlmOptions
        {
            Local = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "qwen2.5-7b" }
        });

        var healthCheck = new LocalLlmHealthCheck(options, clientFactoryMock.Object);

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task GeminiLlmClient_WhenServiceUnavailable_ThrowsHttpRequestException_AndTriggersFallback()
    {
        // Arrange: Gemini returns 503
        var geminiHandler = new SimpleMockHandler(HttpStatusCode.ServiceUnavailable, "503 Gemini Service Unavailable");
        var geminiHttp = new HttpClient(geminiHandler);
        var geminiClient = new GeminiLlmClient("real-gemini-api-key-12345", httpClient: geminiHttp);

        // Local fallback returns valid response
        var localHandler = new SimpleMockHandler(HttpStatusCode.OK, """
        {
          "choices": [
            {
              "message": { "role": "assistant", "content": "{\"intent\":\"SPEC_LOOKUP\"}" }
            }
          ]
        }
        """);
        var localHttp = new HttpClient(localHandler) { BaseAddress = new Uri("http://localhost:8000") };
        var localClient = new LocalLlmClient(new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "qwen2.5-7b" }, localHttp);

        var fallbackClient = new FallbackLlmClient(geminiClient, localClient, fallbackEnabled: true);

        // Act
        var result = await fallbackClient.GenerateTextAsync(new LlmRequest(new[] { new LlmMessage("user", "test") }));

        // Assert: Fallback was invoked instead of generating fabricated mock response!
        Assert.NotNull(result);
        Assert.True(result.Metadata.IsFallback);
        Assert.Equal("Local", result.Metadata.Provider);
    }

    private sealed class FailingMidStreamClient : ILlmClient
    {
        public string ProviderName => "FailingMidStream";
        public Task<LlmTextResult> GenerateTextAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<LlmStructuredResult<T>> GenerateStructuredAsync<T>(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public async IAsyncEnumerable<string> StreamTextAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "First token";
            await Task.Yield();
            throw new HttpRequestException("Simulated mid-stream network drop");
        }
    }

    [Fact]
    public async Task FallbackLlmClient_WhenStreamFailsMidStream_PropagatesException()
    {
        // Arrange
        var primary = new FailingMidStreamClient();
        var secondaryMock = new Mock<ILlmClient>();
        secondaryMock.Setup(x => x.ProviderName).Returns("Secondary");

        var client = new FallbackLlmClient(primary, secondaryMock.Object, fallbackEnabled: true);

        // Act & Assert
        var tokens = new List<string>();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var token in client.StreamTextAsync(new LlmRequest(new[] { new LlmMessage("user", "test") })))
            {
                tokens.Add(token);
            }
        });

        Assert.Contains("Simulated mid-stream network drop", ex.Message);
        Assert.Single(tokens); // Received initial token before error was thrown
    }

    [Fact]
    public async Task OutputGovernor_WhenLlmThrows_ReturnsUnapprovedFailSafe()
    {
        // Arrange
        var failingLlm = new Mock<ILlmClient>();
        failingLlm
            .Setup(x => x.GenerateStructuredAsync<OutputGovernorResult>(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Outage 503"));

        var governor = new OutputGovernor(failingLlm.Object);

        // Act
        var result = await governor.ValidateOutputAsync("question", "answer", new List<DocumentChunk>());

        // Assert
        Assert.False(result.Approved);
        Assert.Equal("FAIL_SAFE", result.Action);
        Assert.NotEmpty(result.Issues);
    }
}
