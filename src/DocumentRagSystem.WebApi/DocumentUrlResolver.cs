using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DocumentRagSystem.WebApi;

public static class DocumentUrlResolver
{
    public static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return string.Empty;
    }

    public static string ToDocumentUrl(string? filePath, string? fileName, IEnumerable<string> uploadDirectories)
    {
        if (string.IsNullOrWhiteSpace(filePath) && string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(filePath) &&
            Uri.TryCreate(filePath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return filePath;
        }

        var rawPath = filePath ?? string.Empty;
        var unescapedPath = Uri.UnescapeDataString(rawPath);
        var normalizedFileName = Path.GetFileName(unescapedPath);
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            var unescapedName = Uri.UnescapeDataString(fileName ?? string.Empty);
            normalizedFileName = Path.GetFileName(unescapedName);
        }

        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            return string.Empty;
        }

        return ToExistingUploadUrl(normalizedFileName, uploadDirectories);
    }

    public static string ToExistingUploadUrl(string targetFileName, IEnumerable<string> uploadDirectories)
    {
        if (string.IsNullOrWhiteSpace(targetFileName)) return string.Empty;

        var unescaped = Uri.UnescapeDataString(targetFileName);
        var cleanName = Path.GetFileName(unescaped);
        foreach (var dir in uploadDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            var direct = Path.Combine(dir, cleanName);
            if (File.Exists(direct))
            {
                return $"/uploads/{Uri.EscapeDataString(cleanName)}";
            }

            var suffixMatch = ResolveUploadSuffixMatch(dir, cleanName);
            if (!string.IsNullOrWhiteSpace(suffixMatch))
            {
                return $"/uploads/{Uri.EscapeDataString(suffixMatch)}";
            }
        }

        return string.Empty;
    }

    public static string? ResolveUploadSuffixMatch(string directory, string targetFileName)
    {
        if (string.IsNullOrWhiteSpace(targetFileName) || !Directory.Exists(directory))
        {
            return null;
        }

        if (Program.DirFileCache.TryGetValue(directory, out var cached) &&
            DateTime.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(30))
        {
            var cachedSuffix = $"_{targetFileName}";
            var cachedMatch = cached.Files.FirstOrDefault(f =>
                string.Equals(f, targetFileName, StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(cachedSuffix, StringComparison.OrdinalIgnoreCase));

            if (cachedMatch != null)
            {
                return cachedMatch;
            }
        }

        try
        {
            var allFiles = Directory.GetFiles(directory)
                .Select(Path.GetFileName)
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(f => f!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Program.DirFileCache[directory] = (DateTime.UtcNow, allFiles);

            var suffix = $"_{targetFileName}";
            var match = allFiles.FirstOrDefault(f =>
                string.Equals(f, targetFileName, StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            return match;
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolvePhysicalFilePath(string? filePath, string? fileName, IEnumerable<string> uploadDirectories)
    {
        if (string.IsNullOrWhiteSpace(filePath) && string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            return filePath;
        }

        var rawPath = filePath ?? string.Empty;
        var unescapedPath = Uri.UnescapeDataString(rawPath);
        var normalizedFileName = Path.GetFileName(unescapedPath);
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            var unescapedName = Uri.UnescapeDataString(fileName ?? string.Empty);
            normalizedFileName = Path.GetFileName(unescapedName);
        }

        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            return null;
        }

        foreach (var dir in uploadDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            var direct = Path.Combine(dir, normalizedFileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            var suffixMatch = ResolveUploadSuffixMatch(dir, normalizedFileName);
            if (!string.IsNullOrWhiteSpace(suffixMatch))
            {
                var suffixPath = Path.Combine(dir, suffixMatch);
                if (File.Exists(suffixPath))
                {
                    return suffixPath;
                }
            }
        }

        return null;
    }
}
