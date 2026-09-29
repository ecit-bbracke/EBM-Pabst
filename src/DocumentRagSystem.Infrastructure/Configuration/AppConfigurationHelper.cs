using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Configuration;

/// <summary>
/// Centralized configuration and user-secrets resolution helper for runtime services,
/// evaluation runners, and automated test suites.
/// </summary>
public static class AppConfigurationHelper
{
    public const string WebApiUserSecretsId = "dd5e23de-ff7b-4ff3-8ddf-06d7dde5c43b";
    public const string WorkerUserSecretsId = "dotnet-DocumentRagSystem.Worker-b60e9740-9afb-4db0-a548-993bc54c506a";

    private static readonly string[] KnownUserSecretsIds = new[]
    {
        WebApiUserSecretsId,
        WorkerUserSecretsId
    };

    /// <summary>
    /// Builds an IConfiguration instance loaded with appsettings.json, environment variables,
    /// and user secrets from known project IDs as well as any calling assembly.
    /// </summary>
    public static IConfiguration BuildConfiguration(Assembly? callingAssembly = null)
    {
        var builder = new ConfigurationBuilder();

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir))
        {
            builder.SetBasePath(baseDir);
            builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            builder.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
        }

        if (callingAssembly != null)
        {
            builder.AddUserSecrets(callingAssembly, optional: true);
        }

        foreach (var secretsId in KnownUserSecretsIds)
        {
            builder.AddUserSecrets(secretsId);
        }

        builder.AddEnvironmentVariables();

        return builder.Build();
    }

    /// <summary>
    /// Resolves the Gemini API key from environment variables, .NET user secrets (WebApi / Worker),
    /// appsettings configuration, or direct disk lookup.
    /// </summary>
    public static string? GetGeminiApiKey(IConfiguration? configuration = null)
    {
        // 1. Check environment variables
        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("Gemini__ApiKey")
            ?? Environment.GetEnvironmentVariable("Gemini:ApiKey");

        if (IsValidApiKey(envKey))
        {
            return envKey;
        }

        // 2. Check provided or built configuration (including UserSecrets)
        var config = configuration ?? BuildConfiguration();
        var configKey = config["GEMINI_API_KEY"]
            ?? config["Gemini:ApiKey"]
            ?? config["Gemini__ApiKey"];

        if (IsValidApiKey(configKey))
        {
            return configKey;
        }

        // 3. Direct disk fallback for known User Secrets stores
        foreach (var secretsId in KnownUserSecretsIds)
        {
            var diskKey = TryReadSecretFromDisk(secretsId, "Gemini:ApiKey")
                ?? TryReadSecretFromDisk(secretsId, "GEMINI_API_KEY");

            if (IsValidApiKey(diskKey))
            {
                return diskKey;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves LlmOptions populated with configuration and the resolved Gemini API key.
    /// </summary>
    public static LlmOptions GetLlmOptions(IConfiguration? configuration = null)
    {
        var config = configuration ?? BuildConfiguration();
        var options = new LlmOptions();
        config.GetSection(LlmOptions.SectionName).Bind(options);

        var resolvedKey = GetGeminiApiKey(config);
        if (IsValidApiKey(resolvedKey) && !IsValidApiKey(options.Gemini.ApiKey))
        {
            options.Gemini.ApiKey = resolvedKey;
        }

        return options;
    }

    /// <summary>
    /// Returns a safely masked version of the API key for logging (e.g. "AQ.Ab8...yQ").
    /// </summary>
    public static string MaskApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "(none)";
        if (apiKey.Length <= 8) return "***";
        return $"{apiKey.Substring(0, 6)}...{apiKey.Substring(apiKey.Length - 4)}";
    }

    private static bool IsValidApiKey(string? key)
    {
        return !string.IsNullOrWhiteSpace(key)
            && !string.Equals(key, "YOUR_GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(key, "your_gemini_api_key_here", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadSecretFromDisk(string userSecretsId, string keyName)
    {
        try
        {
            string secretsPath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                secretsPath = Path.Combine(appData, "Microsoft", "UserSecrets", userSecretsId, "secrets.json");
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                secretsPath = Path.Combine(home, ".microsoft", "usersecrets", userSecretsId, "secrets.json");
            }

            if (File.Exists(secretsPath))
            {
                var json = File.ReadAllText(secretsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(keyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString();
                }

                if (keyName.Contains(':'))
                {
                    var parts = keyName.Split(':');
                    if (doc.RootElement.TryGetProperty(parts[0], out var section) &&
                        section.ValueKind == JsonValueKind.Object &&
                        section.TryGetProperty(parts[1], out var nestedProp) &&
                        nestedProp.ValueKind == JsonValueKind.String)
                    {
                        return nestedProp.GetString();
                    }
                }
            }
        }
        catch
        {
            // Ignore disk read errors
        }

        return null;
    }
}
