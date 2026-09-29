using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Llm;

/// <summary>
/// OpenAI-compatible Local LLM provider supporting vLLM, Ollama, LM Studio, TGI, and local gateways.
/// </summary>
public sealed class LocalLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LocalLlmOptions _options;
    private readonly ILogger<LocalLlmClient>? _logger;
    private readonly SemaphoreSlim? _concurrencySemaphore;
    private bool _disposed;

    public string ProviderName => "Local";

    public LocalLlmClient(
        LocalLlmOptions options,
        HttpClient httpClient,
        ILogger<LocalLlmClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;

        if (_options.MaxConcurrency.HasValue && _options.MaxConcurrency.Value > 0)
        {
            _concurrencySemaphore = new SemaphoreSlim(_options.MaxConcurrency.Value, _options.MaxConcurrency.Value);
        }
    }

    public LocalLlmClient(
        IOptions<LlmOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<LocalLlmClient>? logger = null)
        : this(
            options?.Value.Local ?? new LocalLlmOptions(),
            httpClientFactory?.CreateClient("LocalLlmClient") ?? new HttpClient(),
            logger)
    {
    }

    public LocalLlmClient(
        IOptions<LocalLlmOptions> options,
        HttpClient httpClient,
        ILogger<LocalLlmClient>? logger = null)
        : this(options?.Value ?? new LocalLlmOptions(), httpClient, logger)
    {
    }

    public async Task<LlmTextResult> GenerateTextAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var model = request.Model ?? _options.Model;
        var stopwatch = Stopwatch.StartNew();

        _logger?.LogInformation(
            "[{Provider}] Starting LLM [GenerateText] with model {Model} (RequireJson: {RequireJson}).",
            ProviderName, model, request.RequireJson);

        if (_concurrencySemaphore != null)
        {
            await _concurrencySemaphore.WaitAsync(cancellationToken);
        }

        try
        {
            using var timeoutCts = CreateTimeoutCancellationTokenSource(cancellationToken);
            var linkedToken = timeoutCts.Token;

            var completionRequest = BuildOpenAiRequest(request, model, stream: false);
            var endpointUri = BuildRequestUri(_options.ChatCompletionsPath ?? "/v1/chat/completions");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUri)
            {
                Content = JsonContent.Create(completionRequest, options: SerializerOptions)
            };

            ApplyAuthentication(httpRequest);

            using var response = await _httpClient.SendAsync(httpRequest, linkedToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(linkedToken);
                _logger?.LogWarning(
                    "[{Provider}] HTTP error (Status {StatusCode}) after {DurationMs}ms: {Error}",
                    ProviderName, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, errorBody);

                throw new LlmProviderUnavailableException(
                    ProviderName,
                    model,
                    $"Local inference server returned status {(int)response.StatusCode}: {errorBody}",
                    (int)response.StatusCode);
            }

            var result = await response.Content.ReadFromJsonAsync<OpenAiChatCompletionResponse>(SerializerOptions, linkedToken);
            var content = result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;

            var inputTokens = result?.Usage?.PromptTokens;
            var outputTokens = result?.Usage?.CompletionTokens;

            var metadata = new LlmExecutionMetadata(
                Provider: ProviderName,
                Model: model,
                DurationMs: stopwatch.ElapsedMilliseconds,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                StructuredOutputValid: !request.RequireJson || IsValidJson(content),
                AttemptCount: 1);

            _logger?.LogInformation(
                "[{Provider}] GenerateText completed in {DurationMs}ms (Length: {Length}, Tokens: In={In}/Out={Out}).",
                ProviderName, stopwatch.ElapsedMilliseconds, content.Length, inputTokens, outputTokens);

            return new LlmTextResult(content, metadata);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "[{Provider}] Request timed out after {TimeoutSec}s.", ProviderName, _options.TimeoutSeconds);
            throw new LlmTimeoutException(ProviderName, model, TimeSpan.FromSeconds(_options.TimeoutSeconds), ex);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "[{Provider}] Connection error after {DurationMs}ms: {Message}", ProviderName, stopwatch.ElapsedMilliseconds, ex.Message);
            throw new LlmProviderUnavailableException(ProviderName, model, $"Failed to connect to local LLM server at {_options.BaseUrl}: {ex.Message}", null, ex);
        }
        finally
        {
            _concurrencySemaphore?.Release();
        }
    }

    public async Task<LlmStructuredResult<T>> GenerateStructuredAsync<T>(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var model = request.Model ?? _options.Model;
        var maxRetries = Math.Max(0, _options.MaxStructuredRetries);
        var attemptsList = new List<LlmAttemptRecord>();
        var totalStopwatch = Stopwatch.StartNew();

        int? totalInputTokens = 0;
        int? totalOutputTokens = 0;
        string? lastRawContent = null;
        string? lastErrorMessage = null;

        var currentMessages = new List<LlmMessage>(request.Messages);
        var baseRequest = request with { RequireJson = true };

        for (int attempt = 1; attempt <= maxRetries + 1; attempt++)
        {
            var attemptStopwatch = Stopwatch.StartNew();
            var attemptRequest = baseRequest with { Messages = currentMessages };

            try
            {
                var textResult = await GenerateTextAsync(attemptRequest, cancellationToken);
                attemptStopwatch.Stop();

                totalInputTokens = (totalInputTokens ?? 0) + (textResult.Metadata.InputTokens ?? 0);
                totalOutputTokens = (totalOutputTokens ?? 0) + (textResult.Metadata.OutputTokens ?? 0);
                lastRawContent = CleanJsonFences(textResult.Content);

                var deserialized = JsonSerializer.Deserialize<T>(lastRawContent, CaseInsensitiveOptions);
                if (deserialized != null)
                {
                    totalStopwatch.Stop();
                    attemptsList.Add(new LlmAttemptRecord(ProviderName, "success", null, attemptStopwatch.ElapsedMilliseconds));

                    var meta = new LlmExecutionMetadata(
                        Provider: ProviderName,
                        Model: model,
                        DurationMs: totalStopwatch.ElapsedMilliseconds,
                        InputTokens: totalInputTokens,
                        OutputTokens: totalOutputTokens,
                        StructuredOutputValid: true,
                        AttemptCount: attempt,
                        Attempts: attemptsList);

                    _logger?.LogInformation(
                        "[{Provider}] Structured output for {Type} succeeded on attempt {Attempt}/{MaxAttempts} in {DurationMs}ms.",
                        ProviderName, typeof(T).Name, attempt, maxRetries + 1, totalStopwatch.ElapsedMilliseconds);

                    return new LlmStructuredResult<T>(deserialized, meta, lastRawContent, IsSuccess: true);
                }

                lastErrorMessage = $"Deserialized value for {typeof(T).Name} was null.";
                attemptsList.Add(new LlmAttemptRecord(ProviderName, "null_result", lastErrorMessage, attemptStopwatch.ElapsedMilliseconds));
            }
            catch (JsonException ex)
            {
                attemptStopwatch.Stop();
                lastErrorMessage = $"JSON parse error: {ex.Message}";
                attemptsList.Add(new LlmAttemptRecord(ProviderName, "parse_error", lastErrorMessage, attemptStopwatch.ElapsedMilliseconds));
                _logger?.LogWarning(
                    "[{Provider}] Structured output JSON parsing failed on attempt {Attempt}/{MaxAttempts}: {Message}",
                    ProviderName, attempt, maxRetries + 1, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt <= maxRetries)
            {
                attemptStopwatch.Stop();
                lastErrorMessage = ex.Message;
                attemptsList.Add(new LlmAttemptRecord(ProviderName, "error", ex.Message, attemptStopwatch.ElapsedMilliseconds));
                _logger?.LogWarning(
                    "[{Provider}] Attempt {Attempt}/{MaxAttempts} failed with error: {Message}",
                    ProviderName, attempt, maxRetries + 1, ex.Message);
            }

            // If we have retries left, add feedback message for self-correction
            if (attempt <= maxRetries)
            {
                currentMessages.Add(new LlmMessage("assistant", lastRawContent ?? string.Empty));
                currentMessages.Add(new LlmMessage("user",
                    $"Your previous output was not valid JSON for the expected schema or returned invalid data: {lastErrorMessage}. " +
                    $"Please reply with ONLY a raw valid JSON object satisfying the schema with no extra commentary or markdown formatting."));
            }
        }

        totalStopwatch.Stop();
        var finalMetadata = new LlmExecutionMetadata(
            Provider: ProviderName,
            Model: model,
            DurationMs: totalStopwatch.ElapsedMilliseconds,
            InputTokens: totalInputTokens,
            OutputTokens: totalOutputTokens,
            StructuredOutputValid: false,
            AttemptCount: maxRetries + 1,
            Error: lastErrorMessage,
            Attempts: attemptsList);

        _logger?.LogError(
            "[{Provider}] Structured generation failed after {Attempts} attempts for type {Type}. Error: {Error}",
            ProviderName, maxRetries + 1, typeof(T).Name, lastErrorMessage);

        return new LlmStructuredResult<T>(
            default,
            finalMetadata,
            lastRawContent,
            IsSuccess: false,
            ErrorMessage: lastErrorMessage);
    }

    public async IAsyncEnumerable<string> StreamTextAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var model = request.Model ?? _options.Model;

        if (_concurrencySemaphore != null)
        {
            await _concurrencySemaphore.WaitAsync(cancellationToken);
        }

        HttpResponseMessage? response = null;
        Stream? stream = null;
        StreamReader? reader = null;

        try
        {
            var completionRequest = BuildOpenAiRequest(request, model, stream: true);
            var endpointUri = BuildRequestUri(_options.ChatCompletionsPath ?? "/v1/chat/completions");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUri)
            {
                Content = JsonContent.Create(completionRequest, options: SerializerOptions)
            };
            ApplyAuthentication(httpRequest);

            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new LlmProviderUnavailableException(ProviderName, model, $"Stream request failed with status {(int)response.StatusCode}: {error}", (int)response.StatusCode);
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
                {
                    var data = line.Substring(6).Trim();
                    if (data == "[DONE]")
                        break;

                    string? token = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                            choices.ValueKind == JsonValueKind.Array &&
                            choices.GetArrayLength() > 0 &&
                            choices[0].TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var contentProp))
                        {
                            token = contentProp.GetString();
                        }
                    }
                    catch
                    {
                        // Ignore SSE framing / transient parse issues
                    }

                    if (!string.IsNullOrEmpty(token))
                    {
                        yield return token;
                    }
                }
            }
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
            response?.Dispose();
            _concurrencySemaphore?.Release();
        }
    }

    private Uri BuildRequestUri(string relativePath)
    {
        if (_httpClient.BaseAddress != null)
        {
            var cleanPath = relativePath.TrimStart('/');
            return new Uri(cleanPath, UriKind.RelativeOrAbsolute);
        }

        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "http://localhost:8080" : _options.BaseUrl.Trim();
        if (baseUrl.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            if (relativePath.Contains("models", StringComparison.OrdinalIgnoreCase))
            {
                var baseHost = baseUrl.Substring(0, baseUrl.Length - "/v1/chat/completions".Length);
                return new Uri(baseHost.TrimEnd('/') + "/" + relativePath.TrimStart('/'));
            }
            return new Uri(baseUrl);
        }

        var trimmedBase = baseUrl.TrimEnd('/');
        var trimmedPath = relativePath.TrimStart('/');
        return new Uri($"{trimmedBase}/{trimmedPath}");
    }

    private void ApplyAuthentication(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    private CancellationTokenSource CreateTimeoutCancellationTokenSource(CancellationToken callerToken)
    {
        var timeout = _options.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(_options.TimeoutSeconds) : TimeSpan.FromSeconds(60);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static OpenAiChatCompletionRequest BuildOpenAiRequest(LlmRequest request, string model, bool stream)
    {
        var messages = new List<OpenAiChatMessage>();

        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            messages.Add(new OpenAiChatMessage("system", request.SystemInstruction));
        }

        foreach (var msg in request.Messages)
        {
            // Don't duplicate system instruction if already added
            if (string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(request.SystemInstruction))
            {
                continue;
            }
            messages.Add(new OpenAiChatMessage(msg.Role.ToLowerInvariant(), msg.Content));
        }

        OpenAiResponseFormat? responseFormat = null;
        if (request.RequireJson)
        {
            if (request.ResponseSchema != null)
            {
                responseFormat = new OpenAiResponseFormat("json_schema", request.ResponseSchema);
            }
            else
            {
                responseFormat = new OpenAiResponseFormat("json_object", null);
            }
        }

        return new OpenAiChatCompletionRequest(
            Model: model,
            Messages: messages,
            Temperature: request.Temperature ?? (request.RequireJson ? 0.0 : 0.2),
            MaxTokens: request.MaxOutputTokens ?? (request.RequireJson ? 512 : 2048),
            Stream: stream,
            ResponseFormat: responseFormat
        );
    }

    private static string CleanJsonFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = text.Trim();
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(7);
            if (cleaned.EndsWith("```"))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - 3);
            }
        }
        else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(3);
            if (cleaned.EndsWith("```"))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - 3);
            }
        }
        return cleaned.Trim();
    }

    private static bool IsValidJson(string input)
    {
        try
        {
            using var doc = JsonDocument.Parse(CleanJsonFences(input));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public void Dispose()
    {
        if (!_disposed)
        {
            _concurrencySemaphore?.Dispose();
            _disposed = true;
        }
    }

    #region Internal OpenAI DTOs

    internal sealed record OpenAiChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double? Temperature = null,
        [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
        [property: JsonPropertyName("stream")] bool Stream = false,
        [property: JsonPropertyName("response_format")] OpenAiResponseFormat? ResponseFormat = null
    );

    internal sealed record OpenAiChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    internal sealed record OpenAiResponseFormat(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("json_schema")] object? JsonSchema = null
    );

    internal sealed record OpenAiChatCompletionResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice>? Choices,
        [property: JsonPropertyName("usage")] OpenAiUsage? Usage
    );

    internal sealed record OpenAiChoice(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("message")] OpenAiChatMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason
    );

    internal sealed record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens
    );

    #endregion
}
