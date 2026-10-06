using System;
using System.IO;
using System.Text.RegularExpressions;

namespace DocumentRagSystem.Core.Services;

public static class ArticleDocumentNameParser
{
    private static readonly Regex ArtDocRegex = new(
        @"^Art_(?<article>.+?)\s*[-_]\s*Doc_(?<doc>.+)$", 
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DataSheetPrefixRegex = new(
        @"^Data_sheet_[A-Za-z0-9]+[_\s\-]+", 
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LeadingArticleNumberRegex = new(
        @"^(\d{7,12})[-_](.+)$", 
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a filename following the Art_&lt;articleid&gt;-Doc_&lt;documentid&gt;.pdf pattern.
    /// Also supports fallback &lt;articleid&gt;-&lt;documentid&gt; and domain datasheet conventions.
    /// </summary>
    public static (string? ArticleId, string? DocumentId) Parse(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return (null, null);

        var rawName = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (string.IsNullOrWhiteSpace(rawName))
            return (null, null);

        // 1. Primary Pattern: "Art_<articleid>-Doc_<documentid>"
        var artDocMatch = ArtDocRegex.Match(rawName);
        if (artDocMatch.Success)
        {
            var art = artDocMatch.Groups["article"].Value.Trim();
            var doc = artDocMatch.Groups["doc"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(art) && !string.IsNullOrWhiteSpace(doc))
            {
                return (art, doc);
            }
        }

        // 2. If prefixed with "Data_sheet_XX_-_" or similar, strip prefix first
        var matchPrefix = DataSheetPrefixRegex.Match(rawName);
        if (matchPrefix.Success)
        {
            rawName = rawName.Substring(matchPrefix.Length).TrimStart(' ', '-', '_');
        }

        // 3. If it starts with a 7-12 digit article number (e.g., 9293512011_... or 8300100799-...)
        var leadingMatch = LeadingArticleNumberRegex.Match(rawName);
        if (leadingMatch.Success)
        {
            var art = leadingMatch.Groups[1].Value.Trim();
            var doc = leadingMatch.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(art) && !string.IsNullOrWhiteSpace(doc))
            {
                return (art, doc);
            }
        }

        // 4. Direct split on '-' (fallback <articleid>-<documentid> pattern)
        var dashIdx = rawName.IndexOf('-');
        if (dashIdx > 0 && dashIdx < rawName.Length - 1)
        {
            var articlePart = rawName.Substring(0, dashIdx).Trim();
            var docPart = rawName.Substring(dashIdx + 1).Trim();

            if (!string.IsNullOrWhiteSpace(articlePart) && !string.IsNullOrWhiteSpace(docPart))
            {
                return (articlePart, docPart);
            }
        }

        return (null, null);
    }
}
