using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Llm;

public class GeminiLlmService : ILlmService
{
    private readonly HttpClient? _httpClient;
    private readonly string? _apiKey;
    private readonly string _model;

    public GeminiLlmService(string? apiKey, string model = "gemini-1.5-flash")
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-1.5-flash" : model;

        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_GEMINI_API_KEY")
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
        }
    }

    public async Task<string> GenerateResponseAsync(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentNullException(nameof(query));

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            return GenerateLocalMockResponse(query, contextChunks);
        }

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

            var prompt = BuildPrompt(query, contextChunks);

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
                }
            };

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
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
                return textProp.GetString() ?? string.Empty;
            }

            Console.WriteLine("Failed to parse Gemini LLM response. Falling back to mock.");
            return GenerateLocalMockResponse(query, contextChunks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gemini LLM error: {ex.Message}. Falling back to mock.");
            return GenerateLocalMockResponse(query, contextChunks);
        }
    }

    private string BuildPrompt(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### CONTEXT");
        sb.AppendLine("Brugeren har stillet et spørgsmål i <query> og resultaterne fra en RAG search findes i <answers>");
        sb.AppendLine("### INSTRUCTIONS");
        sb.AppendLine("Besvar brugeren spørgsmål i <query> baseret på informationen i <answers>");
        sb.AppendLine("### CONSTRAINTS");
        sb.AppendLine("- Brug UDELUKKENDE informationen i <answers> til at besvare spørgsmålet i <query>");
        sb.AppendLine("- Ingen præ-ampel");
        sb.AppendLine("### OUTPUT FORMAT");
        sb.AppendLine("- Besvar i høflig og rådgivende tone, uden for mange detaljer, men stadig fyldestgørende.");

        foreach (var chunk in contextChunks)
        {
            // sb.AppendLine($"--- Chunk ID: {chunk.Id} (Document: {chunk.DocumentId}) ---");
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
}
