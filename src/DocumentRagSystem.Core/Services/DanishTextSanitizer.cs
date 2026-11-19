using System;
using System.Text.RegularExpressions;

namespace DocumentRagSystem.Core.Services;

/// <summary>
/// Sanitizer that eliminates cross-lingual Scandinavian leakage (Norwegian/Swedish words and
/// Swedish umlauts like 'ö' and 'ä') in Danish generated responses.
/// </summary>
public static class DanishTextSanitizer
{
    private static readonly (Regex Pattern, string Replacement)[] Replacements = new[]
    {
        // 1. Swedish "översikt / översigt" -> Danish "oversigt" (starts with 'o', NOT 'ø')
        (new Regex(@"(?i)\bTilslutnings[öø]versig[tt]\b", RegexOptions.Compiled), "Tilslutningsoversigt"),
        (new Regex(@"(?i)\b[öø]versig[tt]\b", RegexOptions.Compiled), "oversigt"),
        (new Regex(@"(?i)\b[öø]versigter\b", RegexOptions.Compiled), "oversigter"),

        // English spelling leakage: Candidat -> Kandidat
        (new Regex(@"\bCandidat([a-zæøå]*)\b", RegexOptions.Compiled), "Kandidat$1"),
        (new Regex(@"\bcandidat([a-zæøå]*)\b", RegexOptions.Compiled), "kandidat$1"),

        // 2. Specific compound words and phrases
        (new Regex(@"(?i)\bviftediameter\b", RegexOptions.Compiled), "Impellerdiameter"),
        (new Regex(@"(?i)\b(?:over\s+rotoren\/visere)\b", RegexOptions.Compiled), "over rotoren/monteringsstivere"),
        (new Regex(@"(?i)\b(?:over\s+visere)\b", RegexOptions.Compiled), "over monteringsstivere"),
        (new Regex(@"(?i)\bvisere\s+og\s+vinger\b", RegexOptions.Compiled), "stivere og vinger"),
        (new Regex(@"(?i)\b(?:seglformede\s+)klinger\b", RegexOptions.Compiled), "seglformede vinger"),
        (new Regex(@"(?i)\b(?:HyBlade\s+)klinger\b", RegexOptions.Compiled), "HyBlade vinger"),
        (new Regex(@"(?i)\bblæserklinger\b", RegexOptions.Compiled), "blæservinger"),
        (new Regex(@"(?i)\bventilatorklinger\b", RegexOptions.Compiled), "ventilatorvinger"),
        (new Regex(@"(?i)\bklemrekasse[r]?\b", RegexOptions.Compiled), "klemkasse"),
        (new Regex(@"(?i)\bklemrekke[r]?\b", RegexOptions.Compiled), "klemrække"),
        (new Regex(@"(?i)\bkompaktvifte\b", RegexOptions.Compiled), "kompaktventilator"),
        (new Regex(@"(?i)\bkompaktvifter\b", RegexOptions.Compiled), "kompaktventilatorer"),

        // 3. General Norwegian "vifte" words
        (new Regex(@"(?i)\bVifter\b", RegexOptions.Compiled), "Ventilatorer"),
        (new Regex(@"(?i)\bvifter\b", RegexOptions.Compiled), "ventilatorer"),
        (new Regex(@"(?i)\bVifte\b", RegexOptions.Compiled), "Ventilator"),
        (new Regex(@"(?i)\bvifte\b", RegexOptions.Compiled), "ventilator")
    };

    /// <summary>
    /// Sanitizes generated text by replacing Scandinavian leakage and Swedish umlauts
    /// with authentic Danish terminology.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var result = text;

        foreach (var (pattern, replacement) in Replacements)
        {
            result = pattern.Replace(result, replacement);
        }

        // In Danish text, Swedish umlauts 'ö' and 'ä' are replaced by 'ø' and 'æ'
        result = result
            .Replace('ö', 'ø')
            .Replace('Ö', 'Ø')
            .Replace('ä', 'æ')
            .Replace('Ä', 'Æ');

        return result;
    }
}
