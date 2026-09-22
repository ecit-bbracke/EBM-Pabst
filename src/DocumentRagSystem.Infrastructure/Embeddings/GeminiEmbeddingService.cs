using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DocumentRagSystem.Core.Interfaces;

namespace DocumentRagSystem.Infrastructure.Embeddings;

public class GeminiEmbeddingService : IEmbeddingService
{
    private readonly HttpClient? _httpClient;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly int _fallbackDimensions = 768;
    private readonly ILogger<GeminiEmbeddingService>? _logger;
    private static readonly ConcurrentDictionary<string, float[]> _embeddingCache = new(StringComparer.Ordinal);

    public GeminiEmbeddingService(
        string? apiKey, 
        string model = "text-embedding-004", 
        HttpClient? httpClient = null,
        ILogger<GeminiEmbeddingService>? logger = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "text-embedding-004" : model;
        _logger = logger;
        
        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_GEMINI_API_KEY")
        {
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient();
        }
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new float[_fallbackDimensions];
        }

        var trimmedText = text.Trim();
        var stopwatch = Stopwatch.StartNew();

        if (_embeddingCache.TryGetValue(trimmedText, out var cachedVector))
        {
            stopwatch.Stop();
            _logger?.LogDebug(
                "[GeminiEmbedding] Cache hit for text ({Length} chars) in {DurationMs}ms.",
                trimmedText.Length, stopwatch.ElapsedMilliseconds);
            return cachedVector;
        }

        var textPreview = trimmedText.Length <= 80 ? trimmedText : $"{trimmedText.Substring(0, 77)}...";

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            var mock = GenerateDeterministicMockEmbedding(trimmedText, _fallbackDimensions);
            _embeddingCache.TryAdd(trimmedText, mock);
            stopwatch.Stop();
            _logger?.LogDebug(
                "[GeminiEmbedding] Generated mock embedding in {DurationMs}ms for text: \"{TextPreview}\".",
                stopwatch.ElapsedMilliseconds, textPreview);
            return mock;
        }

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:embedContent?key={_apiKey}";
            
            var requestBody = new
            {
                content = new
                {
                    parts = new[]
                    {
                        new { text = trimmedText }
                    }
                },
                outputDimensionality = _fallbackDimensions
            };

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody);
            stopwatch.Stop();
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning(
                    "[GeminiEmbedding] API error (Status {StatusCode}) after {DurationMs}ms: {ErrorContent}. Falling back to deterministic mock.",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, errorContent);
                Console.WriteLine($"Gemini API error (Status {response.StatusCode}): {errorContent}. Falling back to deterministic mock.");
                var fallback = GenerateDeterministicMockEmbedding(trimmedText, _fallbackDimensions);
                _embeddingCache.TryAdd(trimmedText, fallback);
                return fallback;
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (jsonResult != null && 
                jsonResult.RootElement.TryGetProperty("embedding", out var embeddingProp) && 
                embeddingProp.TryGetProperty("values", out var valuesProp))
            {
                var values = valuesProp.EnumerateArray()
                                        .Select(v => (float)v.GetDouble())
                                        .ToArray();
                _embeddingCache.TryAdd(trimmedText, values);
                _logger?.LogInformation(
                    "[GeminiEmbedding] Generated embedding with model {Model} in {DurationMs}ms (Dimensions: {Dimensions}, Text: \"{TextPreview}\").",
                    _model, stopwatch.ElapsedMilliseconds, values.Length, textPreview);
                return values;
            }

            _logger?.LogWarning(
                "[GeminiEmbedding] Failed to parse response after {DurationMs}ms. Falling back to deterministic mock.",
                stopwatch.ElapsedMilliseconds);
            Console.WriteLine("Failed to parse Gemini response. Falling back to deterministic mock.");
            var parsedFallback = GenerateDeterministicMockEmbedding(trimmedText, _fallbackDimensions);
            _embeddingCache.TryAdd(trimmedText, parsedFallback);
            return parsedFallback;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex,
                "[GeminiEmbedding] Error after {DurationMs}ms: {Message}. Falling back to deterministic mock.",
                stopwatch.ElapsedMilliseconds, ex.Message);
            Console.WriteLine($"Gemini Embedding error: {ex.Message}. Falling back to deterministic mock.");
            var exFallback = GenerateDeterministicMockEmbedding(trimmedText, _fallbackDimensions);
            _embeddingCache.TryAdd(trimmedText, exFallback);
            return exFallback;
        }
    }

    private static float[] GenerateDeterministicMockEmbedding(string text, int dimensions)
    {
        var result = new float[dimensions];
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        
        for (int i = 0; i < dimensions; i++)
        {
            int hashVal = hashBytes[i % hashBytes.Length];
            result[i] = (float)Math.Sin(hashVal + i) * 0.1f;
        }

        float sumOfSquares = 0f;
        for (int i = 0; i < dimensions; i++)
        {
            sumOfSquares += result[i] * result[i];
        }
        float norm = (float)Math.Sqrt(sumOfSquares);
        if (norm > 0)
        {
            for (int i = 0; i < dimensions; i++)
            {
                result[i] /= norm;
            }
        }

        return result;
    }
}
