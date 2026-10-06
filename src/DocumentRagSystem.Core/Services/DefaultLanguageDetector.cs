using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Core.Services;

public class DefaultLanguageDetector : ILanguageDetector
{
    private readonly ILlmClient? _llmClient;
    private readonly ILogger<DefaultLanguageDetector>? _logger;

    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "be", "to", "of", "and", "a", "in", "that", "have", "it",
        "for", "not", "on", "with", "as", "you", "do", "at", "this", "but",
        "by", "from", "they", "we", "or", "an", "will", "my", "one", "all",
        "would", "there", "their", "what", "so", "up", "out", "if", "about", "who",
        "which", "when", "can", "more", "also", "after", "use", "two", "how", "our",
        "work", "first", "well", "way", "even", "new", "any", "these", "give", "day",
        "most", "us", "specifications", "connection", "technical", "operating", "nominal",
        "ambient", "performance", "maximum", "minimum", "speed", "airflow", "voltage",
        "current", "power", "direction", "drawing", "dimensions", "protection", "approval",
        "motor", "fan", "leads", "wire", "description", "weight", "mass", "measured",
        "supply", "phase", "reference", "temperature", "degree", "frequency", "output",
        "input", "control", "curve", "characteristic", "features", "housing", "impeller",
        "bearing", "insulation", "class", "mounting", "shaft", "position", "rotor", "topic"
    };

    private static readonly HashSet<string> DanishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "og", "i", "jeg", "det", "at", "en", "den", "til", "er", "som",
        "på", "de", "med", "han", "af", "for", "ikke", "der", "var", "mig",
        "sig", "men", "et", "har", "om", "vi", "min", "havde", "ham", "hun",
        "nu", "over", "da", "fra", "du", "ud", "sin", "dem", "os", "op",
        "man", "hans", "hvor", "eller", "hvad", "skal", "selv", "her", "alle", "vil",
        "blev", "kunne", "ind", "når", "være", "dog", "noget", "ville", "jo", "derfra",
        "blæser", "ventilator", "spænding", "strøm", "teknisk", "tekniske", "data",
        "driftsdata", "tilslutning", "omdrejningstal", "luftstrøm", "omgivelsestemperatur",
        "godkendelse", "målinger", "kabel", "leder", "ledning", "montage", "mærkespænding",
        "leje", "dimensioner", "motoreffekt", "lydniveau", "tilladt", "mærkedata", "tegning",
        "aksel", "positionsretning", "tilslutningsdiagram", "styring", "indgang", "udgang",
        "fejl", "beskyttelse", "køling", "højde", "vægt", "dette", "denne", "disse"
    };

    private static readonly HashSet<string> GermanWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "des", "dem", "den", "ein", "eine", "einer", "eines",
        "einem", "einen", "und", "in", "zu", "von", "mit", "für", "auf", "ist",
        "im", "nicht", "als", "auch", "es", "an", "werden", "aus", "er", "hat",
        "dass", "sie", "nach", "wird", "bei", "um", "am", "sind", "noch", "wie",
        "über", "so", "zum", "war", "haben", "nur", "oder", "aber", "vor", "zur",
        "bis", "mehr", "durch", "man", "sein", "wurde", "kann", "wenn", "zwischen", "unter",
        "ohne", "lüfter", "ventilator", "drehzahl", "betriebsdaten", "spannung", "strom",
        "nennspannung", "zulässige", "anschluss", "leistung", "fördervolumen", "motorschutz",
        "drehrichtung", "schutzart", "zeichnung", "lagerung", "abmessungen", "einbau",
        "kennlinie", "merkmale", "eigenschaften", "eingang", "ausgang"
    };

    public DefaultLanguageDetector(ILogger<DefaultLanguageDetector>? logger = null)
        : this((ILlmClient?)null, logger)
    {
    }

    public DefaultLanguageDetector(ILlmClientResolver? resolver, ILogger<DefaultLanguageDetector>? logger = null)
        : this(resolver?.Resolve(LlmPurpose.InputGovernor), logger)
    {
    }

    public DefaultLanguageDetector(ILlmClient? llmClient, ILogger<DefaultLanguageDetector>? logger = null)
    {
        _llmClient = llmClient;
        _logger = logger;
    }

    public async Task<LanguageDetectionResult> DetectLanguageAsync(string text, string? fileName = null)
    {
        var heuristicResult = DetectLanguageHeuristic(text, fileName);

        // If heuristic is very confident or no LLM is configured, return heuristic result immediately
        if (heuristicResult.Confidence >= 0.75 || _llmClient == null || string.IsNullOrWhiteSpace(text))
        {
            _logger?.LogInformation(
                "[LanguageDetector] Heuristic detected language '{Language}' (IsEnglish: {IsEnglish}, Confidence: {Confidence:P1}) for file '{FileName}'.",
                heuristicResult.Language, heuristicResult.IsEnglish, heuristicResult.Confidence, fileName ?? "N/A");
            return heuristicResult;
        }

        // LLM fallback for ambiguous text
        try
        {
            var snippet = text.Length > 1000 ? text.Substring(0, 1000) : text;
            var prompt = $$"""
Determine the primary language of the following technical document text.
Return ONLY valid JSON matching this schema:
{
  "language": "en" | "da" | "de" | "other",
  "is_english": true | false,
  "confidence": 0.95,
  "explanation": "brief reason"
}

Document snippet:
{{snippet}}
""";

            var req = LlmRequest.FromPrompt(prompt, requireJson: true, temperature: 0.0, maxOutputTokens: 128);
            var res = await _llmClient.GenerateStructuredAsync<LlmLanguageResponse>(req);

            if (res?.Value != null && !string.IsNullOrWhiteSpace(res.Value.Language))
            {
                var lang = res.Value.Language.Trim().ToLowerInvariant();
                var isEng = res.Value.IsEnglish || lang == "en" || lang == "english";
                _logger?.LogInformation(
                    "[LanguageDetector] LLM detected language '{Language}' (IsEnglish: {IsEnglish}, Confidence: {Confidence:P1}) for file '{FileName}'.",
                    lang, isEng, res.Value.Confidence, fileName ?? "N/A");

                return new LanguageDetectionResult(
                    Language: isEng ? "en" : lang,
                    IsEnglish: isEng,
                    Confidence: Math.Clamp(res.Value.Confidence, 0.5, 1.0),
                    Explanation: res.Value.Explanation ?? "Classified by LLM."
                );
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[LanguageDetector] LLM language detection fallback failed. Using heuristic result.");
        }

        return heuristicResult;
    }

    private static LanguageDetectionResult DetectLanguageHeuristic(string text, string? fileName)
    {
        double enScore = 0;
        double daScore = 0;
        double deScore = 0;

        // 1. Filename hints
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fn = fileName.ToUpperInvariant();
            if (fn.Contains("_DA_") || fn.Contains("DATA_SHEET_DA") || fn.Contains("-DA-") || fn.Contains("_DA."))
            {
                daScore += 15.0;
            }
            if (fn.Contains("_EN_") || fn.Contains("DATA_SHEET_US") || fn.Contains("-EN-") || fn.Contains("_EN.") ||
                fn.Contains("_US_") || fn.Contains("-US-") || fn.Contains("_US.") || fn.Contains("_PDB_EN"))
            {
                enScore += 15.0;
            }
            if (fn.Contains("_DE_") || fn.Contains("DATA_SHEET_DE") || fn.Contains("-DE-") || fn.Contains("_DE.") || fn.Contains("_PDB_DE"))
            {
                deScore += 15.0;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            if (enScore > daScore && enScore > deScore) return new LanguageDetectionResult("en", true, 0.8, "Filename indicator");
            if (daScore > enScore && daScore > deScore) return new LanguageDetectionResult("da", false, 0.8, "Filename indicator");
            if (deScore > enScore && deScore > daScore) return new LanguageDetectionResult("de", false, 0.8, "Filename indicator");
            return new LanguageDetectionResult("unknown", false, 0.0, "Empty text");
        }

        // 2. Character-level markers
        foreach (char c in text)
        {
            if (c is 'æ' or 'ø' or 'å' or 'Æ' or 'Ø' or 'Å')
            {
                daScore += 4.0;
            }
            else if (c is 'ä' or 'ö' or 'ü' or 'ß' or 'Ä' or 'Ö' or 'Ü')
            {
                deScore += 4.0;
            }
        }

        // 3. Word-level markers
        var matches = Regex.Matches(text, @"\b[a-zA-ZæøåÆØÅäöüßÄÖÜ]{2,}\b");
        int totalTokens = 0;

        foreach (Match match in matches)
        {
            var word = match.Value;
            totalTokens++;

            if (EnglishWords.Contains(word))
            {
                enScore += 1.0;
            }
            if (DanishWords.Contains(word))
            {
                daScore += 1.0;
            }
            if (GermanWords.Contains(word))
            {
                deScore += 1.0;
            }
        }

        double totalScore = enScore + daScore + deScore;
        if (totalScore <= 0.0)
        {
            // If nothing matched, default to English if English filename hints or neutral
            return new LanguageDetectionResult("en", true, 0.5, "Default assumption");
        }

        if (enScore >= daScore && enScore >= deScore)
        {
            double confidence = enScore / totalScore;
            return new LanguageDetectionResult("en", true, confidence, $"Statistical analysis (EN score: {enScore:F1})");
        }
        else if (daScore > enScore && daScore >= deScore)
        {
            double confidence = daScore / totalScore;
            return new LanguageDetectionResult("da", false, confidence, $"Statistical analysis (DA score: {daScore:F1})");
        }
        else
        {
            double confidence = deScore / totalScore;
            return new LanguageDetectionResult("de", false, confidence, $"Statistical analysis (DE score: {deScore:F1})");
        }
    }

    private sealed record LlmLanguageResponse(
        string? Language,
        bool IsEnglish,
        double Confidence,
        string? Explanation
    );
}
