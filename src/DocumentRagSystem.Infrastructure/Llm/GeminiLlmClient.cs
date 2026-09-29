using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Llm;

/// <summary>
/// Google Gemini implementation of the ILlmClient provider abstraction.
/// </summary>
public sealed class GeminiLlmClient : ILlmClient
{
    private readonly HttpClient? _httpClient;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string _fastModel;
    private readonly ILogger<GeminiLlmClient>? _logger;

    public string ProviderName => "Gemini";

    public GeminiLlmClient(
        string? apiKey,
        string model = "gemini-3.6-flash",
        string? fastModel = null,
        HttpClient? httpClient = null,
        ILogger<GeminiLlmClient>? logger = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-3.6-flash" : model;
        _fastModel = string.IsNullOrWhiteSpace(fastModel) ? _model : fastModel;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_GEMINI_API_KEY")
        {
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient();
        }
    }

    public GeminiLlmClient(
        IOptions<LlmOptions> options,
        HttpClient? httpClient = null,
        ILogger<GeminiLlmClient>? logger = null)
        : this(
            options.Value.Gemini.ApiKey,
            options.Value.Gemini.LlmModel,
            options.Value.Gemini.FastLlmModel,
            httpClient,
            logger)
    {
    }

    public async Task<LlmTextResult> GenerateTextAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var prompt = FormatMessagesToPrompt(request.Messages, request.SystemInstruction);
        var modelToUse = request.Model ?? (request.RequireJson ? _fastModel : _model);
        var stopwatch = Stopwatch.StartNew();

        _logger?.LogInformation(
            "[{Provider}] Starting LLM [GenerateText] with model {Model} (Prompt length: {PromptLength} chars).",
            ProviderName, modelToUse, prompt.Length);

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            var mock = GenerateLocalMockResponse(prompt, request.RequireJson);
            stopwatch.Stop();
            var meta = new LlmExecutionMetadata(
                Provider: ProviderName,
                Model: modelToUse,
                DurationMs: stopwatch.ElapsedMilliseconds,
                InputTokens: prompt.Length / 4,
                OutputTokens: mock.Length / 4,
                StructuredOutputValid: !request.RequireJson || IsValidJson(mock),
                AttemptCount: 1);

