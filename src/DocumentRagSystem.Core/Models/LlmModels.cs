using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DocumentRagSystem.Core.Models;

/// <summary>
/// A single message in an LLM conversation or prompt.
/// </summary>
public sealed record LlmMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Provider-neutral request for text or structured generation.
/// </summary>
public sealed record LlmRequest(
    [property: JsonPropertyName("messages")] IReadOnlyList<LlmMessage> Messages,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("max_output_tokens")] int? MaxOutputTokens = null,
    [property: JsonPropertyName("require_json")] bool RequireJson = false,
    [property: JsonPropertyName("system_instruction")] string? SystemInstruction = null,
    [property: JsonPropertyName("response_schema")] object? ResponseSchema = null)
{
    /// <summary>
    /// Helper to create a standard request from a simple prompt.
    /// </summary>
    public static LlmRequest FromPrompt(
        string prompt,
        string? model = null,
        double? temperature = null,
        int? maxOutputTokens = null,
        bool requireJson = false,
        string? systemInstruction = null)
    {
        var messages = new List<LlmMessage>();
        if (!string.IsNullOrWhiteSpace(systemInstruction))
        {
            messages.Add(new LlmMessage("system", systemInstruction));
        }
        messages.Add(new LlmMessage("user", prompt));
        return new LlmRequest(messages, model, temperature, maxOutputTokens, requireJson, systemInstruction);
    }
}

/// <summary>
/// Telemetry and observability metadata returned by every LLM invocation.
/// </summary>
public sealed record LlmExecutionMetadata(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("structured_output_valid")] bool StructuredOutputValid,
    [property: JsonPropertyName("attempt_count")] int AttemptCount,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("is_fallback")] bool IsFallback = false,
    [property: JsonPropertyName("original_provider")] string? OriginalProvider = null,
    [property: JsonPropertyName("attempts")] IReadOnlyList<LlmAttemptRecord>? Attempts = null
)
{
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>
/// Information on a single attempt (e.g. during retry or fallback).
/// </summary>
public sealed record LlmAttemptRecord(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("duration_ms")] long? DurationMs = null);

/// <summary>
/// Text generation result containing the generated text and execution metadata.
/// </summary>
public sealed record LlmTextResult(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("metadata")] LlmExecutionMetadata Metadata);

/// <summary>
/// Structured generation result containing the typed value and execution metadata.
/// </summary>
public sealed record LlmStructuredResult<T>(
    [property: JsonPropertyName("value")] T? Value,
    [property: JsonPropertyName("metadata")] LlmExecutionMetadata Metadata,
    [property: JsonPropertyName("raw_content")] string? RawContent = null,
    [property: JsonPropertyName("is_success")] bool IsSuccess = true,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

/// <summary>
/// Model capabilities metadata.
/// </summary>
public sealed record LlmCapabilities(
    [property: JsonPropertyName("supports_structured_output")] bool SupportsStructuredOutput,
    [property: JsonPropertyName("supports_json_schema")] bool SupportsJsonSchema,
    [property: JsonPropertyName("supports_tool_calling")] bool SupportsToolCalling,
    [property: JsonPropertyName("context_window")] int? ContextWindow,
    [property: JsonPropertyName("supports_streaming")] bool SupportsStreaming);
