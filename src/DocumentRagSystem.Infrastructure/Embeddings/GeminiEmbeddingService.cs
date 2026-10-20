using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Infrastructure.Embeddings;

public class GeminiEmbeddingService : IEmbeddingService
{
    private readonly HttpClient? _httpClient;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly int _fallbackDimensions = 768;
    private readonly ILogger<GeminiEmbeddingService>? _logger;
    private sealed record CacheEntry(float[] Vector, DateTime LastAccessed);
    private static readonly ConcurrentDictionary<string, CacheEntry> _embeddingCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheEntries = 10000;

    public GeminiEmbeddingService(
        string? apiKey,
        string model = "gemini-embedding-001",
        HttpClient? httpClient = null,
        ILogger<GeminiEmbeddingService>? logger = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-embedding-001" : model;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_GEMINI_API_KEY")
        {
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient();
        }
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new float[_fallbackDimensions];
        }

        var trimmedText = text.Trim();
        var stopwatch = Stopwatch.StartNew();

        if (_embeddingCache.TryGetValue(trimmedText, out var entry))
        {
            stopwatch.Stop();
            _logger?.LogDebug(
                "[GeminiEmbedding] Cache hit for text ({Length} chars) in {DurationMs}ms.",
                trimmedText.Length, stopwatch.ElapsedMilliseconds);
            _embeddingCache[trimmedText] = entry with { LastAccessed = DateTime.UtcNow };
            return entry.Vector;
        }

        var textPreview = trimmedText.Length <= 80 ? trimmedText : $"{trimmedText.Substring(0, 77)}...";

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            var mock = GenerateDeterministicMockEmbedding(trimmedText, _fallbackDimensions);
            StoreInCache(trimmedText, mock);
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

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger?.LogError(
                    "[GeminiEmbedding] API error (Status {StatusCode}) after {DurationMs}ms: {ErrorContent}",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, errorContent);
                throw new HttpRequestException($"[GeminiEmbedding] API error (Status {response.StatusCode}): {errorContent}");
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (jsonResult != null &&
                jsonResult.RootElement.TryGetProperty("embedding", out var embeddingProp) &&
                embeddingProp.TryGetProperty("values", out var valuesProp))
            {
                var values = valuesProp.EnumerateArray()
                                        .Select(v => (float)v.GetDouble())
                                        .ToArray();
                StoreInCache(trimmedText, values);
                _logger?.LogInformation(
                    "[GeminiEmbedding] Generated embedding with model {Model} in {DurationMs}ms (Dimensions: {Dimensions}, Text: \"{TextPreview}\").",
                    _model, stopwatch.ElapsedMilliseconds, values.Length, textPreview);
                return values;
            }

            _logger?.LogError(
                "[GeminiEmbedding] Failed to parse response after {DurationMs}ms.",
                stopwatch.ElapsedMilliseconds);
            throw new InvalidOperationException("[GeminiEmbedding] Failed to parse embedding values from Gemini API response.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex,
                "[GeminiEmbedding] Error after {DurationMs}ms: {Message}.",
                stopwatch.ElapsedMilliseconds, ex.Message);

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                var exFallback = GenerateDeterministicMockEmbedding(trimmedText, _fallbackDimensions);
                StoreInCache(trimmedText, exFallback);
                return exFallback;
            }

            throw;
        }
    }

    public async Task<IList<float[]>> GenerateEmbeddingsAsync(IList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts == null || texts.Count == 0)
        {
            return new List<float[]>();
        }

        var results = new float[texts.Count][];
        var uncachedIndices = new List<int>();

        // 1. Check in-memory cache first
        for (int i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                results[i] = new float[_fallbackDimensions];
                continue;
            }

            var trimmed = text.Trim();
            if (_embeddingCache.TryGetValue(trimmed, out var entry))
            {
                _embeddingCache[trimmed] = entry with { LastAccessed = DateTime.UtcNow };
                results[i] = entry.Vector;
            }
            else
            {
                uncachedIndices.Add(i);
            }
        }

        if (uncachedIndices.Count == 0)
        {
            return results;
        }

        // 2. If no API key or HTTP client, generate mock embeddings for uncached
        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            foreach (var idx in uncachedIndices)
            {
                var trimmed = texts[idx].Trim();
                var mock = GenerateDeterministicMockEmbedding(trimmed, _fallbackDimensions);
                StoreInCache(trimmed, mock);
                results[idx] = mock;
            }
            return results;
        }

        // 3. Batch requests in chunks of up to 100 (Gemini batch limit)
        var modelResourceName = _model.StartsWith("models/") ? _model : $"models/{_model}";
        var batchUrl = $"https://generativelanguage.googleapis.com/v1beta/{modelResourceName}:batchEmbedContents?key={_apiKey}";

        const int maxBatchSize = 100;
        for (int offset = 0; offset < uncachedIndices.Count; offset += maxBatchSize)
        {
            var batchIndices = uncachedIndices.Skip(offset).Take(maxBatchSize).ToList();
            var requests = batchIndices.Select(idx => new
            {
                model = modelResourceName,
                content = new
                {
                    parts = new[]
                    {
                        new { text = texts[idx].Trim() }
                    }
                },
                outputDimensionality = _fallbackDimensions
            }).ToList();

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(batchUrl, new { requests }, cancellationToken);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger?.LogError(
                        "[GeminiEmbedding] batchEmbedContents API error (Status {StatusCode}) after {DurationMs}ms: {ErrorContent}",
                        response.StatusCode, stopwatch.ElapsedMilliseconds, errorContent);

                    if (string.IsNullOrWhiteSpace(_apiKey))
                    {
                        foreach (var idx in batchIndices)
                        {
                            var trimmed = texts[idx].Trim();
                            var fallback = GenerateDeterministicMockEmbedding(trimmed, _fallbackDimensions);
                            StoreInCache(trimmed, fallback);
                            results[idx] = fallback;
                        }
                        continue;
                    }

                    throw new HttpRequestException($"[GeminiEmbedding] batchEmbedContents API error (Status {response.StatusCode}): {errorContent}");
                }

                var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>();
                if (jsonResult != null &&
                    jsonResult.RootElement.TryGetProperty("embeddings", out var embeddingsProp) &&
                    embeddingsProp.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var item in embeddingsProp.EnumerateArray())
                    {
                        if (i >= batchIndices.Count) break;
                        var targetIdx = batchIndices[i];
                        var trimmed = texts[targetIdx].Trim();

                        if (item.TryGetProperty("values", out var valuesProp) && valuesProp.ValueKind == JsonValueKind.Array)
                        {
                            var values = valuesProp.EnumerateArray()
                                .Select(v => (float)v.GetDouble())
                                .ToArray();
                            StoreInCache(trimmed, values);
                            results[targetIdx] = values;
                        }
                        else
                        {
                            var fallback = GenerateDeterministicMockEmbedding(trimmed, _fallbackDimensions);
                            StoreInCache(trimmed, fallback);
                            results[targetIdx] = fallback;
                        }
                        i++;
                    }

                    _logger?.LogInformation(
                        "[GeminiEmbedding] Batch generated {Count} embeddings with model {Model} in {DurationMs}ms.",
                        batchIndices.Count, _model, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    _logger?.LogWarning(
                        "[GeminiEmbedding] Failed to parse batchEmbedContents response after {DurationMs}ms. Falling back to mocks.",
                        stopwatch.ElapsedMilliseconds);

                    foreach (var idx in batchIndices)
                    {
                        var trimmed = texts[idx].Trim();
                        var fallback = GenerateDeterministicMockEmbedding(trimmed, _fallbackDimensions);
                        StoreInCache(trimmed, fallback);
                        results[idx] = fallback;
                    }
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger?.LogError(ex,
                    "[GeminiEmbedding] Batch embedding error after {DurationMs}ms: {Message}.",
                    stopwatch.ElapsedMilliseconds, ex.Message);

                if (string.IsNullOrWhiteSpace(_apiKey))
                {
                    foreach (var idx in batchIndices)
                    {
                        var trimmed = texts[idx].Trim();
                        var fallback = GenerateDeterministicMockEmbedding(trimmed, _fallbackDimensions);
                        StoreInCache(trimmed, fallback);
                        results[idx] = fallback;
                    }
                }
                else
                {
                    throw;
                }
            }
        }

        return results;
    }

    private static void StoreInCache(string key, float[] vector)
    {
        if (_embeddingCache.Count >= MaxCacheEntries)
        {
            var keysToRemove = _embeddingCache
                .OrderBy(kv => kv.Value.LastAccessed)
                .Take(1000)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in keysToRemove)
            {
                _embeddingCache.TryRemove(k, out _);
            }
        }
        _embeddingCache[key] = new CacheEntry(vector, DateTime.UtcNow);
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
