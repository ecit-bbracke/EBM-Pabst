using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentRagSystem.Infrastructure.Llm;

public class GeminiSpeechToTextService : ISpeechToTextService
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly ILogger<GeminiSpeechToTextService>? _logger;

    public GeminiSpeechToTextService(
        IOptions<LlmOptions> options,
        IConfiguration? configuration = null,
        HttpClient? httpClient = null,
        ILogger<GeminiSpeechToTextService>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;

        var key = options?.Value?.Gemini?.ApiKey;
        if (string.IsNullOrWhiteSpace(key) || key == "YOUR_GEMINI_API_KEY")
        {
            key = AppConfigurationHelper.GetGeminiApiKey(configuration);
        }
        _apiKey = key;

        _model = options?.Value?.Gemini?.FastLlmModel ?? options?.Value?.Gemini?.LlmModel ?? "gemini-3.6-flash";
    }

    public async Task<string> TranscribeAudioAsync(
        Stream audioStream,
        string mimeType,
        string? languageHint = null,
        CancellationToken cancellationToken = default)
    {
        if (audioStream == null) throw new ArgumentNullException(nameof(audioStream));

        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, cancellationToken);
        var audioBytes = ms.ToArray();
        if (audioBytes.Length == 0) return string.Empty;

        var base64Audio = Convert.ToBase64String(audioBytes);
        var cleanMime = string.IsNullOrWhiteSpace(mimeType) ? "audio/webm" : mimeType.Split(';')[0].Trim();

        var langInstruction = !string.IsNullOrWhiteSpace(languageHint) && languageHint.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "Transcribe the audio speech verbatim into English. If model codes, numbers, or technical parameters are spoken, transcribe them accurately."
            : "Transkriber talen i lydklippet ordret til tekst. Hvis sproget ikke kan bestemmes entydigt, eller hvis der nævnes modelkoder, tal eller tekniske parametre, skal det transskriberes på dansk (da-DK).";

        var prompt = $"{langInstruction} Return ONLY the transcription text, without quotation marks, pleasantries, or explanations.";

        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "YOUR_GEMINI_API_KEY")
        {
            _logger?.LogWarning("[GeminiSTT] No Gemini API key configured, returning empty transcription.");
            return string.Empty;
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inlineData = new
                            {
                                mimeType = cleanMime,
                                data = base64Audio
                            }
                        },
                        new
                        {
                            text = prompt
                        }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,
                maxOutputTokens = 1024,
                thinkingConfig = new
                {
                    thinkingBudget = 0
                }
            }
        };

        var jsonPayload = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger?.LogError("[GeminiSTT] Transcription API call failed with {StatusCode}: {Error}", response.StatusCode, err);
            return string.Empty;
        }

        var resJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(resJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var contentElem) &&
            contentElem.TryGetProperty("parts", out var parts) &&
            parts.GetArrayLength() > 0 &&
            parts[0].TryGetProperty("text", out var textElem))
        {
            return textElem.GetString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }
}
