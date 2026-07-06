using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;

namespace DocumentRagSystem.Infrastructure.Embeddings;

public class GeminiEmbeddingService : IEmbeddingService
{
    private readonly HttpClient? _httpClient;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly int _fallbackDimensions = 768;

    public GeminiEmbeddingService(string? apiKey, string model = "text-embedding-004")
    {
        _model = string.IsNullOrWhiteSpace(model) ? "text-embedding-004" : model;
        
        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_GEMINI_API_KEY")
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
        }
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new float[_fallbackDimensions];
        }

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            return GenerateDeterministicMockEmbedding(text, _fallbackDimensions);
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
                        new { text = text }
                    }
                },
                outputDimensionality = _fallbackDimensions
            };

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Gemini API error (Status {response.StatusCode}): {errorContent}. Falling back to deterministic mock.");
                return GenerateDeterministicMockEmbedding(text, _fallbackDimensions);
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (jsonResult != null && 
                jsonResult.RootElement.TryGetProperty("embedding", out var embeddingProp) && 
                embeddingProp.TryGetProperty("values", out var valuesProp))
            {
                var values = valuesProp.EnumerateArray()
                                        .Select(v => (float)v.GetDouble())
                                        .ToArray();
                return values;
            }

            Console.WriteLine("Failed to parse Gemini response. Falling back to deterministic mock.");
            return GenerateDeterministicMockEmbedding(text, _fallbackDimensions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gemini Embedding error: {ex.Message}. Falling back to deterministic mock.");
            return GenerateDeterministicMockEmbedding(text, _fallbackDimensions);
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
