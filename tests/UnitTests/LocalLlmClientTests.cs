using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Llm;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class LocalLlmClientTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<HttpRequestMessage> SentRequests { get; } = new();

        public void EnqueueResponse(HttpStatusCode statusCode, string content, string mediaType = "application/json")
        {
            _responses.Enqueue(req => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            });
        }

        public void EnqueueCallback(Func<HttpRequestMessage, HttpResponseMessage> callback)
        {
            _responses.Enqueue(callback);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (_responses.Count > 0)
            {
                return Task.FromResult(_responses.Dequeue()(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task GenerateTextAsync_ShouldReturnExpectedContentAndMetadata_OnSuccess()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var responseJson = """
        {
          "id": "chatcmpl-123",
          "model": "qwen2.5-7b",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "The fan operates at 230V." },
              "finish_reason": "stop"
            }
          ],
          "usage": {
            "prompt_tokens": 42,
            "completion_tokens": 12,
            "total_tokens": 54
          }
        }
        """;
        handler.EnqueueResponse(HttpStatusCode.OK, responseJson);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "qwen2.5-7b" };
        var client = new LocalLlmClient(options, httpClient);

        var request = LlmRequest.FromPrompt("What voltage?");

        // Act
        var result = await client.GenerateTextAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("The fan operates at 230V.", result.Content);
        Assert.Equal("Local", result.Metadata.Provider);
        Assert.Equal("qwen2.5-7b", result.Metadata.Model);
        Assert.Equal(42, result.Metadata.InputTokens);
        Assert.Equal(12, result.Metadata.OutputTokens);
        Assert.True(result.Metadata.DurationMs >= 0);
        Assert.Equal(1, result.Metadata.AttemptCount);
    }

    [Fact]
    public async Task GenerateTextAsync_ShouldAttachAuthorizationHeader_WhenApiKeyIsConfigured()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """
        {
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": "OK" } }]
        }
        """);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "local-model", ApiKey = "secret-token-123" };
        var client = new LocalLlmClient(options, httpClient);

        // Act
        await client.GenerateTextAsync(LlmRequest.FromPrompt("Hello"));

        // Assert
        Assert.Single(handler.SentRequests);
        var sentReq = handler.SentRequests[0];
        Assert.NotNull(sentReq.Headers.Authorization);
        Assert.Equal("Bearer", sentReq.Headers.Authorization.Scheme);
        Assert.Equal("secret-token-123", sentReq.Headers.Authorization.Parameter);
    }

    private record SampleDto(string Name, int Value);

    [Fact]
    public async Task GenerateStructuredAsync_ShouldDeserializeTypedResult_OnValidJson()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var responseJson = """
        {
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "{\"name\": \"Fan-A\", \"value\": 450}" }
            }
          ]
        }
        """;
        handler.EnqueueResponse(HttpStatusCode.OK, responseJson);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "local-model" };
        var client = new LocalLlmClient(options, httpClient);

        // Act
        var result = await client.GenerateStructuredAsync<SampleDto>(LlmRequest.FromPrompt("Get sample"));

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Fan-A", result.Value.Name);
        Assert.Equal(450, result.Value.Value);
        Assert.True(result.Metadata.StructuredOutputValid);
        Assert.Equal(1, result.Metadata.AttemptCount);
    }

    [Fact]
    public async Task GenerateStructuredAsync_ShouldRetryAndSucceed_WhenFirstResponseIsMalformedJson()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        // 1st attempt: malformed JSON
        handler.EnqueueResponse(HttpStatusCode.OK, """
        {
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": "Here is the json: {name: Fan-A, value: invalid" } }]
        }
        """);
        // 2nd attempt: corrected valid JSON
        handler.EnqueueResponse(HttpStatusCode.OK, """
        {
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": "{\"name\": \"Fan-A\", \"value\": 450}" } }]
        }
        """);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "local-model", MaxStructuredRetries = 1 };
        var client = new LocalLlmClient(options, httpClient);

        // Act
        var result = await client.GenerateStructuredAsync<SampleDto>(LlmRequest.FromPrompt("Get sample"));

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Fan-A", result.Value.Name);
        Assert.Equal(450, result.Value.Value);
        Assert.True(result.Metadata.StructuredOutputValid);
        Assert.Equal(2, result.Metadata.AttemptCount);
        Assert.Equal(2, handler.SentRequests.Count);
    }

    [Fact]
    public async Task GenerateStructuredAsync_ShouldReturnFailure_WhenAllRetriesExhausted()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """{ "choices": [{ "index": 0, "message": { "role": "assistant", "content": "not json 1" } }] }""");
        handler.EnqueueResponse(HttpStatusCode.OK, """{ "choices": [{ "index": 0, "message": { "role": "assistant", "content": "not json 2" } }] }""");

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "local-model", MaxStructuredRetries = 1 };
        var client = new LocalLlmClient(options, httpClient);

        // Act
        var result = await client.GenerateStructuredAsync<SampleDto>(LlmRequest.FromPrompt("Get sample"));

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.False(result.Metadata.StructuredOutputValid);
        Assert.Equal(2, result.Metadata.AttemptCount);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateTextAsync_ShouldThrowLlmProviderUnavailableException_OnServerError()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "CUDA out of memory");

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "local-model" };
        var client = new LocalLlmClient(options, httpClient);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<LlmProviderUnavailableException>(() =>
            client.GenerateTextAsync(LlmRequest.FromPrompt("Test prompt")));

        Assert.Equal("Local", ex.Provider);
        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task GenerateTextAsync_ShouldThrowLlmTimeoutException_WhenTimeoutElapses()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        handler.EnqueueCallback(req =>
        {
            Thread.Sleep(100);
            throw new OperationCanceledException();
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "local-model", TimeoutSeconds = 1 };
        var client = new LocalLlmClient(options, httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<LlmTimeoutException>(() =>
            client.GenerateTextAsync(LlmRequest.FromPrompt("Test timeout")));
    }

    [Fact]
    public async Task GenerateTextAsync_ShouldHonorCancellationToken_WhenCallerCancels()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options = new LocalLlmOptions { BaseUrl = "http://localhost:8000", Model = "local-model" };
        var client = new LocalLlmClient(options, httpClient);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GenerateTextAsync(LlmRequest.FromPrompt("Cancel test"), cts.Token));
    }
}
