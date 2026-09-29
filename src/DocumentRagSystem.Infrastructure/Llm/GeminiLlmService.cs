using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Llm;

/// <summary>
/// Backward-compatible ILlmService adapter delegating to an ILlmClient instance (defaults to GeminiLlmClient).
/// </summary>
public class GeminiLlmService : ILlmService
{
    private readonly ILlmClient _client;
    private readonly ILogger<GeminiLlmService>? _logger;

    public GeminiLlmService(
        string? apiKey = null, 
        string model = "gemini-3.6-flash", 
        string? fastModel = null, 
        HttpClient? httpClient = null,
        ILogger<GeminiLlmService>? logger = null)
    {
        _logger = logger;
        _client = new GeminiLlmClient(apiKey, model, fastModel, httpClient);
    }

    public GeminiLlmService(ILlmClient? client = null, ILogger<GeminiLlmService>? logger = null)
    {
        _logger = logger;
        _client = client ?? new GeminiLlmClient(null, "gemini-3.6-flash", null, null);
    }

    public async Task<string> GenerateResponseAsync(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentNullException(nameof(query));

        var prompt = BuildPrompt(query, contextChunks);
        var stopwatch = Stopwatch.StartNew();
        _logger?.LogInformation(
            "Starting LLM [GenerateResponse] call with model {Model} (Prompt length: {PromptLength} chars).\nPrompt:\n{Prompt}",
            "gemini-3.6-flash", prompt.Length, prompt);

        var request = LlmRequest.FromPrompt(prompt, requireJson: false);
        var result = await _client.GenerateTextAsync(request);
        stopwatch.Stop();

        _logger?.LogInformation(
            "LLM [GenerateResponse] completed in {DurationMs}ms with model {Model} (Response length: {ResponseLength} chars).",
            stopwatch.ElapsedMilliseconds, result.Metadata.Model, result.Content.Length);

        return result.Content;
    }

    public async Task<string> GenerateCompletionAsync(string prompt, bool requireJson = false)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentNullException(nameof(prompt));

        var promptType = requireJson ? "JSON Structured" : "Text Completion";
        var stopwatch = Stopwatch.StartNew();
        _logger?.LogInformation(
            "Starting LLM [GenerateCompletion] ({PromptType}) with model {Model} (Prompt length: {PromptLength} chars).\nPrompt:\n{Prompt}",
            promptType, "gemini-3.6-flash", prompt.Length, prompt);

        var request = LlmRequest.FromPrompt(prompt, requireJson: requireJson);
        var result = await _client.GenerateTextAsync(request);
        stopwatch.Stop();

        _logger?.LogInformation(
            "LLM [GenerateCompletion] ({PromptType}) completed in {DurationMs}ms with model {Model} (Response length: {ResponseLength} chars).",
            promptType, stopwatch.ElapsedMilliseconds, result.Metadata.Model, result.Content.Length);

        return result.Content;
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string query, 
        IEnumerable<DocumentChunk> contextChunks, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentNullException(nameof(query));

        var prompt = BuildPrompt(query, contextChunks);
        var request = LlmRequest.FromPrompt(prompt, requireJson: false);
        await foreach (var token in _client.StreamTextAsync(request, cancellationToken))
        {
            yield return token;
        }
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentNullException(nameof(prompt));

        var request = LlmRequest.FromPrompt(prompt, requireJson: false);
        await foreach (var token in _client.StreamTextAsync(request, cancellationToken))
        {
            yield return token;
        }
    }

    private static string BuildPrompt(string query, IEnumerable<DocumentChunk> contextChunks)
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
}
