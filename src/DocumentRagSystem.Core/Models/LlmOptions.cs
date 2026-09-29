using System;
using System.Collections.Generic;

namespace DocumentRagSystem.Core.Models;

/// <summary>
/// Root configuration options for the LLM subsystem.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Default provider to use when no component-specific override is defined ("Gemini" or "Local").
    /// </summary>
    public string DefaultProvider { get; set; } = "Gemini";

    /// <summary>
    /// Per-component provider and model overrides keyed by component name or LlmPurpose (e.g. "QueryRefiner", "InputGovernor").
    /// </summary>
    public Dictionary<string, LlmComponentOptions> Components { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback configuration between providers.
    /// </summary>
    public LlmFallbackOptions Fallback { get; set; } = new();

    /// <summary>
    /// Local inference provider settings.
    /// </summary>
    public LocalLlmOptions Local { get; set; } = new();

    /// <summary>
    /// Gemini provider settings.
    /// </summary>
    public GeminiOptions Gemini { get; set; } = new();

    /// <summary>
    /// When true, disables fallback so benchmark evaluations reflect pure model performance.
    /// </summary>
    public bool EvaluationMode { get; set; } = false;
}

/// <summary>
/// Per-component configuration override.
/// </summary>
public sealed class LlmComponentOptions
{
    /// <summary>
    /// The provider to use for this component ("Gemini", "Local", etc.).
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Model identifier to use for this component.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Optional temperature override.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// Optional max output tokens override.
    /// </summary>
    public int? MaxOutputTokens { get; set; }
}

/// <summary>
/// Fallback configuration.
/// </summary>
public sealed class LlmFallbackOptions
{
    /// <summary>
    /// Whether automatic fallback on failure is enabled in production mode.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Primary provider to fall back from ("Local").
    /// </summary>
    public string From { get; set; } = "Local";

    /// <summary>
    /// Secondary provider to fall back to ("Gemini").
    /// </summary>
    public string To { get; set; } = "Gemini";
}

/// <summary>
/// Options for the Local LLM provider (e.g., vLLM, Ollama, LM Studio, TGI exposing OpenAI-compatible endpoints).
/// </summary>
public sealed class LocalLlmOptions
{
    /// <summary>
    /// Base URL of the OpenAI-compatible local inference server (e.g. "http://localhost:8080" or "http://localhost:11434").
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Default model name for local inference.
    /// </summary>
    public string Model { get; set; } = "local-model";

    /// <summary>
    /// Optional API key / Bearer token if the local gateway requires authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// HTTP timeout in seconds for local inference calls.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum retry attempts when structured JSON parsing/deserialization fails.
    /// </summary>
    public int MaxStructuredRetries { get; set; } = 1;

    /// <summary>
    /// Optional maximum concurrent requests to send to the local inference server (concurrency limiter).
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>
    /// Optional chat completion endpoint path (defaults to "/v1/chat/completions").
    /// </summary>
    public string ChatCompletionsPath { get; set; } = "/v1/chat/completions";

    /// <summary>
    /// Optional health check endpoint path (defaults to "/v1/models").
    /// </summary>
    public string HealthPath { get; set; } = "/v1/models";
}

/// <summary>
/// Options for Google Gemini provider.
/// </summary>
public sealed class GeminiOptions
{
    /// <summary>
    /// Gemini API key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Standard LLM model.
    /// </summary>
    public string LlmModel { get; set; } = "gemini-3.6-flash";

    /// <summary>
    /// Fast LLM model used for classification and structured JSON tasks.
    /// </summary>
    public string? FastLlmModel { get; set; }

    /// <summary>
    /// Timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}
