using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public class EbmProductCodeParser : IEbmProductCodeParser
{
    private static readonly Regex Standard12CharRegex = new(
        @"^[ASWRKGD](?:3G|[0-9]{1,2}[ED]|[0-9]G)[0-9]{3}[A-Z0-9]{6}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex ProductCodeExtractorRegex = new(
        @"\b([ASWRKGD][\s\-]*(?:3G|[0-9]{1,2}[ED]|[0-9]G)[\s\-]*[0-9]{3}[\s\-]*[A-Z0-9]{2,4}[\s\-]*[0-9]{2,4})\b|\b(9[2-7]\d{8})\b|\b(83\d{2}[A-Z0-9\-\/]*)\b|\b([0-9]{4}[A-Z]{0,2}\/[0-9A-Z\-\+]+)\b|\b(RLF[0-9]{3}[A-Z0-9\-\/]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public bool TryParse(string? rawInput, [NotNullWhen(true)] out EbmProductInfo? productInfo)
    {
        productInfo = null;
        if (string.IsNullOrWhiteSpace(rawInput))
            return false;

        var clean = SanitizeCode(rawInput);

        // 1. Check if 12-char standard key (or standard prefix match)
        if (clean.Length == 12 && Standard12CharRegex.IsMatch(clean))
        {
            productInfo = ParseStandard12(rawInput, clean);
            return true;
        }

        // Check if starts with standard prefix even if slightly different length
        if (clean.Length >= 6 && IsStandardPrefix(clean))
        {
            productInfo = ParseStandardRelaxed(rawInput, clean);
            return true;
        }

        // 2. Check compact / other known series
        if (TryParseCompactOrOtherSeries(rawInput, clean, out var compactInfo))
        {
            productInfo = compactInfo;
            return true;
        }

        return false;
    }

    public EbmProductInfo Parse(string rawInput)
    {
        if (TryParse(rawInput, out var info))
        {
            return info;
        }

        var clean = SanitizeCode(rawInput);
        return new EbmProductInfo(
            RawCode: rawInput ?? string.Empty,
            CleanCode: clean,
            IsStandard12CharKey: false,
            FanFamily: FanFamilyType.Unknown,
            FanTypeDescription: "Ukendt eller uklassificeret produktmodel",
            AccessoryDescription: "Ukendt",
            Technology: MotorTechnology.Unknown,
            MotorDescription: "Ukendt motortype",
            Poles: null,
            PhaseDescription: null,
            ImpellerDiameterMm: null,
            VariantCode: clean,
            AirflowDirection: AirflowDirection.CustomOrUnknown,
            AirflowDescription: "Uspecificeret luftretning",
            SeriesFamily: "Ukendt serie"
        );
    }

    public List<EbmProductInfo> ExtractProductsFromText(string text)
    {
        var results = new List<EbmProductInfo>();
        if (string.IsNullOrWhiteSpace(text))
            return results;

        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ProcessText(string t)
        {
            var matches = ProductCodeExtractorRegex.Matches(t);
            foreach (Match match in matches)
            {
                var candidate = match.Value.Trim();
                var clean = SanitizeCode(candidate);
                if (seenCodes.Add(clean))
                {
                    if (TryParse(candidate, out var info))
                    {
                        results.Add(info);
                    }
                }
            }
        }

        ProcessText(text);
        if (text.Contains('_'))
        {
            ProcessText(text.Replace('_', ' '));
        }

        return results;
    }

    public EbmComparisonEvaluation Compare(EbmProductInfo productA, EbmProductInfo productB)
    {
        ArgumentNullException.ThrowIfNull(productA);
        ArgumentNullException.ThrowIfNull(productB);

        var blockers = new List<string>();
        var differences = new List<EbmComparisonDifference>();

        // 1. Fan Family comparison
        if (productA.FanFamily != FanFamilyType.Unknown && productB.FanFamily != FanFamilyType.Unknown)
        {
            if (productA.FanFamily != productB.FanFamily)
            {
                var msg = $"Forskellig blæsertype: {productA.RawCode} er {productA.FanFamily} ({productA.FanTypeDescription}), mens {productB.RawCode} er {productB.FanFamily} ({productB.FanTypeDescription}).";
                blockers.Add(msg);
                differences.Add(new EbmComparisonDifference("Fan Family", productA.FanFamily.ToString(), productB.FanFamily.ToString(), true, msg));
            }
        }

        // 2. Airflow Direction (CRITICAL FOR AXIAL FANS)
        if (productA.FanFamily == FanFamilyType.Axial && productB.FanFamily == FanFamilyType.Axial)
        {
            if (productA.AirflowDirection != AirflowDirection.CustomOrUnknown &&
                productB.AirflowDirection != AirflowDirection.CustomOrUnknown &&
                productA.AirflowDirection != productB.AirflowDirection)
            {
                var msg = $"KRITISK FORSKEL PÅ LUFTRETNING: {productA.RawCode} har luftretning {productA.AirflowDirection} ({productA.AirflowDescription}), mens {productB.RawCode} har luftretning {productB.AirflowDirection} ({productB.AirflowDescription}). Produkterne blæser modsat vej og er IKKE kompatible som direkte udskiftning!";
                blockers.Add(msg);
                differences.Add(new EbmComparisonDifference("Airflow Direction", $"Retning {productA.AirflowDirection}", $"Retning {productB.AirflowDirection}", true, msg));
            }
        }

        // 3. Impeller Diameter
        if (productA.ImpellerDiameterMm.HasValue && productB.ImpellerDiameterMm.HasValue)
        {
            if (productA.ImpellerDiameterMm.Value != productB.ImpellerDiameterMm.Value)
            {
                var msg = $"Forskellig impellerdiameter: Ø{productA.ImpellerDiameterMm.Value} mm mod Ø{productB.ImpellerDiameterMm.Value} mm. Forskellige fysiske mål forhindrer direkte mekanisk udskiftning.";
                blockers.Add(msg);
                differences.Add(new EbmComparisonDifference("Diameter", $"Ø{productA.ImpellerDiameterMm.Value} mm", $"Ø{productB.ImpellerDiameterMm.Value} mm", true, msg));
            }
        }

        // 4. Motor Technology (EC vs AC)
        if (productA.Technology != MotorTechnology.Unknown && productB.Technology != MotorTechnology.Unknown)
        {
            if (productA.Technology != productB.Technology)
            {
                var msg = $"Forskellig motorteknologi: {productA.Technology} ({productA.MotorDescription}) mod {productB.Technology} ({productB.MotorDescription}). Kræver forskellig forsynings- og styringsinfrastruktur.";
                blockers.Add(msg);
                differences.Add(new EbmComparisonDifference("Motor Technology", productA.Technology.ToString(), productB.Technology.ToString(), true, msg));
            }
        }

        // 5. AC Phase / Poles
        if (productA.Technology == MotorTechnology.AC && productB.Technology == MotorTechnology.AC)
        {
            if (!string.IsNullOrEmpty(productA.PhaseDescription) && !string.IsNullOrEmpty(productB.PhaseDescription) &&
                !string.Equals(productA.PhaseDescription, productB.PhaseDescription, StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Forskellig AC forsyning: {productA.PhaseDescription} mod {productB.PhaseDescription}.";
                blockers.Add(msg);
                differences.Add(new EbmComparisonDifference("Electrical Phase", productA.PhaseDescription, productB.PhaseDescription, true, msg));
            }

            if (productA.Poles.HasValue && productB.Poles.HasValue && productA.Poles.Value != productB.Poles.Value)
            {
                var msg = $"Forskelligt poltal: {productA.Poles.Value}-polet mod {productB.Poles.Value}-polet (forskellige omdrejningstal/karakteristik).";
                differences.Add(new EbmComparisonDifference("Poles", $"{productA.Poles.Value}-polet", $"{productB.Poles.Value}-polet", false, msg));
            }
        }

        // 6. Mechanical Accessories (A vs S vs W)
        if (productA.FanFamily == FanFamilyType.Axial && productB.FanFamily == FanFamilyType.Axial)
        {
            if (!string.Equals(productA.AccessoryDescription, productB.AccessoryDescription, StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Mekanisk tilbehør / montageform: {productA.AccessoryDescription} mod {productB.AccessoryDescription}. (S og W er baseret på samme A-grundmodel med monteret tilbehør).";
                differences.Add(new EbmComparisonDifference("Accessory / Housing", productA.AccessoryDescription, productB.AccessoryDescription, false, msg));
            }
        }

        // 7. Centrifugal mounting (R vs K vs G vs D)
        if (productA.FanFamily == FanFamilyType.Centrifugal && productB.FanFamily == FanFamilyType.Centrifugal)
        {
            if (!string.Equals(productA.AccessoryDescription, productB.AccessoryDescription, StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Centrifugal udførelse: {productA.AccessoryDescription} mod {productB.AccessoryDescription}.";
                differences.Add(new EbmComparisonDifference("Housing / Mounting", productA.AccessoryDescription, productB.AccessoryDescription, false, msg));
            }
        }

        var isDropIn = blockers.Count == 0;
        var summary = isDropIn
            ? $"Produkterne {productA.RawCode} og {productB.RawCode} deler samme fundamentale type, dimensioner, teknologi og luftretning."
            : $"Produkterne {productA.RawCode} og {productB.RawCode} er IKKE direkte udskiftelige pga. {blockers.Count} kritiske forskelle: {string.Join("; ", blockers)}";

        return new EbmComparisonEvaluation(
            ProductA: productA,
            ProductB: productB,
            IsDropInReplacement: isDropIn,
            CriticalBlockers: blockers,
            Differences: differences,
            Summary: summary
        );
    }

    public EbmReplacementAnalysis AnalyzeReplacements(EbmProductInfo sourceProduct, IEnumerable<EbmProductInfo>? catalogCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(sourceProduct);

        var matched = new List<EbmProductInfo>();
        var incompatible = new List<EbmComparisonEvaluation>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceProduct.CleanCode };

        if (catalogCandidates != null)
        {
            foreach (var cand in catalogCandidates)
            {
                if (!seenCodes.Add(cand.CleanCode))
                    continue;

                var eval = Compare(sourceProduct, cand);
                if (eval.IsDropInReplacement)
                {
                    matched.Add(cand);
                }
                else
                {
                    if (cand.FanFamily == sourceProduct.FanFamily || 
                        (cand.ImpellerDiameterMm.HasValue && sourceProduct.ImpellerDiameterMm.HasValue && Math.Abs(cand.ImpellerDiameterMm.Value - sourceProduct.ImpellerDiameterMm.Value) <= 100))
                    {
                        incompatible.Add(eval);
                    }
                }
            }
        }

        var patterns = new List<EbmReplacementPattern>();
        var rules = new List<string>();

        var diameterStr = sourceProduct.ImpellerDiameterMm?.ToString() ?? "XXX";
        var techStr = sourceProduct.Technology == MotorTechnology.EC ? "3G" : (sourceProduct.Poles.HasValue ? $"{sourceProduct.Poles}{(sourceProduct.PhaseDescription?.Contains("3-faset") == true ? "D" : "E")}" : "");

        if (sourceProduct.FanFamily == FanFamilyType.Axial)
        {
            rules.Add($"Impellerdiameter: Erstatningsmodellen skal have samme diameter (Ø{diameterStr} mm) for at passe i kappe/vægudskæring.");
            
            if (sourceProduct.AirflowDirection == AirflowDirection.A)
            {
                rules.Add("KRITISK LUFTRETNING: Erstatningen SKAL have et LIGE ciffer på 12. position (f.eks. 02, 04, 90) for at sikre Luftretning A. Et ulige ciffer vender luftretningen 180° og er inkompatibel!");
            }
            else if (sourceProduct.AirflowDirection == AirflowDirection.V)
            {
                rules.Add("KRITISK LUFTRETNING: Erstatningen SKAL have et ULIGE ciffer på 12. position (f.eks. 01, 03, 91) for at sikre Luftretning V. Et lige ciffer vender luftretningen 180° og er inkompatibel!");
            }

            var evenOrOddHint = sourceProduct.AirflowDirection == AirflowDirection.A ? "lige slutciffer (f.eks. ..02 / ..90) for Luftretning A" : "ulige slutciffer (f.eks. ..01 / ..91) for Luftretning V";

            // S-series pattern
            patterns.Add(new EbmReplacementPattern(
                PatternType: "Aksial med beskyttelsesgitter (S-serie)",
                SuggestedModelOrPrefix: $"S{techStr}{diameterStr}...",
                Description: "S-serien er mekanisk baseret på samme aksialblæser, men leveres færdigmonteret med beskyttelsesgitter (guard grille).",
                Requirements: new List<string>
                {
                    $"Diameter Ø{diameterStr} mm",
                    $"Motorteknologi {sourceProduct.Technology} ({sourceProduct.MotorDescription})",
                    $"12. ciffer skal have {evenOrOddHint}"
                }
            ));

            // W-series pattern
            patterns.Add(new EbmReplacementPattern(
                PatternType: "Aksial i vægring (W-serie)",
                SuggestedModelOrPrefix: $"W{techStr}{diameterStr}...",
                Description: "W-serien er mekanisk baseret på samme aksialblæser, men monteret i en aerodynamisk vægring (wall ring) til montage i væg eller pladeværk.",
                Requirements: new List<string>
                {
                    $"Diameter Ø{diameterStr} mm",
                    $"Motorteknologi {sourceProduct.Technology} ({sourceProduct.MotorDescription})",
                    $"12. ciffer skal have {evenOrOddHint}"
                }
            ));

            // A-series base pattern (if source was S or W)
            if (sourceProduct.CleanCode.StartsWith("S") || sourceProduct.CleanCode.StartsWith("W"))
            {
                patterns.Add(new EbmReplacementPattern(
                    PatternType: "Aksial grundmodel uden tilbehør (A-serie)",
                    SuggestedModelOrPrefix: $"A{techStr}{diameterStr}...",
                    Description: "A-serien er selve grundmotoren og impelleren uden monteret gitter eller vægring (eksisterende gitter/ring kan evt. genbruges).",
                    Requirements: new List<string>
                    {
                        $"Diameter Ø{diameterStr} mm",
                        $"12. ciffer skal have {evenOrOddHint}"
                    }
                ));
            }

            // Technology Upgrade/Alternative
            if (sourceProduct.Technology == MotorTechnology.AC)
            {
                patterns.Add(new EbmReplacementPattern(
                    PatternType: "EC Energieffektiv Opgradering (3G-serie)",
                    SuggestedModelOrPrefix: $"A3G{diameterStr}... / S3G{diameterStr}... / W3G{diameterStr}...",
                    Description: "Opgradering til nyeste EC-teknologi (3G) med integreret motorelektronik, markant lavere energiforbrug og 0-10V/PWM/Modbus hastighedsstyring.",
                    Requirements: new List<string>
                    {
                        $"Diameter Ø{diameterStr} mm",
                        "Tilslutning til 1~ 230V eller 3~ 400V forsyning",
                        "Styresignal (0-10V eller Modbus RTU)"
                    }
                ));
            }
            else if (sourceProduct.Technology == MotorTechnology.EC)
            {
                rules.Add("Motorteknologi: EC-motorer (3G) har integreret elektronik. Erstatning med AC kræver ekstern frekvensomformer og relæstyring.");
            }
        }
        else if (sourceProduct.FanFamily == FanFamilyType.Centrifugal)
        {
            rules.Add("Monteringsform: Centrifugalventilatorer findes som frit hjul (R), modul i ramme/RadiPac (K) eller i sneglehus (G/D).");

            if (sourceProduct.CleanCode.StartsWith("K"))
            {
                patterns.Add(new EbmReplacementPattern(
                    PatternType: "Motoriseret centrifugalhjul uden ramme (R-serie)",
                    SuggestedModelOrPrefix: $"R{techStr}{diameterStr}...",
                    Description: "Centrifugalhjul (single-inlet) for direkte montage i eksisterende kammer eller konsol.",
                    Requirements: new List<string> { $"Diameter Ø{diameterStr} mm", $"Samme motorteknologi ({sourceProduct.Technology})" }
                ));

                patterns.Add(new EbmReplacementPattern(
                    PatternType: "Ny generation RadiPac EC modul (8300-serien)",
                    SuggestedModelOrPrefix: "8300-serien",
                    Description: "ebm-papsts nyeste generation af RadiPac EC centrifugalmoduler (erstatter K3G-serien i nye og eksisterende anlæg).",
                    Requirements: new List<string> { "Check byggemål og nominel luftmængde/tryk i datablad" }
                ));
            }
            else if (sourceProduct.CleanCode.StartsWith("R"))
            {
                patterns.Add(new EbmReplacementPattern(
                    PatternType: "RadiPac modul i ramme (K-serie)",
                    SuggestedModelOrPrefix: $"K{techStr}{diameterStr}...",
                    Description: "Centrifugalhjul monteret i stabil monteringsramme med indløbsdyse.",
                    Requirements: new List<string> { $"Diameter Ø{diameterStr} mm" }
                ));
            }
        }
        else if (sourceProduct.FanFamily == FanFamilyType.CompactOrSpecial)
        {
            var seriesPrefix = sourceProduct.CleanCode.Length >= 4 ? sourceProduct.CleanCode.Substring(0, 4) : sourceProduct.CleanCode;
            rules.Add($"Kompaktblæser {seriesPrefix}: Kræver samme dimensioner (f.eks. {sourceProduct.ImpellerDiameterMm} mm), forsyningsspænding (f.eks. 12/24/48 VDC) og lejetype.");
            patterns.Add(new EbmReplacementPattern(
                PatternType: $"Kompaktblæser {seriesPrefix}-familien",
                SuggestedModelOrPrefix: $"{seriesPrefix}...",
                Description: "Kompakt DC-blæser fra samme familie med matchende spænding og tachoudgang.",
                Requirements: new List<string> { "Samme spænding (VDC)", "Tilsvarende omdrejningstal (RPM) og lejetype" }
            ));
        }

        var summary = matched.Count > 0
            ? $"Fandt {matched.Count} direkte erstatningskandidat(er) i databasen: {string.Join(", ", matched.Select(m => m.RawCode))}."
            : $"Der findes p.t. ingen direkte erstatningsmodeller for {sourceProduct.RawCode} i den indlæste dokumentdatabase. Erstatning bør vælges iht. de teoretiske ebm-papst typenøgle-mønstre ({string.Join(", ", patterns.Select(p => p.SuggestedModelOrPrefix))}).";

        return new EbmReplacementAnalysis(
            SourceProduct: sourceProduct,
            MatchedDatabaseCandidates: matched,
            IncompatibleDatabaseCandidates: incompatible,
            TheoreticalPatterns: patterns,
            ReplacementRules: rules,
            Summary: summary
        );
    }

    private static string SanitizeCode(string raw)
    {
        return raw.Replace("-", "")
                  .Replace(" ", "")
                  .Replace("_", "")
                  .Replace("/", "")
                  .Replace(".", "")
                  .Trim()
                  .ToUpperInvariant();
    }

    private static bool IsStandardPrefix(string clean)
    {
        if (clean.Length < 1) return false;
        char first = clean[0];
        return "ASWRKGD".Contains(first);
    }

    private static EbmProductInfo ParseStandard12(string raw, string clean)
    {
        // 1st Char
        char first = clean[0];
        var (family, typeDesc, accDesc) = ParseFirstChar(first);

        // 2nd and 3rd Char
        var (tech, motorDesc, poles, phase) = ParseMotorChars(clean.Substring(1, 2));

        // 4th, 5th, 6th Char (Impeller diameter)
        int? diameter = null;
        if (int.TryParse(clean.Substring(3, 3), out int d))
        {
            diameter = d;
        }

        // 7th to 12th Char (Variant)
        var variant = clean.Substring(6, 6);

        // 12th Char (Airflow direction for Axial fans)
        var (airflowDir, airflowDesc) = ParseAirflowDirection(family, clean[11]);

        var seriesFamily = $"{first}{clean.Substring(1, 2)}{clean.Substring(3, 3)}";

        return new EbmProductInfo(
            RawCode: raw,
            CleanCode: clean,
            IsStandard12CharKey: true,
            FanFamily: family,
            FanTypeDescription: typeDesc,
            AccessoryDescription: accDesc,
            Technology: tech,
            MotorDescription: motorDesc,
            Poles: poles,
            PhaseDescription: phase,
            ImpellerDiameterMm: diameter,
            VariantCode: variant,
            AirflowDirection: airflowDir,
            AirflowDescription: airflowDesc,
            SeriesFamily: seriesFamily
        );
    }

    private static EbmProductInfo ParseStandardRelaxed(string raw, string clean)
    {
        char first = clean[0];
        var (family, typeDesc, accDesc) = ParseFirstChar(first);

        var techPart = clean.Length >= 3 ? clean.Substring(1, 2) : "";
        var (tech, motorDesc, poles, phase) = ParseMotorChars(techPart);

        int? diameter = null;
        if (clean.Length >= 6 && int.TryParse(clean.Substring(3, 3), out int d))
        {
            diameter = d;
        }

        var variant = clean.Length > 6 ? clean.Substring(6) : "";
        var lastChar = clean[^1];
        var (airflowDir, airflowDesc) = ParseAirflowDirection(family, lastChar);

        var seriesFamily = clean.Length >= 6 ? clean.Substring(0, 6) : clean;

        return new EbmProductInfo(
            RawCode: raw,
            CleanCode: clean,
            IsStandard12CharKey: false,
            FanFamily: family,
            FanTypeDescription: typeDesc,
            AccessoryDescription: accDesc,
            Technology: tech,
            MotorDescription: motorDesc,
            Poles: poles,
            PhaseDescription: phase,
            ImpellerDiameterMm: diameter,
            VariantCode: variant,
            AirflowDirection: airflowDir,
            AirflowDescription: airflowDesc,
            SeriesFamily: seriesFamily
        );
    }

    private static (FanFamilyType Family, string TypeDesc, string AccessoryDesc) ParseFirstChar(char c)
    {
        return c switch
        {
            'A' => (FanFamilyType.Axial, "Aksialventilator (grundmodel)", "Ingen (grundmodel uden tilbehør)"),
            'S' => (FanFamilyType.Axial, "Aksialventilator med beskyttelsesgitter", "Beskyttelsesgitter (guard grille)"),
            'W' => (FanFamilyType.Axial, "Aksialventilator i vægring", "Vægring (wall ring)"),
            'R' => (FanFamilyType.Centrifugal, "Centrifugalventilator med 1 indsugning (motoriseret hjul)", "Enkeltindsugende hjul uden ramme/hus"),
            'K' => (FanFamilyType.Centrifugal, "Centrifugalventilator i ramme/bracket (RadiPac)", "Monteringsramme / bracket"),
            'G' => (FanFamilyType.Centrifugal, "Centrifugalventilator med 1 indsugning i sneglehus", "Scroll-hus (enkeltindsugende)"),
            'D' => (FanFamilyType.Centrifugal, "Centrifugalventilator med dobbelt indsugning i sneglehus", "Scroll-hus (dobbeltindsugende)"),
            _ => (FanFamilyType.Unknown, "Uspecificeret blæsertype", "Ingen")
        };
    }

    private static (MotorTechnology Tech, string Description, int? Poles, string? Phase) ParseMotorChars(string motorChars)
    {
        if (string.Equals(motorChars, "3G", StringComparison.OrdinalIgnoreCase))
        {
            return (MotorTechnology.EC, "EC-motor (elektronisk kommuteret, ny teknologi)", null, "EC elektronisk");
        }
        if (string.Equals(motorChars, "1G", StringComparison.OrdinalIgnoreCase))
        {
            return (MotorTechnology.EC, "EC-motor (1-faset elektronik)", null, "1-faset EC");
        }
        if (string.Equals(motorChars, "2G", StringComparison.OrdinalIgnoreCase))
        {
            return (MotorTechnology.EC, "EC-motor (2-polet elektronik)", null, "EC elektronisk");
        }

        if (motorChars.Length == 2 && char.IsDigit(motorChars[0]))
        {
            int poles = motorChars[0] - '0';
            char phaseChar = char.ToUpperInvariant(motorChars[1]);

            if (phaseChar == 'E')
            {
                return (MotorTechnology.AC, $"{poles}-polet 1-faset AC-motor (Einphasen)", poles, "1-faset AC (Einphasen)");
            }
            if (phaseChar == 'D')
            {
                return (MotorTechnology.AC, $"{poles}-polet 3-faset AC-motor (Dreiphasen / Drei)", poles, "3-faset AC (Dreiphasen)");
            }
        }

        return (MotorTechnology.Unknown, $"Uspecificeret motorteknologi ({motorChars})", null, null);
    }

    private static (AirflowDirection Direction, string Description) ParseAirflowDirection(FanFamilyType family, char lastChar)
    {
        if (family == FanFamilyType.Axial)
        {
            if (char.IsDigit(lastChar))
            {
                int digit = lastChar - '0';
                if (digit % 2 == 0)
                {
                    return (AirflowDirection.A, "Luftretning 'A' (lige slutciffer / sugende/trykkende iht. montage)");
                }
                else
                {
                    return (AirflowDirection.V, "Luftretning 'V' (ulige slutciffer / modsat retning af A)");
                }
            }
            return (AirflowDirection.CustomOrUnknown, "Uspecificeret aksial luftretning");
        }

        if (family == FanFamilyType.Centrifugal)
        {
            return (AirflowDirection.AxialInRadialOut, "Aksial indsugning, radial udblæsning (centrifugal standard)");
        }

        return (AirflowDirection.CustomOrUnknown, "Uspecificeret luftretning");
    }

    private static bool TryParseCompactOrOtherSeries(string raw, string clean, [NotNullWhen(true)] out EbmProductInfo? productInfo)
    {
        productInfo = null;

        // 10-digit compact part numbers (e.g. 9694300352, 9295420021, 9793510182)
        if (Regex.IsMatch(clean, @"^9[2-7]\d{8}$"))
        {
            productInfo = new EbmProductInfo(
                RawCode: raw,
                CleanCode: clean,
                IsStandard12CharKey: false,
                FanFamily: FanFamilyType.CompactOrSpecial,
                FanTypeDescription: "Kompaktblæser / Varenummer (10-cifret ebm-papst katalog-ID)",
                AccessoryDescription: "Kompakthus / Integreret",
                Technology: MotorTechnology.DC,
                MotorDescription: "Elektronisk DC / Kompaktmotor",
                Poles: null,
                PhaseDescription: "DC forsyning",
                ImpellerDiameterMm: null,
                VariantCode: clean,
                AirflowDirection: AirflowDirection.CustomOrUnknown,
                AirflowDescription: "Se specifik montage i datablad (over ribber / struts)",
                SeriesFamily: $"Serie {clean.Substring(0, 4)}"
            );
            return true;
        }

        // Compact model names (e.g. 4114N, 4114N/2H6PU, 6318/2TDH4P, 6314H, 3258J)
        if (clean.StartsWith("4114") || clean.StartsWith("6314") || clean.StartsWith("6318") || clean.StartsWith("3258"))
        {
            var series = clean.Substring(0, 4);
            productInfo = new EbmProductInfo(
                RawCode: raw,
                CleanCode: clean,
                IsStandard12CharKey: false,
                FanFamily: FanFamilyType.CompactOrSpecial,
                FanTypeDescription: $"Kompaktblæser {series}-serien",
                AccessoryDescription: "Kompakthus (metal/plast)",
                Technology: MotorTechnology.DC,
                MotorDescription: "Elektronisk DC kompaktmotor",
                Poles: null,
                PhaseDescription: "DC forsyning",
                ImpellerDiameterMm: series switch
                {
                    "4114" => 119, // 119x119 mm
                    "6314" or "6318" => 172, // Ø172 mm
                    "3258" => 92, // 92x92 mm
                    _ => null
                },
                VariantCode: clean.Length > 4 ? clean.Substring(4) : "",
                AirflowDirection: AirflowDirection.CustomOrUnknown,
                AirflowDescription: "Luftstrøm over ribber / struts",
                SeriesFamily: $"{series} Kompaktblæser"
            );
            return true;
        }

        // 8300 series (replaces K3G)
        if (clean.StartsWith("8300") || clean.StartsWith("83"))
        {
            productInfo = new EbmProductInfo(
                RawCode: raw,
                CleanCode: clean,
                IsStandard12CharKey: false,
                FanFamily: FanFamilyType.Centrifugal,
                FanTypeDescription: "Centrifugalblæser (8300-serien ny generation)",
                AccessoryDescription: "RadiPac / EC Modul",
                Technology: MotorTechnology.EC,
                MotorDescription: "EC-motor (ny generation erstatning for K3G)",
                Poles: null,
                PhaseDescription: "EC 3~ / 1~",
                ImpellerDiameterMm: null,
                VariantCode: clean,
                AirflowDirection: AirflowDirection.AxialInRadialOut,
                AirflowDescription: "Aksial indsugning, radial udblæsning",
                SeriesFamily: "8300 EC-serie"
            );
            return true;
        }

        // RLF series (e.g. RLF100)
        if (clean.StartsWith("RLF"))
        {
            productInfo = new EbmProductInfo(
                RawCode: raw,
                CleanCode: clean,
                IsStandard12CharKey: false,
                FanFamily: FanFamilyType.Centrifugal,
                FanTypeDescription: "Kompakt radialblæser (RLF-serien)",
                AccessoryDescription: "Radialblæserhus",
                Technology: MotorTechnology.DC,
                MotorDescription: "DC Radialmotor",
                Poles: null,
                PhaseDescription: "DC forsyning",
                ImpellerDiameterMm: 100,
                VariantCode: clean,
                AirflowDirection: AirflowDirection.AxialInRadialOut,
                AirflowDescription: "Radial luftafgang",
                SeriesFamily: "RLF Radialserie"
            );
            return true;
        }

        return false;
    }
}
