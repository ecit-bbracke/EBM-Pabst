using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Llm;

/// <summary>
/// Resolves ILlmClient instances for specific architectural purposes or by provider name,
/// applying component configurations and fallback policies.
/// </summary>
public sealed class LlmClientResolver : ILlmClientResolver
{
    private readonly LlmOptions _options;
    private readonly GeminiLlmClient _geminiClient;
    private readonly LocalLlmClient _localClient;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<LlmClientResolver>? _logger;
    private readonly ConcurrentDictionary<string, ILlmClient> _purposeCache = new(StringComparer.OrdinalIgnoreCase);

    public LlmClientResolver(
        IOptions<LlmOptions> options,
        GeminiLlmClient geminiClient,
        LocalLlmClient localClient,
        ILoggerFactory? loggerFactory = null,
        ILogger<LlmClientResolver>? logger = null)
    {
        _options = options?.Value ?? new LlmOptions();
        _geminiClient = geminiClient ?? throw new ArgumentNullException(nameof(geminiClient));
        _localClient = localClient ?? throw new ArgumentNullException(nameof(localClient));
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public LlmClientResolver(
        LlmOptions options,
        GeminiLlmClient geminiClient,
        LocalLlmClient localClient,
        ILoggerFactory? loggerFactory = null)
        : this(Microsoft.Extensions.Options.Options.Create(options), geminiClient, localClient, loggerFactory)
    {
    }

    public ILlmClient Resolve(LlmPurpose purpose)
    {
        var key = purpose.ToString();
        return _purposeCache.GetOrAdd(key, _ => BuildClientForPurpose(purpose));
    }

    public ILlmClient Resolve(string providerName)
    {
        if (string.Equals(providerName, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return _localClient;
        }

        if (string.Equals(providerName, "Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return _geminiClient;
        }

        _logger?.LogWarning("Unknown LLM provider '{ProviderName}'. Defaulting to {DefaultProvider}.", providerName, _options.DefaultProvider);
        return string.Equals(_options.DefaultProvider, "Local", StringComparison.OrdinalIgnoreCase)
            ? _localClient
            : _geminiClient;
    }

    private ILlmClient BuildClientForPurpose(LlmPurpose purpose)
    {
        var purposeName = purpose.ToString();
        LlmComponentOptions? compOptions = null;

        if (_options.Components != null && _options.Components.TryGetValue(purposeName, out var found))
        {
            compOptions = found;
        }

        var providerName = compOptions?.Provider;
        if (string.IsNullOrWhiteSpace(providerName))
        {
            providerName = _options.DefaultProvider;
        }

        var baseClient = Resolve(providerName);

        // Apply component-level defaults decorator if model/temperature/max tokens are specified
        ILlmClient effectiveClient = baseClient;
        if (compOptions != null && (!string.IsNullOrWhiteSpace(compOptions.Model) || compOptions.Temperature.HasValue || compOptions.MaxOutputTokens.HasValue))
        {
            effectiveClient = new ComponentConfiguredLlmClient(baseClient, compOptions.Model, compOptions.Temperature, compOptions.MaxOutputTokens);
        }

        // Apply fallback decorator if configured and not in evaluation mode
        if (_options.Fallback != null &&
            _options.Fallback.Enabled &&
            !_options.EvaluationMode &&
            string.Equals(baseClient.ProviderName, _options.Fallback.From, StringComparison.OrdinalIgnoreCase))
        {
            var fallbackTarget = Resolve(_options.Fallback.To);
            var fallbackLogger = _loggerFactory?.CreateLogger<FallbackLlmClient>();
            effectiveClient = new FallbackLlmClient(effectiveClient, fallbackTarget, fallbackEnabled: true, fallbackLogger);
            _logger?.LogInformation(
                "[LlmClientResolver] Configured [{Purpose}] with primary [{Primary}] and fallback [{Fallback}].",
                purposeName, baseClient.ProviderName, fallbackTarget.ProviderName);
        }
        else
        {
            _logger?.LogInformation(
                "[LlmClientResolver] Configured [{Purpose}] with provider [{Provider}].",
                purposeName, baseClient.ProviderName);
        }

        return effectiveClient;
    }

    /// <summary>
    /// Decorator that applies component-specific defaults (Model, Temperature, MaxOutputTokens)
    /// to LlmRequests if the caller hasn't explicitly set them.
    /// </summary>
    private sealed class ComponentConfiguredLlmClient : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly string? _defaultModel;
        private readonly double? _defaultTemperature;
        private readonly int? _defaultMaxTokens;

        public string ProviderName => _inner.ProviderName;

        public ComponentConfiguredLlmClient(ILlmClient inner, string? defaultModel, double? defaultTemperature, int? defaultMaxTokens)
        {
            _inner = inner;
            _defaultModel = defaultModel;
            _defaultTemperature = defaultTemperature;
            _defaultMaxTokens = defaultMaxTokens;
        }

        private LlmRequest ApplyDefaults(LlmRequest request)
        {
            return request with
            {
                Model = request.Model ?? _defaultModel,
                Temperature = request.Temperature ?? _defaultTemperature,
                MaxOutputTokens = request.MaxOutputTokens ?? _defaultMaxTokens
            };
        }

        public Task<LlmTextResult> GenerateTextAsync(LlmRequest request, System.Threading.CancellationToken cancellationToken = default)
            => _inner.GenerateTextAsync(ApplyDefaults(request), cancellationToken);

        public Task<LlmStructuredResult<T>> GenerateStructuredAsync<T>(LlmRequest request, System.Threading.CancellationToken cancellationToken = default)
            => _inner.GenerateStructuredAsync<T>(ApplyDefaults(request), cancellationToken);

        public IAsyncEnumerable<string> StreamTextAsync(LlmRequest request, System.Threading.CancellationToken cancellationToken = default)
            => _inner.StreamTextAsync(ApplyDefaults(request), cancellationToken);
    }
}
