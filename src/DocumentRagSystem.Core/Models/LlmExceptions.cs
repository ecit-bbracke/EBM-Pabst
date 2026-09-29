using System;

namespace DocumentRagSystem.Core.Models;

/// <summary>
/// Base exception for all LLM provider failures.
/// </summary>
public class LlmException : Exception
{
    public string Provider { get; }
    public string? Model { get; }

    public LlmException(string provider, string? model, string message, Exception? innerException = null)
        : base($"[{provider}:{model ?? "default"}] {message}", innerException)
    {
        Provider = provider;
        Model = model;
    }
}

/// <summary>
/// Thrown when an LLM provider endpoint is unreachable or returns 5xx/connection errors.
/// </summary>
public class LlmProviderUnavailableException : LlmException
{
    public int? StatusCode { get; }

    public LlmProviderUnavailableException(string provider, string? model, string message, int? statusCode = null, Exception? innerException = null)
        : base(provider, model, message, innerException)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Thrown when an LLM request times out.
/// </summary>
public class LlmTimeoutException : LlmException
{
    public TimeSpan Timeout { get; }

    public LlmTimeoutException(string provider, string? model, TimeSpan timeout, Exception? innerException = null)
        : base(provider, model, $"Request timed out after {timeout.TotalSeconds} seconds.", innerException)
    {
        Timeout = timeout;
    }
}

/// <summary>
/// Thrown when structured output generation fails schema validation or JSON parsing after all bounded retries.
/// </summary>
public class LlmStructuredOutputException : LlmException
{
    public string? RawOutput { get; }
    public int Attempts { get; }

    public LlmStructuredOutputException(string provider, string? model, string message, string? rawOutput, int attempts, Exception? innerException = null)
        : base(provider, model, message, innerException)
    {
        RawOutput = rawOutput;
        Attempts = attempts;
    }
}
