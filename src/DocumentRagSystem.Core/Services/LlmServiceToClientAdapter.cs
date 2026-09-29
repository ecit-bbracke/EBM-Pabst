using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

/// <summary>
/// Bridges legacy ILlmService instances to the ILlmClient interface.
/// </summary>
public sealed class LlmServiceToClientAdapter : ILlmClient
{
    private readonly ILlmService _service;

    public string ProviderName => "Gemini";
    public ILlmService UnderlyingService => _service;

    public LlmServiceToClientAdapter(ILlmService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public async Task<LlmTextResult> GenerateTextAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var prompt = FormatPrompt(request);
        var stopwatch = Stopwatch.StartNew();
        var response = await _service.GenerateCompletionAsync(prompt, request.RequireJson) ?? string.Empty;
        stopwatch.Stop();

        var meta = new LlmExecutionMetadata(
            Provider: ProviderName,
            Model: request.Model ?? "default",
            DurationMs: stopwatch.ElapsedMilliseconds,
            InputTokens: prompt.Length / 4,
            OutputTokens: response.Length / 4,
            StructuredOutputValid: !request.RequireJson || IsValidJson(response),
            AttemptCount: 1);

        return new LlmTextResult(response, meta);
    }

    public async Task<LlmStructuredResult<T>> GenerateStructuredAsync<T>(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var jsonReq = request with { RequireJson = true };
        var textResult = await GenerateTextAsync(jsonReq, cancellationToken);
        var rawContent = CleanJsonFences(textResult.Content);

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var deserialized = JsonSerializer.Deserialize<T>(rawContent, options);
            if (deserialized != null)
            {
                return new LlmStructuredResult<T>(deserialized, textResult.Metadata with { StructuredOutputValid = true }, rawContent, IsSuccess: true);
            }

            return new LlmStructuredResult<T>(default, textResult.Metadata with { StructuredOutputValid = false }, rawContent, IsSuccess: false, ErrorMessage: "Deserialized value was null");
        }
        catch (Exception ex)
        {
            return new LlmStructuredResult<T>(default, textResult.Metadata with { StructuredOutputValid = false, Error = ex.Message }, rawContent, IsSuccess: false, ErrorMessage: ex.Message);
        }
    }

    public async IAsyncEnumerable<string> StreamTextAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = FormatPrompt(request);
        await foreach (var token in _service.StreamCompletionAsync(prompt, cancellationToken))
        {
            yield return token;
        }
    }

    private static string FormatPrompt(LlmRequest request)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            sb.AppendLine(request.SystemInstruction);
            sb.AppendLine();
        }
        foreach (var msg in request.Messages)
        {
            sb.AppendLine(msg.Content);
        }
        return sb.ToString().Trim();
    }

    private static string CleanJsonFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(7);
            if (cleaned.EndsWith("```")) cleaned = cleaned.Substring(0, cleaned.Length - 3);
        }
        else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(3);
            if (cleaned.EndsWith("```")) cleaned = cleaned.Substring(0, cleaned.Length - 3);
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
        catch { return false; }
    }
}

/// <summary>
/// Bridges ILlmClient instances to the legacy ILlmService interface.
/// </summary>
public sealed class LlmClientToServiceAdapter : ILlmService
{
    private readonly ILlmClient _client;

    public LlmClientToServiceAdapter(ILlmClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<string> GenerateResponseAsync(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        if (_client is LlmServiceToClientAdapter adapter)
        {
            return await adapter.UnderlyingService.GenerateResponseAsync(query, contextChunks);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("### CONTEXT");
        sb.AppendLine("The user has asked a question in <query> and technical documentation excerpts are provided in <answers>.");
        sb.AppendLine("### INSTRUCTIONS");
        sb.AppendLine("- Answer the user's question in <query> based on the information in <answers>.");
        sb.AppendLine("- CRITICAL LANGUAGE REQUIREMENT: You MUST ALWAYS answer in the EXACT same language as the user's question in <query>.");
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

        var request = LlmRequest.FromPrompt(sb.ToString(), requireJson: false);
        var result = await _client.GenerateTextAsync(request);
        return result.Content;
    }

    public async Task<string> GenerateCompletionAsync(string prompt, bool requireJson = false)
    {
        if (_client is LlmServiceToClientAdapter adapter)
        {
            return await adapter.UnderlyingService.GenerateCompletionAsync(prompt, requireJson);
        }

        var request = LlmRequest.FromPrompt(prompt, requireJson: requireJson);
        var result = await _client.GenerateTextAsync(request);
        return result.Content;
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string query,
        IEnumerable<DocumentChunk> contextChunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_client is LlmServiceToClientAdapter adapter)
        {
            await foreach (var token in adapter.UnderlyingService.StreamResponseAsync(query, contextChunks, cancellationToken))
            {
                yield return token;
            }
            yield break;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var chunk in contextChunks)
        {
            sb.AppendLine("<answers>");
            sb.AppendLine(chunk.Text);
            sb.AppendLine("</answers>");
        }
        sb.AppendLine($"<query>{query}</query>");

        var request = LlmRequest.FromPrompt(sb.ToString(), requireJson: false);
        await foreach (var token in _client.StreamTextAsync(request, cancellationToken))
        {
            yield return token;
        }
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_client is LlmServiceToClientAdapter adapter)
        {
            await foreach (var token in adapter.UnderlyingService.StreamCompletionAsync(prompt, cancellationToken))
            {
                yield return token;
            }
            yield break;
        }

        var request = LlmRequest.FromPrompt(prompt, requireJson: false);
        await foreach (var token in _client.StreamTextAsync(request, cancellationToken))
        {
            yield return token;
        }
    }
}
