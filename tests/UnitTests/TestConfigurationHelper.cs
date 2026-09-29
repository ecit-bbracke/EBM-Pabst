using System;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Configuration;

namespace DocumentRagSystem.UnitTests;

/// <summary>
/// Test suite helper to easily access configuration, User Secrets, and Gemini API keys.
/// </summary>
public static class TestConfigurationHelper
{
    private static readonly Lazy<IConfiguration> _configLazy = new(() =>
        AppConfigurationHelper.BuildConfiguration(Assembly.GetExecutingAssembly()));

    public static IConfiguration Configuration => _configLazy.Value;

    /// <summary>
    /// Gets the Gemini API key from User Secrets, environment variables, or appsettings.
    /// </summary>
    public static string? GetGeminiApiKey() => AppConfigurationHelper.GetGeminiApiKey(Configuration);

    /// <summary>
    /// Gets fully populated LlmOptions with Gemini API key injected if available.
    /// </summary>
    public static LlmOptions GetLlmOptions() => AppConfigurationHelper.GetLlmOptions(Configuration);
}
