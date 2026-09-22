using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Llm;

public class GeminiLlmService : ILlmService
{
    private readonly HttpClient? _httpClient;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string _fastModel;
    private readonly ILogger<GeminiLlmService>? _logger;

    public GeminiLlmService(
        string? apiKey, 
        string model = "gemini-3.6-flash", 
        string? fastModel = null, 
        HttpClient? httpClient = null,
        ILogger<GeminiLlmService>? logger = null)
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

    public async Task<string> GenerateResponseAsync(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentNullException(nameof(query));

        var prompt = BuildPrompt(query, contextChunks);
        var stopwatch = Stopwatch.StartNew();

        _logger?.LogInformation(
            "Starting LLM [GenerateResponse] call with model {Model} (Prompt length: {PromptLength} chars).\nPrompt:\n{Prompt}",
            _model, prompt.Length, prompt);

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            var mock = GenerateLocalMockResponse(query, contextChunks);
            stopwatch.Stop();
            _logger?.LogInformation(
                "LLM [GenerateResponse] (Local Mock) completed in {DurationMs}ms (Response length: {ResponseLength} chars).",
                stopwatch.ElapsedMilliseconds, mock.Length);
            return mock;
        }

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

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
                    temperature = 0.2,
                    topP = 0.95,
                    maxOutputTokens = 4096,
                    thinkingConfig = new
                    {
                        thinkingBudget = 0
                    }
                }
            };

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning(
                    "Gemini LLM API error (Status {StatusCode}) after {DurationMs}ms: {ErrorContent}. Falling back to mock.",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, errorContent);
                Console.WriteLine($"Gemini LLM API error (Status {response.StatusCode}): {errorContent}. Falling back to mock.");
                return GenerateLocalMockResponse(query, contextChunks);
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>();
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
                _logger?.LogInformation(
                    "LLM [GenerateResponse] completed in {DurationMs}ms with model {Model} (Response length: {ResponseLength} chars).",
                    stopwatch.ElapsedMilliseconds, _model, resultText.Length);
                return resultText;
            }

            _logger?.LogWarning("Failed to parse Gemini LLM response after {DurationMs}ms. Falling back to mock.", stopwatch.ElapsedMilliseconds);
            Console.WriteLine("Failed to parse Gemini LLM response. Falling back to mock.");
            return GenerateLocalMockResponse(query, contextChunks);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "Gemini LLM error after {DurationMs}ms: {Message}. Falling back to mock.", stopwatch.ElapsedMilliseconds, ex.Message);
            Console.WriteLine($"Gemini LLM error: {ex.Message}. Falling back to mock.");
            return GenerateLocalMockResponse(query, contextChunks);
        }
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string query, 
        IEnumerable<DocumentChunk> contextChunks, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentNullException(nameof(query));

        var prompt = BuildPrompt(query, contextChunks);
        var totalStopwatch = Stopwatch.StartNew();
        var ttftStopwatch = Stopwatch.StartNew();
        bool firstTokenReceived = false;
        long ttftMs = 0;
        int chunkCount = 0;
        int totalChars = 0;

        _logger?.LogInformation(
            "Starting LLM [StreamResponse] with model {Model} (Prompt length: {PromptLength} chars).\nPrompt:\n{Prompt}",
            _model, prompt.Length, prompt);

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            var mock = GenerateLocalMockResponse(query, contextChunks);
            var words = mock.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                var piece = words[i] + (i < words.Length - 1 ? " " : "");
                if (!firstTokenReceived)
                {
                    firstTokenReceived = true;
                    ttftMs = ttftStopwatch.ElapsedMilliseconds;
                }
                chunkCount++;
                totalChars += piece.Length;
                yield return piece;
            }
            totalStopwatch.Stop();
            _logger?.LogInformation(
                "LLM [StreamResponse] (Local Mock) completed in {TotalDurationMs}ms (TTFT: {TtftMs}ms, Chunks: {ChunkCount}, Chars: {TotalChars}).",
                totalStopwatch.ElapsedMilliseconds, ttftMs, chunkCount, totalChars);
            yield break;
        }

        await foreach (var chunk in StreamFromGeminiAsync(_model, prompt, cancellationToken))
        {
            if (!firstTokenReceived)
            {
                firstTokenReceived = true;
                ttftMs = ttftStopwatch.ElapsedMilliseconds;
                _logger?.LogInformation(
                    "LLM [StreamResponse] received first token in {TtftMs}ms.",
                    ttftMs);
            }
            chunkCount++;
            totalChars += chunk.Length;
            yield return chunk;
        }

        totalStopwatch.Stop();
        _logger?.LogInformation(
            "LLM [StreamResponse] completed in {TotalDurationMs}ms with model {Model} (TTFT: {TtftMs}ms, Chunks: {ChunkCount}, Chars: {TotalChars}).",
            totalStopwatch.ElapsedMilliseconds, _model, ttftMs, chunkCount, totalChars);
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentNullException(nameof(prompt));

        var totalStopwatch = Stopwatch.StartNew();
        var ttftStopwatch = Stopwatch.StartNew();
        bool firstTokenReceived = false;
        long ttftMs = 0;
        int chunkCount = 0;
        int totalChars = 0;

        _logger?.LogInformation(
            "Starting LLM [StreamCompletion] with model {Model} (Prompt length: {PromptLength} chars).\nPrompt:\n{Prompt}",
            _model, prompt.Length, prompt);

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            var mock = GenerateLocalMockCompletion(prompt, false);
            var words = mock.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                var piece = words[i] + (i < words.Length - 1 ? " " : "");
                if (!firstTokenReceived)
                {
                    firstTokenReceived = true;
                    ttftMs = ttftStopwatch.ElapsedMilliseconds;
                }
                chunkCount++;
                totalChars += piece.Length;
                yield return piece;
            }
            totalStopwatch.Stop();
            _logger?.LogInformation(
                "LLM [StreamCompletion] (Local Mock) completed in {TotalDurationMs}ms (TTFT: {TtftMs}ms, Chunks: {ChunkCount}, Chars: {TotalChars}).",
                totalStopwatch.ElapsedMilliseconds, ttftMs, chunkCount, totalChars);
            yield break;
        }

        await foreach (var chunk in StreamFromGeminiAsync(_model, prompt, cancellationToken))
        {
            if (!firstTokenReceived)
            {
                firstTokenReceived = true;
                ttftMs = ttftStopwatch.ElapsedMilliseconds;
                _logger?.LogInformation(
                    "LLM [StreamCompletion] received first token in {TtftMs}ms.",
                    ttftMs);
            }
            chunkCount++;
            totalChars += chunk.Length;
            yield return chunk;
        }

        totalStopwatch.Stop();
        _logger?.LogInformation(
            "LLM [StreamCompletion] completed in {TotalDurationMs}ms with model {Model} (TTFT: {TtftMs}ms, Chunks: {ChunkCount}, Chars: {TotalChars}).",
            totalStopwatch.ElapsedMilliseconds, _model, ttftMs, chunkCount, totalChars);
    }

    private async IAsyncEnumerable<string> StreamFromGeminiAsync(
        string modelName, 
        string prompt, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:streamGenerateContent?alt=sse&key={_apiKey}";

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
                temperature = 0.2,
                topP = 0.95,
                maxOutputTokens = 4096,
                thinkingConfig = new
                {
                    thinkingBudget = 0
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody)
        };

        var streamStopwatch = Stopwatch.StartNew();
        using var response = await _httpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger?.LogWarning(
                "Gemini LLM stream API error (Status {StatusCode}) after {DurationMs}ms: {ErrorContent}. Falling back to non-streamed completion.",
                response.StatusCode, streamStopwatch.ElapsedMilliseconds, errorContent);
            Console.WriteLine($"Gemini LLM stream API error (Status {response.StatusCode}): {errorContent}. Falling back to non-streamed completion.");
            var fallback = await GenerateCompletionAsync(prompt, false);
            yield return fallback;
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
                    // Ignore transient chunk parse errors
                }

                if (!string.IsNullOrEmpty(chunkText))
                {
                    yield return chunkText;
                }
            }
        }
    }

    private string BuildPrompt(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### CONTEXT");
        sb.AppendLine("The user has asked a question in <query> and technical documentation excerpts are provided in <answers>.");
        sb.AppendLine("### INSTRUCTIONS");
        sb.AppendLine("- Answer the user's question in <query> based on the information in <answers>.");
        sb.AppendLine("- CRITICAL LANGUAGE REQUIREMENT: You MUST ALWAYS answer in the EXACT same language as the user's question in <query> (e.g. if the user asks in English, answer in English; if the user asks in Danish, answer in Danish; if in German, answer in German). NEVER reply in a different language.");
        sb.AppendLine("### CONSTRAINTS");
        sb.AppendLine("- Use ONLY the information in <answers> to answer the question in <query>.");
        sb.AppendLine("- No preamble, meta-commentary, or conversational filler.");
        sb.AppendLine("### OUTPUT FORMAT");
        sb.AppendLine("- Polite, professional, and advisory tone, providing a thorough and accurate response.");

        foreach (var chunk in contextChunks)
        {
            sb.AppendLine("<answers>");
            sb.AppendLine(chunk.Text);
            sb.AppendLine("</answers>");            
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine($"<query>{query}</query>");
        sb.AppendLine("Answer:");

        return sb.ToString();
    }

    private string GenerateLocalMockResponse(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Local Mock Response] Based on the retrieved context chunks:");
        sb.AppendLine();

        bool hasChunks = false;
        foreach (var chunk in contextChunks)
        {
            hasChunks = true;
            sb.AppendLine($"- From chunk '{chunk.Id}' (Doc: {chunk.DocumentId}): \"{chunk.Text}\"");
        }

        if (!hasChunks)
        {
            sb.AppendLine("- No relevant document context chunks were found to answer this query.");
        }

        sb.AppendLine();
        sb.AppendLine($"This is a mock answer generated locally for the query: '{query}'");
        return sb.ToString();
    }

    public async Task<string> GenerateCompletionAsync(string prompt, bool requireJson = false)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentNullException(nameof(prompt));

        var modelToUse = requireJson ? _fastModel : _model;
        var promptType = requireJson ? "JSON Structured" : "Text Completion";
        var stopwatch = Stopwatch.StartNew();

        _logger?.LogInformation(
            "Starting LLM [GenerateCompletion] ({PromptType}) with model {Model} (Prompt length: {PromptLength} chars).\nPrompt:\n{Prompt}",
            promptType, modelToUse, prompt.Length, prompt);

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            var mock = GenerateLocalMockCompletion(prompt, requireJson);
            stopwatch.Stop();
            _logger?.LogInformation(
                "LLM [GenerateCompletion] ({PromptType}) (Local Mock) completed in {DurationMs}ms (Response length: {ResponseLength} chars).",
                promptType, stopwatch.ElapsedMilliseconds, mock.Length);
            return mock;
        }

        try
        {
            // Use fast model and temperature 0.0 for structured JSON classification/evaluations to maximize speed
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelToUse}:generateContent?key={_apiKey}";

            object requestBody;
            if (requireJson)
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
                        temperature = 0.0,
                        maxOutputTokens = 2048,
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
                        temperature = 0.2,
                        topP = 0.95,
                        maxOutputTokens = 4096,
                        thinkingConfig = new
                        {
                            thinkingBudget = 0
                        }
                    }
                };
            }

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning(
                    "Gemini LLM API error (Status {StatusCode}) after {DurationMs}ms: {ErrorContent}. Falling back to mock.",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, errorContent);
                Console.WriteLine($"Gemini LLM API error (Status {response.StatusCode}): {errorContent}. Falling back to mock.");
                return GenerateLocalMockCompletion(prompt, requireJson);
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>();
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
                _logger?.LogInformation(
                    "LLM [GenerateCompletion] ({PromptType}) completed in {DurationMs}ms with model {Model} (Response length: {ResponseLength} chars).",
                    promptType, stopwatch.ElapsedMilliseconds, modelToUse, resultText.Length);
                return resultText;
            }

            _logger?.LogWarning("Failed to parse Gemini LLM response after {DurationMs}ms. Falling back to mock.", stopwatch.ElapsedMilliseconds);
            Console.WriteLine("Failed to parse Gemini LLM response. Falling back to mock.");
            return GenerateLocalMockCompletion(prompt, requireJson);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "Gemini LLM error after {DurationMs}ms: {Message}. Falling back to mock.", stopwatch.ElapsedMilliseconds, ex.Message);
            Console.WriteLine($"Gemini LLM error: {ex.Message}. Falling back to mock.");
            return GenerateLocalMockCompletion(prompt, requireJson);
        }
    }

    private string GenerateLocalMockCompletion(string prompt, bool requireJson)
    {
        if (!requireJson)
        {
            return $"[Local Mock Completion] Response for prompt:\n{prompt.Substring(0, Math.Min(prompt.Length, 100))}...";
        }

        // Return appropriate JSON mock structure based on prompt keyword inspection
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
            else if (promptLower.Contains("calculate") || promptLower.Contains("formula") || promptLower.Contains("voltage") && promptLower.Contains("current"))
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

        // Default generic JSON
        return "{}";
    }
}
