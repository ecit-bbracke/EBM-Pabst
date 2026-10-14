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
        "og", "jeg", "det", "til", "som", "på", "med", "han", "ikke", "der",
        "mig", "sig", "men", "har", "om", "vi", "havde", "ham", "hun",
        "fra", "dem", "hvor", "eller", "hvad", "skal", "selv", "alle",
        "blev", "kunne", "ind", "når", "være", "dog", "noget", "ville", "jo",
        "blæser", "ventilator", "spænding", "strøm", "tekniske",
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
        "kennlinie", "merkmale", "eigenschaften", "eingang", "ausgang", "daten", "technische",
        "kabel", "gewicht", "durchmesser", "temperatur", "volumenstrom", "druck"
    };

    private static readonly HashSet<string> SpanishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "el", "la", "los", "las", "un", "una", "unos", "unas", "del", "por",
        "para", "con", "sin", "sobre", "entre", "hasta", "desde", "como", "pero", "mas",
        "este", "esta", "estos", "estas", "datos", "tecnicos", "ventilador", "tension",
        "corriente", "potencia", "velocidad", "flujo", "presion", "temperatura", "dimensiones",
        "conexion", "motor", "frecuencia", "peso", "dibujo", "caracteristicas", "curva",
        "aire", "caudal", "montaje", "sentido", "giro", "esquema", "borne", "aislamiento",
        "grado", "proteccion", "homologacion", "cable", "salida", "entrada", "medido"
    };

    private static readonly HashSet<string> FrenchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "le", "la", "les", "un", "une", "des", "du", "dans", "pour", "sur",
        "avec", "sans", "sous", "par", "est", "sont", "ce", "cette", "ces", "donnees",
        "techniques", "ventilateur", "tension", "courant", "puissance", "vitesse", "debit",
        "pression", "temperature", "raccordement", "moteur", "frequence", "masse", "poids",
        "dessin", "caracteristiques", "courbe", "sens", "rotation", "schema", "protection",
        "homologation", "classe", "isolation", "air", "entree", "sortie"
    };

    private static readonly HashSet<string> ItalianWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "il", "lo", "la", "i", "gli", "le", "un", "uno", "una", "del",
        "dello", "della", "dei", "degli", "delle", "nel", "nella", "con", "per", "tra",
        "fra", "dati", "tecnici", "ventilatore", "tensione", "corrente", "potenza", "velocita",
        "portata", "pressione", "temperatura", "dimensioni", "collegamento", "motore", "frequenza",
        "peso", "disegno", "caratteristiche", "curva", "senso", "rotazione", "schema", "protezione"
    };

    private static readonly HashSet<string> PortugueseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "o", "a", "os", "as", "um", "uma", "uns", "umas", "do", "da",
        "dos", "das", "no", "na", "nos", "nas", "por", "para", "com", "sem",
        "dados", "tecnicos", "ventilador", "tensao", "corrente", "potencia", "velocidade",
        "fluxo", "pressao", "temperatura", "dimensoes", "ligacao", "motor", "frequencia",
        "peso", "desenho", "caracteristicas", "curva", "sentido", "rotacao", "esquema", "protecao"
    };

    private static readonly HashSet<string> DutchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "het", "een", "van", "naar", "als", "voor", "niet", "met", "aan",
        "zijn", "dat", "om", "hebben", "door", "over", "worden", "zich", "bij",
        "technische", "gegevens", "ventilator", "spanning", "stroom", "vermogen", "toerental",
        "luchtdebiet", "luchthoeveelheid", "druk", "temperatuur", "afmetingen", "aansluiting",
        "motor", "frequentie", "gewicht", "tekening", "draairichting", "bescherming"
    };

    private static readonly HashSet<string> SwedishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "och", "att", "som", "en", "av", "till", "den", "inte", "ett",
        "om", "har", "var", "sig", "skall", "eller", "eller", "under", "också",
        "tekniska", "fläkt", "ventilator", "spänning", "ström", "effekt", "varvtal",
        "luftflöde", "tryck", "temperatur", "mått", "dimensioner", "anslutning", "motor",
        "frekvens", "vikt", "ritning", "rotationsriktning", "skyddsklass"
    };

    private static readonly HashSet<string> NorwegianWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "og", "at", "som", "til", "på", "ikke", "en", "av", "om", "men",
        "ble", "seg", "etter", "eller", "ved", "skal", "selv", "under", "vifte",
        "tekniske", "spenning", "strøm", "effekt", "turtall", "luftmengde", "trykk",
        "temperatur", "dimensjoner", "tilkobling", "motor", "frekvens", "vekt", "tegning",
        "rotasjonsretning", "kapslingsgrad"
    };

    private static readonly HashSet<string> PolishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "nie", "się", "jest", "na", "do", "że", "to", "jak", "dla",
        "od", "po", "przez", "dane", "techniczne", "wentylator", "napięcie", "prąd",
        "moc", "prędkość", "obrotowa", "przepływ", "ciśnienie", "temperatura", "wymiary",
        "podłączenie", "silnik", "częstotliwość", "masa", "rysunek", "kierunek", "obrotów"
    };

    private static readonly HashSet<string> CzechWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "se", "na", "že", "do", "pro", "je", "jako", "po", "při", "jsou",
        "technická", "data", "ventilátor", "napětí", "proud", "výkon", "otáčky",
        "průtok", "vzduchu", "tlak", "teplota", "rozměry", "připojení", "motor",
        "frekvence", "hmotnost", "výkres", "směr", "otáčení", "krytí"
    };

    private static readonly HashSet<string> FinnishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ja", "on", "ei", "että", "se", "hän", "mutta", "ovat", "kuin",
        "tekniset", "tiedot", "tuuletin", "puhallin", "jännite", "virta", "teho",
        "pyörimisnopeus", "ilmavirta", "paine", "lämpötila", "mitat", "liitäntä", "moottori",
        "taajuus", "paino", "piirustus", "pyörimissuunta", "suojausluokka"
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
        if ((heuristicResult.Confidence >= 0.80 && heuristicResult.IsEnglish) || _llmClient == null || string.IsNullOrWhiteSpace(text))
        {
            _logger?.LogInformation(
                "[LanguageDetector] Heuristic detected language '{Language}' (IsEnglish: {IsEnglish}, Confidence: {Confidence:P1}) for file '{FileName}'.",
                heuristicResult.Language, heuristicResult.IsEnglish, heuristicResult.Confidence, fileName ?? "N/A");
            return heuristicResult;
        }

        // LLM fallback for ambiguous text or non-English validation
        try
        {
            var snippet = text.Length > 1200 ? text.Substring(0, 1200) : text;
            var prompt = $$"""
Determine the primary language of the following technical document text.
Identify the ISO 639-1 two-letter language code (e.g. "en", "da", "de", "es", "fr", "it", "nl", "pt", "sv", "no", "pl", "cs", "fi", "ru", "zh", "ja", "ko", "ar", "tr", "el", etc.).
Return ONLY valid JSON matching this schema:
{
  "language": "iso_code",
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

    public static LanguageDetectionResult DetectLanguageHeuristic(string text, string? fileName)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = 0.0,
            ["da"] = 0.0,
            ["de"] = 0.0,
            ["es"] = 0.0,
            ["fr"] = 0.0,
            ["it"] = 0.0,
            ["pt"] = 0.0,
            ["nl"] = 0.0,
            ["sv"] = 0.0,
            ["no"] = 0.0,
            ["pl"] = 0.0,
            ["cs"] = 0.0,
            ["fi"] = 0.0,
            ["ru"] = 0.0,
            ["zh"] = 0.0,
            ["ja"] = 0.0,
            ["ko"] = 0.0,
            ["ar"] = 0.0
        };

        // 1. Filename hints
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fn = fileName.ToUpperInvariant();
            if (fn.Contains("_EN_") || fn.Contains("DATA_SHEET_US") || fn.Contains("-EN-") || fn.Contains("_EN.") ||
                fn.Contains("_US_") || fn.Contains("-US-") || fn.Contains("_US.") || fn.Contains("_PDB_EN"))
            {
                scores["en"] += 15.0;
            }
            if (fn.Contains("_DA_") || fn.Contains("DATA_SHEET_DA") || fn.Contains("-DA-") || fn.Contains("_DA."))
            {
                scores["da"] += 15.0;
            }
            if (fn.Contains("_DE_") || fn.Contains("DATA_SHEET_DE") || fn.Contains("-DE-") || fn.Contains("_DE.") || fn.Contains("_PDB_DE"))
            {
                scores["de"] += 15.0;
            }
            if (fn.Contains("_ES_") || fn.Contains("DATA_SHEET_ES") || fn.Contains("-ES-") || fn.Contains("_ES.") || fn.Contains("_SP_") || fn.Contains("PDB_ES"))
            {
                scores["es"] += 15.0;
            }
            if (fn.Contains("_FR_") || fn.Contains("DATA_SHEET_FR") || fn.Contains("-FR-") || fn.Contains("_FR.") || fn.Contains("PDB_FR"))
            {
                scores["fr"] += 15.0;
            }
            if (fn.Contains("_IT_") || fn.Contains("DATA_SHEET_IT") || fn.Contains("-IT-") || fn.Contains("_IT.") || fn.Contains("PDB_IT"))
            {
                scores["it"] += 15.0;
            }
            if (fn.Contains("_NL_") || fn.Contains("DATA_SHEET_NL") || fn.Contains("-NL-") || fn.Contains("_NL.") || fn.Contains("PDB_NL"))
            {
                scores["nl"] += 15.0;
            }
            if (fn.Contains("_PT_") || fn.Contains("DATA_SHEET_PT") || fn.Contains("-PT-") || fn.Contains("_PT.") || fn.Contains("PDB_PT"))
            {
                scores["pt"] += 15.0;
            }
            if (fn.Contains("_SV_") || fn.Contains("_SE_") || fn.Contains("DATA_SHEET_SE") || fn.Contains("PDB_SV"))
            {
                scores["sv"] += 15.0;
            }
            if (fn.Contains("_NO_") || fn.Contains("_NB_") || fn.Contains("DATA_SHEET_NO") || fn.Contains("PDB_NO"))
            {
                scores["no"] += 15.0;
            }
            if (fn.Contains("_PL_") || fn.Contains("DATA_SHEET_PL") || fn.Contains("PDB_PL"))
            {
                scores["pl"] += 15.0;
            }
            if (fn.Contains("_CS_") || fn.Contains("_CZ_") || fn.Contains("DATA_SHEET_CZ") || fn.Contains("PDB_CS"))
            {
                scores["cs"] += 15.0;
            }
            if (fn.Contains("_FI_") || fn.Contains("DATA_SHEET_FI") || fn.Contains("PDB_FI"))
            {
                scores["fi"] += 15.0;
            }
            if (fn.Contains("_RU_") || fn.Contains("DATA_SHEET_RU") || fn.Contains("PDB_RU"))
            {
                scores["ru"] += 15.0;
            }
            if (fn.Contains("_ZH_") || fn.Contains("_CN_") || fn.Contains("DATA_SHEET_CN") || fn.Contains("PDB_ZH"))
            {
                scores["zh"] += 15.0;
            }
            if (fn.Contains("_JA_") || fn.Contains("_JP_") || fn.Contains("DATA_SHEET_JP") || fn.Contains("PDB_JA"))
            {
                scores["ja"] += 15.0;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            var best = scores.OrderByDescending(kv => kv.Value).First();
            if (best.Value > 0)
            {
                return new LanguageDetectionResult(best.Key, best.Key == "en", 0.8, "Filename indicator");
            }
            return new LanguageDetectionResult("unknown", false, 0.0, "Empty text");
        }

        // 2. Character-level and Unicode Script Markers
        int cyrillicCount = 0;
        int cjkCount = 0;
        int hiraganaKatakanaCount = 0;
        int hangulCount = 0;
        int arabicCount = 0;

        foreach (char c in text)
        {
            // Non-Latin Scripts
            if (c >= '\u0400' && c <= '\u04FF') cyrillicCount++;
            else if (c >= '\u4E00' && c <= '\u9FFF') cjkCount++;
            else if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF')) hiraganaKatakanaCount++;
            else if (c >= '\uAC00' && c <= '\uD7AF') hangulCount++;
            else if (c >= '\u0600' && c <= '\u06FF') arabicCount++;

            // European Latin Diacritics
            if (c is 'æ' or 'ø' or 'å' or 'Æ' or 'Ø' or 'Å')
            {
                scores["da"] += 3.0;
                scores["no"] += 2.0;
            }
            else if (c is 'ä' or 'ö' or 'ü' or 'ß' or 'Ä' or 'Ö' or 'Ü')
            {
                scores["de"] += 3.0;
                scores["sv"] += 1.0;
                scores["fi"] += 1.0;
            }
            else if (c is 'ñ' or 'Ñ' or '¿' or '¡')
            {
                scores["es"] += 5.0;
            }
            else if (c is 'á' or 'í' or 'ó' or 'ú' or 'Á' or 'Í' or 'Ó' or 'Ú')
            {
                scores["es"] += 2.0;
                scores["pt"] += 1.5;
            }
            else if (c is 'ã' or 'õ' or 'Ã' or 'Õ')
            {
                scores["pt"] += 5.0;
            }
            else if (c is 'ç' or 'Ç' or 'è' or 'ê' or 'ë' or 'à' or 'â' or 'ù' or 'û' or 'ô' or 'î' or 'ï')
            {
                scores["fr"] += 2.5;
                scores["it"] += 1.0;
                scores["pt"] += 1.0;
            }
            else if (c is 'ł' or 'Ł' or 'ą' or 'ę' or 'ś' or 'ć' or 'ż' or 'ź' or 'ń' or 'Ą' or 'Ę' or 'Ś' or 'Ć' or 'Ż' or 'Ź' or 'Ń')
            {
                scores["pl"] += 4.0;
            }
            else if (c is 'ř' or 'č' or 'š' or 'ž' or 'ě' or 'ů' or 'ď' or 'ť' or 'ň' or 'Ř' or 'Č' or 'Š' or 'Ž' or 'Ě' or 'Ů')
            {
                scores["cs"] += 4.0;
            }
        }

        if (hiraganaKatakanaCount > 5) scores["ja"] += hiraganaKatakanaCount * 4.0;
        if (cjkCount > 5 && hiraganaKatakanaCount <= 2) scores["zh"] += cjkCount * 4.0;
        if (hangulCount > 5) scores["ko"] += hangulCount * 4.0;
        if (cyrillicCount > 10) scores["ru"] += cyrillicCount * 2.0;
        if (arabicCount > 10) scores["ar"] += arabicCount * 2.0;

        // 3. Word-level markers
        var matches = Regex.Matches(text, @"\b[\p{L}]{2,}\b");

        foreach (Match match in matches)
        {
            var word = match.Value;

            if (EnglishWords.Contains(word)) scores["en"] += 1.0;
            if (DanishWords.Contains(word)) scores["da"] += 1.0;
            if (GermanWords.Contains(word)) scores["de"] += 1.0;
            if (SpanishWords.Contains(word)) scores["es"] += 1.0;
            if (FrenchWords.Contains(word)) scores["fr"] += 1.0;
            if (ItalianWords.Contains(word)) scores["it"] += 1.0;
            if (PortugueseWords.Contains(word)) scores["pt"] += 1.0;
            if (DutchWords.Contains(word)) scores["nl"] += 1.0;
            if (SwedishWords.Contains(word)) scores["sv"] += 1.0;
            if (NorwegianWords.Contains(word)) scores["no"] += 1.0;
            if (PolishWords.Contains(word)) scores["pl"] += 1.0;
            if (CzechWords.Contains(word)) scores["cs"] += 1.0;
            if (FinnishWords.Contains(word)) scores["fi"] += 1.0;
        }

        double totalScore = scores.Values.Sum();
        if (totalScore <= 0.0)
        {
            return new LanguageDetectionResult("en", true, 0.5, "Default assumption");
        }

        var top = scores.OrderByDescending(kv => kv.Value).First();
        double confidence = top.Value / totalScore;
        bool isEnglish = top.Key.Equals("en", StringComparison.OrdinalIgnoreCase);

        return new LanguageDetectionResult(
            Language: top.Key.ToLowerInvariant(),
            IsEnglish: isEnglish,
            Confidence: confidence,
            Explanation: $"Statistical heuristic ({top.Key.ToUpperInvariant()} score: {top.Value:F1} of {totalScore:F1})"
        );
    }

    private sealed record LlmLanguageResponse(
        string? Language,
        bool IsEnglish,
        double Confidence,
        string? Explanation
    );
}