            return new LlmTextResult(mock, meta);
        }

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelToUse}:generateContent?key={_apiKey}";

            object requestBody;
            if (request.RequireJson)
            {
                requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        responseMimeType = "application/json",
                        temperature = request.Temperature ?? 0.0,
                        maxOutputTokens = request.MaxOutputTokens ?? 2048,
                        thinkingConfig = new
                        {
                            thinkingBudget = 0
                        }
                    }
                };
            }
            else
            {
                requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = request.Temperature ?? 0.2,
                        topP = 0.95,
                        maxOutputTokens = request.MaxOutputTokens ?? 4096,
                        thinkingConfig = new
                        {
                            thinkingBudget = 0
                        }
                    }
                };
            }

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogWarning(
                    "[{Provider}] API error (Status {StatusCode}) after {DurationMs}ms: {ErrorContent}. Falling back to mock.",
                    ProviderName, response.StatusCode, stopwatch.ElapsedMilliseconds, errorContent);

                var mockFallback = GenerateLocalMockResponse(prompt, request.RequireJson);
                var errMeta = new LlmExecutionMetadata(
                    Provider: ProviderName,
                    Model: modelToUse,
                    DurationMs: stopwatch.ElapsedMilliseconds,
                    InputTokens: prompt.Length / 4,
                    OutputTokens: mockFallback.Length / 4,
                    StructuredOutputValid: !request.RequireJson || IsValidJson(mockFallback),
                    AttemptCount: 1,
                    Error: $"HTTP {(int)response.StatusCode}: {errorContent}");
                return new LlmTextResult(mockFallback, errMeta);
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            int? inputTokens = null;
            int? outputTokens = null;

            if (jsonResult != null && jsonResult.RootElement.TryGetProperty("usageMetadata", out var usageProp))
            {
                if (usageProp.TryGetProperty("promptTokenCount", out var pTokens))
                    inputTokens = pTokens.GetInt32();
                if (usageProp.TryGetProperty("candidatesTokenCount", out var cTokens))
                    outputTokens = cTokens.GetInt32();
            }

            if (jsonResult != null &&
                jsonResult.RootElement.TryGetProperty("candidates", out var candidatesProp) &&
                candidatesProp.ValueKind == JsonValueKind.Array &&
                candidatesProp.GetArrayLength() > 0 &&
                candidatesProp[0].TryGetProperty("content", out var contentProp) &&
                contentProp.TryGetProperty("parts", out var partsProp) &&
                partsProp.ValueKind == JsonValueKind.Array &&
                partsProp.GetArrayLength() > 0 &&
                partsProp[0].TryGetProperty("text", out var textProp))
            {
                var resultText = textProp.GetString() ?? string.Empty;
                var meta = new LlmExecutionMetadata(
                    Provider: ProviderName,
                    Model: modelToUse,
                    DurationMs: stopwatch.ElapsedMilliseconds,
                    InputTokens: inputTokens ?? (prompt.Length / 4),
                    OutputTokens: outputTokens ?? (resultText.Length / 4),
                    StructuredOutputValid: !request.RequireJson || IsValidJson(resultText),
                    AttemptCount: 1);

                return new LlmTextResult(resultText, meta);
            }

            _logger?.LogWarning("[{Provider}] Failed to parse response after {DurationMs}ms. Falling back to mock.", ProviderName, stopwatch.ElapsedMilliseconds);
            var parseMock = GenerateLocalMockResponse(prompt, request.RequireJson);
            var parseMeta = new LlmExecutionMetadata(
                Provider: ProviderName,
                Model: modelToUse,
                DurationMs: stopwatch.ElapsedMilliseconds,
                InputTokens: prompt.Length / 4,
                OutputTokens: parseMock.Length / 4,
                StructuredOutputValid: !request.RequireJson || IsValidJson(parseMock),
                AttemptCount: 1,
                Error: "Empty or unparseable candidates from Gemini API");

            return new LlmTextResult(parseMock, parseMeta);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "[{Provider}] Error after {DurationMs}ms: {Message}. Falling back to mock.", ProviderName, stopwatch.ElapsedMilliseconds, ex.Message);
            var mockFallback = GenerateLocalMockResponse(prompt, request.RequireJson);
            var errMeta = new LlmExecutionMetadata(
                Provider: ProviderName,
                Model: modelToUse,
                DurationMs: stopwatch.ElapsedMilliseconds,
                InputTokens: prompt.Length / 4,
                OutputTokens: mockFallback.Length / 4,
                StructuredOutputValid: !request.RequireJson || IsValidJson(mockFallback),
                AttemptCount: 1,
                Error: ex.Message);
            return new LlmTextResult(mockFallback, errMeta);
        }
    }

    public async Task<LlmStructuredResult<T>> GenerateStructuredAsync<T>(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var jsonRequest = request with { RequireJson = true };
        var textResult = await GenerateTextAsync(jsonRequest, cancellationToken);
        var rawContent = CleanJsonFences(textResult.Content);

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var deserialized = JsonSerializer.Deserialize<T>(rawContent, options);
            if (deserialized != null)
            {
                var meta = textResult.Metadata with { StructuredOutputValid = true };
                return new LlmStructuredResult<T>(deserialized, meta, rawContent, IsSuccess: true);
            }

            var failMeta = textResult.Metadata with { StructuredOutputValid = false, Error = "Deserialization returned null" };
            return new LlmStructuredResult<T>(default, failMeta, rawContent, IsSuccess: false, ErrorMessage: "Deserialization returned null");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[{Provider}] Structured deserialization failed for {Type}: {Message}", ProviderName, typeof(T).Name, ex.Message);
            var failMeta = textResult.Metadata with { StructuredOutputValid = false, Error = ex.Message };
            return new LlmStructuredResult<T>(default, failMeta, rawContent, IsSuccess: false, ErrorMessage: ex.Message);
        }
    }

    public async IAsyncEnumerable<string> StreamTextAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var prompt = FormatMessagesToPrompt(request.Messages, request.SystemInstruction);
        var modelToUse = request.Model ?? _model;

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            var mock = GenerateLocalMockResponse(prompt, request.RequireJson);
            var words = mock.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                yield return words[i] + (i < words.Length - 1 ? " " : "");
            }
            yield break;
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelToUse}:streamGenerateContent?alt=sse&key={_apiKey}";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = request.Temperature ?? 0.2,
                topP = 0.95,
                maxOutputTokens = request.MaxOutputTokens ?? 4096,
                thinkingConfig = new
                {
                    thinkingBudget = 0
                }
            }
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody)
        };

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var fallback = await GenerateTextAsync(request, cancellationToken);
            yield return fallback.Content;
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
            {
                var jsonStr = line.Substring(6).Trim();
                if (string.IsNullOrWhiteSpace(jsonStr))
                    continue;

                string? chunkText = null;
                try
                {
                    using var doc = JsonDocument.Parse(jsonStr);
                    if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                        candidates.ValueKind == JsonValueKind.Array &&
                        candidates.GetArrayLength() > 0 &&
                        candidates[0].TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array &&
                        parts.GetArrayLength() > 0 &&
                        parts[0].TryGetProperty("text", out var textEl))
                    {
                        chunkText = textEl.GetString();
                    }
                }
                catch
                {
                    // Ignore transient SSE chunk parse errors
                }

                if (!string.IsNullOrEmpty(chunkText))
                {
                    yield return chunkText;
                }
            }
        }
    }

    private static string FormatMessagesToPrompt(IReadOnlyList<LlmMessage> messages, string? systemInstruction)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemInstruction))
        {
            sb.AppendLine(systemInstruction);
            sb.AppendLine();
        }

        foreach (var msg in messages)
        {
            if (string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(systemInstruction))
                {
                    sb.AppendLine(msg.Content);
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine(msg.Content);
            }
        }

        return sb.ToString().Trim();
    }

    private static string CleanJsonFences(string response)
    {
        var cleaned = response.Trim();
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

    private static string GenerateLocalMockResponse(string prompt, bool requireJson)
    {
        if (!requireJson)
        {
            if (prompt.Contains("### CONTEXT") || prompt.Contains("<answers>"))
            {
                return $"[Local Mock Response] Based on the retrieved context chunks:\n\nThis is a mock answer generated locally for the prompt.";
            }
            return $"[Local Mock Completion] Response for prompt:\n{prompt.Substring(0, Math.Min(prompt.Length, 100))}...";
        }

        var promptLower = prompt.ToLowerInvariant();

        if (promptLower.Contains("conversational query refiner") || promptLower.Contains("query refiner"))
        {
            return """
            {
              "original_question": "Mock question",
              "relationship_to_previous_turn": "CONTINUES",
              "resolved_question": "Mock question",
              "effective_question": "Mock question",
              "active_entities": [],
              "active_constraints": [],
              "candidate_set": [],
              "constraints_added": [],
              "constraints_removed": [],
              "constraints_replaced": [],
              "references_resolved": [],
              "context_used": [],
              "clarification_required": false,
              "clarification_reason": null,
              "new_topic": false
            }
            """;
        }

        if (promptLower.Contains("input governor") || promptLower.Contains("intent taxonomy"))
        {
            string intent = "SPEC_LOOKUP";
            var entitiesJson = "[]";
            var requestedAttributesJson = "[]";

            if (promptLower.Contains("compare") || promptLower.Contains("difference"))
            {
                intent = "COMPARISON";
                entitiesJson = "[{\"Type\":\"controller\",\"Name\":\"Model A\"},{\"Type\":\"controller\",\"Name\":\"Model B\"}]";
                requestedAttributesJson = "[\"supply_voltage\",\"communication_protocol\"]";
            }
            else if (promptLower.Contains("compatible") || promptLower.Contains("compatibility") || promptLower.Contains("work with"))
            {
                intent = "COMPATIBILITY";
                entitiesJson = "[{\"Type\":\"controller\",\"Name\":\"ABC-500\"},{\"Type\":\"sensor\",\"Name\":\"XYZ-20\"}]";
                requestedAttributesJson = "[\"supply_voltage\",\"signal_type\"]";
            }
            else if (promptLower.Contains("trouble") || promptLower.Contains("error") || promptLower.Contains("doesn't work") || promptLower.Contains("symptom"))
            {
                intent = "TROUBLESHOOTING";
                entitiesJson = "[{\"Type\":\"device\",\"Name\":\"Controller\"}]";
            }
            else if (promptLower.Contains("calculate") || promptLower.Contains("formula") || (promptLower.Contains("voltage") && promptLower.Contains("current")))
            {
                intent = "CALCULATION";
            }
            else if (promptLower.Contains("design"))
            {
                intent = "DESIGN";
            }
            else if (promptLower.Contains("clarify") || promptLower.Contains("missing info"))
            {
                intent = "CLARIFICATION";
            }

            return $$"""
            {
              "intent": "{{intent}}",
              "confidence": 0.95,
              "entities": {{entitiesJson}},
              "requested_attributes": {{requestedAttributesJson}},
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """;
        }

        if (promptLower.Contains("comparison rag") || promptLower.Contains("compare multiple entities"))
        {
            return """
            {
              "entities": {
                "Model A": {
                  "name": "Model A",
                  "attributes": {
                    "supply_voltage": {
                      "value": "18-30 VDC",
                      "source": "Model A datasheet"
                    },
                    "communication_protocol": {
                      "value": "Modbus RTU",
                      "source": "Model A manual"
                    }
                  }
                },
                "Model B": {
                  "name": "Model B",
                  "attributes": {
                    "supply_voltage": {
                      "value": "24 VDC",
                      "source": "Model B specifications"
                    },
                    "communication_protocol": {
                      "value": "CANopen",
                      "source": "Model B manual"
                    }
                  }
                }
              }
            }
            """;
        }

        if (promptLower.Contains("compatibility rag") || promptLower.Contains("compatibility questions"))
        {
            return """
            {
              "checks": [
                {
                  "dimension": "supply_voltage",
                  "sourceValue": "24 VDC +/-10%",
                  "targetRequirement": "18-30 VDC",
                  "status": "COMPATIBLE",
                  "reason": "The source voltage range is within the target operating range."
                },
                {
                  "dimension": "communication_protocol",
                  "sourceValue": "Modbus RTU",
                  "targetRequirement": "CANopen",
                  "status": "INCOMPATIBLE",
                  "reason": "Protocols do not match. Conversion device is required."
                }
              ],
              "status": "INCOMPATIBLE",
              "reason": "The communication protocols are incompatible."
            }
            """;
        }

        if (promptLower.Contains("diagnostic rag") || promptLower.Contains("troubleshooting questions"))
        {
            return """
            {
              "causes": [
                {
                  "cause": "Supply voltage below operating range",
                  "status": "SUPPORTED",
                  "evidence": ["Manual section 4.2"],
                  "check": "Measure voltage at terminals X and Y.",
                  "expectedResult": "18-30 VDC"
                }
              ],
              "troubleshootingSteps": [
                "Verify input power source is active.",
                "Check wiring connection at terminals X and Y."
              ]
            }
            """;
        }

        if (promptLower.Contains("evidence layer") || promptLower.Contains("supported, derived, conflicting, missing"))
        {
            return """
            [
              {
                "claim": "The device operates at 24 VDC.",
                "status": "DERIVED",
                "reason": "Operating voltage is stated as 18-30 VDC in the manual.",
                "sources": ["Manual page 12"]
              },
              {
                "claim": "Maximum current is 1.5A.",
                "status": "SUPPORTED",
                "reason": "The datasheet states max current is 1.5A.",
                "sources": ["Datasheet page 2"]
              }
            ]
            """;
        }

        if (promptLower.Contains("output governor") || promptLower.Contains("validate the proposed answer") || promptLower.Contains("validate the proposed final answer"))
        {
            return """
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }
            """;
        }

        return "{}";
    }
}
