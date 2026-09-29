using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.HealthChecks;
using DocumentRagSystem.Infrastructure.Llm;
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
}
