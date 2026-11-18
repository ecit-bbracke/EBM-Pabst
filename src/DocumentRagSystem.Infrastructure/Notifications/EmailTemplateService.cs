using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Infrastructure.Notifications;

/// <summary>
/// Production-grade email template service that renders HTML templates from disk
/// with support for token substitutions and robust defensive fallbacks.
/// </summary>
public sealed class EmailTemplateService : IEmailTemplateService
{
    private readonly string? _customTemplatesPath;
    private readonly ILogger<EmailTemplateService>? _logger;
    private static readonly ConcurrentDictionary<string, (string Content, DateTime LastModified)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public EmailTemplateService(
        string? customTemplatesPath = null,
        ILogger<EmailTemplateService>? logger = null)
    {
        _customTemplatesPath = customTemplatesPath;
        _logger = logger;
    }

    public async Task<string> RenderTemplateAsync(
        string templateName,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        parameters ??= new Dictionary<string, string?>();

        var rawTemplate = await LoadTemplateStringAsync(templateName, ct);

        // Replace placeholders formatted as {{Key}}
        // Regex matches {{Key}} or {{{Key}}}
        var result = Regex.Replace(rawTemplate, @"\{\{\{?([a-zA-Z0-9_\-]+)\}?\}\}", match =>
        {
            var key = match.Groups[1].Value;
            if (parameters.TryGetValue(key, out var val))
            {
                if (val is null) return string.Empty;

                // Tripled curly braces {{{Key}}} denote unencoded raw insertion
                if (match.Value.StartsWith("{{{", StringComparison.Ordinal) && match.Value.EndsWith("}}}", StringComparison.Ordinal))
                {
                    return val;
                }

                return WebUtility.HtmlEncode(val);
            }

            return match.Value; // Leave untouched if not supplied
        });

        return result;
    }

    private async Task<string> LoadTemplateStringAsync(string templateName, CancellationToken ct)
    {
        var cleanName = templateName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? templateName
            : $"{templateName}.html";

        var filePath = ResolveTemplateFilePath(cleanName);

        if (filePath != null && File.Exists(filePath))
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (Cache.TryGetValue(filePath, out var cached) && cached.LastModified >= fileInfo.LastWriteTimeUtc)
                {
                    return cached.Content;
                }

                var content = await File.ReadAllTextAsync(filePath, ct);
                Cache[filePath] = (content, fileInfo.LastWriteTimeUtc);
                _logger?.LogDebug("[EmailTemplateService] Loaded template '{TemplateName}' from {FilePath}", templateName, filePath);
                return content;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[EmailTemplateService] Could not read template file {FilePath}. Using built-in fallback.", filePath);
            }
        }
        else
        {
            _logger?.LogWarning("[EmailTemplateService] Template file '{TemplateName}' not found on disk. Using built-in fallback.", cleanName);
        }

        return GetFallbackTemplate(templateName);
    }

    private string? ResolveTemplateFilePath(string fileName)
    {
        var candidatePaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(_customTemplatesPath))
        {
            candidatePaths.Add(Path.Combine(_customTemplatesPath, fileName));
        }

        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "Templates", "Emails", fileName));
        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, fileName));
        candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "Templates", "Emails", fileName));
        candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "src", "DocumentRagSystem.WebApi", "Templates", "Emails", fileName));

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string GetFallbackTemplate(string templateName)
    {
        var key = templateName.ToLowerInvariant();
        if (key.Contains("welcome"))
        {
            return """
                <!DOCTYPE html>
                <html lang="da">
                <body>
                  <h2>Velkommen til ebm-papst Portalen</h2>
                  <p>Hej,</p>
                  <p>Brugernavn: {{Email}}<br/>Midlertidig adgangskode: {{Password}}<br/>Login URL: <a href="{{LoginUrl}}">{{LoginUrl}}</a></p>
                </body>
                </html>
                """;
        }

        if (key.Contains("reset"))
        {
            return """
                <!DOCTYPE html>
                <html lang="da">
                <body>
                  <h2>Nulstilling af adgangskode</h2>
                  <p>Hej,</p>
                  <p>Brugernavn: {{Email}}<br/>Ny adgangskode: {{NewPassword}}<br/>Login URL: <a href="{{LoginUrl}}">{{LoginUrl}}</a></p>
                </body>
                </html>
                """;
        }

        return """
            <!DOCTYPE html>
            <html lang="da">
            <body>
              <h2>ebm-papst Portal Feedback</h2>
              <p>Tidspunkt: {{CreatedAt}}<br/>Bruger: {{UserEmail}} (ID: {{UserId}})<br/>Kommentar: {{Comment}}<br/>Prompt: {{SearchPrompts}}</p>
            </body>
            </html>
            """;
    }
}
