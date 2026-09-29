using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.HealthChecks;

/// <summary>
/// Health check for the local LLM inference server.
/// Performs a lightweight probe (GET /v1/models) to verify reachability, responsiveness, and model availability.
/// </summary>
public sealed class LocalLlmHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalLlmOptions _options;
    private readonly ILogger<LocalLlmHealthCheck>? _logger;

    public LocalLlmHealthCheck(
        IOptions<LlmOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<LocalLlmHealthCheck>? logger = null)
    {
        _options = options?.Value.Local ?? new LocalLlmOptions();
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger;
    }

    public LocalLlmHealthCheck(
        LocalLlmOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<LocalLlmHealthCheck>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return HealthCheckResult.Degraded("Local LLM BaseUrl is not configured.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient("LocalLlmHealthClient");
            if (client.BaseAddress == null)
            {
                var rawUrl = _options.BaseUrl.Trim();
                if (rawUrl.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    rawUrl = rawUrl.Substring(0, rawUrl.Length - "/v1/chat/completions".Length);
                }
                client.BaseAddress = new Uri(rawUrl.TrimEnd('/') + "/");
            }

            var path = string.IsNullOrWhiteSpace(_options.HealthPath) ? "v1/models" : _options.HealthPath.TrimStart('/');
            using var request = new HttpRequestMessage(HttpMethod.Get, path);

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(_options.TimeoutSeconds, 5)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var response = await client.SendAsync(request, linkedCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Unhealthy(
                    $"Local LLM endpoint '{_options.BaseUrl}{path}' returned HTTP {(int)response.StatusCode}.");
            }

            // Optionally parse model list to check model presence
            try
            {
                var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: linkedCts.Token);
                if (json != null && json.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                {
                    var models = dataProp.EnumerateArray()
                        .Select(m => m.TryGetProperty("id", out var idProp) ? idProp.GetString() : null)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToList();

                    var targetModel = _options.Model;
                    bool modelFound = string.IsNullOrWhiteSpace(targetModel) ||
                                     models.Any(m => string.Equals(m, targetModel, StringComparison.OrdinalIgnoreCase));

                    var data = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["baseUrl"] = _options.BaseUrl,
                        ["configuredModel"] = targetModel,
                        ["availableModels"] = models,
                        ["modelFound"] = modelFound
                    };

                    if (!modelFound && models.Count > 0)
                    {
                        return HealthCheckResult.Degraded(
                            $"Local LLM endpoint responsive, but model '{targetModel}' was not found in [{string.Join(", ", models)}].",
                            data: data);
                    }

                    return HealthCheckResult.Healthy(
                        $"Local LLM responsive at {_options.BaseUrl}. Model: {targetModel}",
                        data: data);
                }
            }
            catch
            {
                // If model list json format differs, 2xx HTTP response still confirms endpoint reachability
            }

            return HealthCheckResult.Healthy($"Local LLM responsive at {_options.BaseUrl}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Local LLM health probe timed out for {_options.BaseUrl}.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Local LLM health check failed for {BaseUrl}: {Message}", _options.BaseUrl, ex.Message);
            return HealthCheckResult.Unhealthy($"Local LLM health check failed for {_options.BaseUrl}: {ex.Message}", ex);
        }
    }
}
