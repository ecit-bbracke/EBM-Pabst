using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Llm;

/// <summary>
/// Decorator around a primary ILlmClient that automatically falls back to a secondary ILlmClient
/// upon connectivity, timeout, or structured-output failures when fallback is enabled.
/// </summary>
public sealed class FallbackLlmClient : ILlmClient
{
    private readonly ILlmClient _primary;
    private readonly ILlmClient _fallback;
    private readonly bool _fallbackEnabled;
    private readonly ILogger<FallbackLlmClient>? _logger;

    public string ProviderName => _primary.ProviderName;

    public FallbackLlmClient(
        ILlmClient primary,
        ILlmClient fallback,
        bool fallbackEnabled,
        ILogger<FallbackLlmClient>? logger = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _fallbackEnabled = fallbackEnabled;
        _logger = logger;
    }

    public async Task<LlmTextResult> GenerateTextAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_fallbackEnabled)
        {
            return await _primary.GenerateTextAsync(request, cancellationToken);
        }

        var attempts = new List<LlmAttemptRecord>();
        var primaryStopwatch = Stopwatch.StartNew();

        try
        {
            var primaryResult = await _primary.GenerateTextAsync(request, cancellationToken);
            primaryStopwatch.Stop();

            if (primaryResult.Metadata.Error != null)
            {
                attempts.Add(new LlmAttemptRecord(_primary.ProviderName, "warning", primaryResult.Metadata.Error, primaryStopwatch.ElapsedMilliseconds));
            }
            else
            {
                attempts.Add(new LlmAttemptRecord(_primary.ProviderName, "success", null, primaryStopwatch.ElapsedMilliseconds));
            }

            return primaryResult with
            {
                Metadata = primaryResult.Metadata with
                {
                    Attempts = attempts
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested == false)
        {
            primaryStopwatch.Stop();
            attempts.Add(new LlmAttemptRecord(_primary.ProviderName, "failed", ex.Message, primaryStopwatch.ElapsedMilliseconds));

            _logger?.LogWarning(
                ex,
                "[{PrimaryProvider}] failed for GenerateText. Falling back to [{FallbackProvider}]. Error: {Error}",
                _primary.ProviderName, _fallback.ProviderName, ex.Message);

            var fallbackStopwatch = Stopwatch.StartNew();
            var fallbackResult = await _fallback.GenerateTextAsync(request, cancellationToken);
            fallbackStopwatch.Stop();

            attempts.Add(new LlmAttemptRecord(_fallback.ProviderName, "success", null, fallbackStopwatch.ElapsedMilliseconds));

            var mergedMetadata = fallbackResult.Metadata with
            {
                IsFallback = true,
                OriginalProvider = _primary.ProviderName,
                Attempts = attempts
            };

            return fallbackResult with { Metadata = mergedMetadata };
        }
    }

    public async Task<LlmStructuredResult<T>> GenerateStructuredAsync<T>(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_fallbackEnabled)
        {
            return await _primary.GenerateStructuredAsync<T>(request, cancellationToken);
        }

        var attempts = new List<LlmAttemptRecord>();
        var primaryStopwatch = Stopwatch.StartNew();

        try
        {
            var primaryResult = await _primary.GenerateStructuredAsync<T>(request, cancellationToken);
            primaryStopwatch.Stop();

            if (primaryResult.IsSuccess && primaryResult.Metadata.StructuredOutputValid)
            {
                attempts.Add(new LlmAttemptRecord(_primary.ProviderName, "success", null, primaryStopwatch.ElapsedMilliseconds));
                return primaryResult with
                {
                    Metadata = primaryResult.Metadata with
                    {
                        Attempts = attempts
                    }
                };
            }

            attempts.Add(new LlmAttemptRecord(_primary.ProviderName, "structured_failure", primaryResult.ErrorMessage, primaryStopwatch.ElapsedMilliseconds));

            _logger?.LogWarning(
                "[{PrimaryProvider}] structured output invalid/failed for {Type}. Falling back to [{FallbackProvider}]. Error: {Error}",
                _primary.ProviderName, typeof(T).Name, _fallback.ProviderName, primaryResult.ErrorMessage);

            var fallbackStopwatch = Stopwatch.StartNew();
            var fallbackResult = await _fallback.GenerateStructuredAsync<T>(request, cancellationToken);
            fallbackStopwatch.Stop();

            attempts.Add(new LlmAttemptRecord(_fallback.ProviderName, fallbackResult.IsSuccess ? "success" : "failed", fallbackResult.ErrorMessage, fallbackStopwatch.ElapsedMilliseconds));

            var mergedMetadata = fallbackResult.Metadata with
            {
                IsFallback = true,
                OriginalProvider = _primary.ProviderName,
                Attempts = attempts
            };

            return fallbackResult with { Metadata = mergedMetadata };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested == false)
        {
            primaryStopwatch.Stop();
            attempts.Add(new LlmAttemptRecord(_primary.ProviderName, "exception", ex.Message, primaryStopwatch.ElapsedMilliseconds));

            _logger?.LogWarning(
                ex,
                "[{PrimaryProvider}] threw exception for structured {Type}. Falling back to [{FallbackProvider}]. Error: {Error}",
                _primary.ProviderName, typeof(T).Name, _fallback.ProviderName, ex.Message);

            var fallbackStopwatch = Stopwatch.StartNew();
            var fallbackResult = await _fallback.GenerateStructuredAsync<T>(request, cancellationToken);
            fallbackStopwatch.Stop();

            attempts.Add(new LlmAttemptRecord(_fallback.ProviderName, fallbackResult.IsSuccess ? "success" : "failed", fallbackResult.ErrorMessage, fallbackStopwatch.ElapsedMilliseconds));

            var mergedMetadata = fallbackResult.Metadata with
            {
                IsFallback = true,
                OriginalProvider = _primary.ProviderName,
                Attempts = attempts
            };

            return fallbackResult with { Metadata = mergedMetadata };
        }
    }

    public async IAsyncEnumerable<string> StreamTextAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_fallbackEnabled)
        {
            await foreach (var token in _primary.StreamTextAsync(request, cancellationToken))
            {
                yield return token;
            }
            yield break;
        }

        var queue = new System.Collections.Generic.Queue<string>();
        var enumerator = _primary.StreamTextAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool primaryFailed = false;
        bool hasProducedTokens = false;

        while (true)
        {
            bool hasNext = false;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (Exception ex)
            {
                if (!hasProducedTokens)
                {
                    primaryFailed = true;
                    _logger?.LogWarning(ex, "[{PrimaryProvider}] stream failed on start. Falling back to [{FallbackProvider}].", _primary.ProviderName, _fallback.ProviderName);
                }
                else
                {
                    _logger?.LogError(ex, "[{PrimaryProvider}] stream failed mid-stream.", _primary.ProviderName);
                    break;
                }
            }

            if (primaryFailed)
            {
                await enumerator.DisposeAsync();
                break;
            }

            if (!hasNext)
            {
                await enumerator.DisposeAsync();
                break;
            }

            hasProducedTokens = true;
            yield return enumerator.Current;
        }

        if (primaryFailed)
        {
            await foreach (var token in _fallback.StreamTextAsync(request, cancellationToken))
            {
                yield return token;
            }
        }
    }
}
