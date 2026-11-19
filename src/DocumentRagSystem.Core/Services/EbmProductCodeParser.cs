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
        @"(?:(?:modelnavn|model|varenr|varenummer|artikelnr|artikelnummer|type)\s*[:=]\s*([A-Za-z0-9\-\/]+))|(?:Art_([A-Za-z0-9\-]+?)(?:[-_]Doc|\.pdf|\b))|\b([ASWRKGD](?:3G|[0-9]{1,2}[ED]|[0-9]G)[0-9]{3,4}(?:-?[A-Z0-9]{2,4}-?[A-Z0-9]{1,4})?)\b|\b(V[A-Z]{2}0?[0-9]{3}[A-Z0-9]+)\b|\b(9[2-7]\d{8})\b|\b(83\d{8})\b|\b(83\d{2}[A-Z0-9\-\/]*)\b|\b((?:44|43|42|41|84|32|34|63|64|52|53)[0-9]{2}[A-Za-z0-9\-\/]*)\b|\b([0-9]{4}[A-Z]{0,2}\/[0-9A-Z\-\+]+)\b|\b(RLF[0-9]{3}[A-Z0-9\-\/]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex ExtraZeroDiameterRegex = new(
        @"^([ASWRKGD](?:3G|[0-9]{1,2}[ED]|[0-9]G))([0-9]{3})0([A-Z0-9]{6})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex VSeriesRegex = new(
        @"^V[A-Z]{2}0?([1-9][0-9]{2})([A-Z0-9]+)$",
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
                var explicitMatch = Regex.Match(candidate, @"^(?:modelnavn|model|varenr|varenummer|artikelnr|artikelnummer|type)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase);
                if (explicitMatch.Success)
                {
                    candidate = explicitMatch.Groups[1].Value.Trim();
                }
                if (candidate.StartsWith("Art_", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = candidate.Substring(4);
                }
                candidate = Regex.Replace(candidate, @"[-_]Doc.*$", "", RegexOptions.IgnoreCase);

                var clean = SanitizeCode(candidate);
                if (seenCodes.Add(clean))
                {
                    if (TryParse(candidate, out var info))
                    {
                        results.Add(info);
                    }
                    else if (explicitMatch.Success && !string.IsNullOrWhiteSpace(clean))
                    {
                        results.Add(new EbmProductInfo(
                            RawCode: candidate,
                            CleanCode: clean,
                            IsStandard12CharKey: false,
                            FanFamily: FanFamilyType.CompactOrSpecial,
                            FanTypeDescription: $"Model {candidate}",
                            AccessoryDescription: "Special / ukendt modeltype",
                            Technology: MotorTechnology.Unknown,
                            MotorDescription: "Uspecificeret motorteknologi",
                            Poles: null,
                            PhaseDescription: "Uspecificeret forsyning",
                            ImpellerDiameterMm: null,
                            VariantCode: "",
                            AirflowDirection: AirflowDirection.CustomOrUnknown,
                            AirflowDescription: "Se datablad",
                            SeriesFamily: $"Model {candidate}"
                        ));
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
        else if (productA.ImpellerDiameterMm.HasValue != productB.ImpellerDiameterMm.HasValue)
        {
            var known = productA.ImpellerDiameterMm ?? productB.ImpellerDiameterMm;
            var unknownProd = productA.ImpellerDiameterMm.HasValue ? productB.RawCode : productA.RawCode;
            var msg = $"Uvis eller uverificeret impellerdiameter for {unknownProd} (sammenlignet med Ø{known} mm). Kræver manuel verifikation af mekaniske dimensioner før udskiftning.";
            blockers.Add(msg);
            differences.Add(new EbmComparisonDifference("Diameter", productA.ImpellerDiameterMm.HasValue ? $"Ø{productA.ImpellerDiameterMm.Value} mm" : "Ubekendt", productB.ImpellerDiameterMm.HasValue ? $"Ø{productB.ImpellerDiameterMm.Value} mm" : "Ubekendt", true, msg));
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

        // 7. Centrifugal mounting (R vs K vs G vs D, and 8300 RadiPac)
        if (productA.FanFamily == FanFamilyType.Centrifugal && productB.FanFamily == FanFamilyType.Centrifugal)
        {
            bool isRadiPacEquivalent = (productA.CleanCode.StartsWith("K") && productB.CleanCode.StartsWith("8300")) ||
                                       (productB.CleanCode.StartsWith("K") && productA.CleanCode.StartsWith("8300"));

            bool isSameMounting = (productA.CleanCode.Length > 0 && productB.CleanCode.Length > 0 && productA.CleanCode[0] == productB.CleanCode[0]) || isRadiPacEquivalent;

            if (!isSameMounting)
            {
                var msg = $"Forskellig mekanisk centrifugal-udførelse: {productA.RawCode} ({productA.AccessoryDescription}) mod {productB.RawCode} ({productB.AccessoryDescription}). En K-serie (komplet RadiPac modul i bæreramme) og en R-serie (løst motoriseret pumpehjul uden ramme) er mekanisk forskellige og ikke direkte drop-in udskiftelige!";
                blockers.Add(msg);
                differences.Add(new EbmComparisonDifference("Housing / Mounting", productA.AccessoryDescription, productB.AccessoryDescription, true, msg));
            }

            // 7b. RadiPac (PR/PE/PC) vs RadiCal (RR) aerodynamic series difference
            var aCode = productA.CleanCode;
            var bCode = productB.CleanCode;

            bool aIsRadiPac = aCode.Contains("PR") || aCode.Contains("PE") || aCode.Contains("PC") || aCode.StartsWith("8300") || aCode.StartsWith("VBH");
            bool bIsRadiPac = bCode.Contains("PR") || bCode.Contains("PE") || bCode.Contains("PC") || bCode.StartsWith("8300") || bCode.StartsWith("VBH");

            bool aIsRadiCal = aCode.Contains("RR") || aCode.StartsWith("VBS");
            bool bIsRadiCal = bCode.Contains("RR") || bCode.StartsWith("VBS");

            if ((aIsRadiPac && bIsRadiCal) || (aIsRadiCal && bIsRadiPac))
            {
                var msg = $"Forskellig aerodynamisk serie: {productA.RawCode} ({(aIsRadiPac ? "RadiPac" : "RadiCal")}) mod {productB.RawCode} ({(bIsRadiPac ? "RadiPac" : "RadiCal")}). RadiCal er en anden aerodynamisk serie med lavere tryk og luftmængde end RadiPac og er IKKE en direkte drop-in erstatning!";
                blockers.Add(msg);
                differences.Add(new EbmComparisonDifference("Aerodynamic Series", aIsRadiPac ? "RadiPac" : "RadiCal", bIsRadiPac ? "RadiPac" : "RadiCal", true, msg));
            }
        }

        // 8. Electrical supply check between EC products if phase/voltage is specified
        if (productA.Technology == MotorTechnology.EC && productB.Technology == MotorTechnology.EC)
        {
            var aSupply = GetEcSupply(productA);
            var bSupply = GetEcSupply(productB);

            if (!string.IsNullOrWhiteSpace(aSupply) && !string.IsNullOrWhiteSpace(bSupply))
            {
                bool aIs3Phase = aSupply.Contains("3~") || aSupply.Contains("400");
                bool bIs3Phase = bSupply.Contains("3~") || bSupply.Contains("400");
                bool aIs1Phase = aSupply.Contains("1~") || aSupply.Contains("230");
                bool bIs1Phase = bSupply.Contains("1~") || bSupply.Contains("230");

                if ((aIs3Phase && bIs1Phase) || (aIs1Phase && bIs3Phase))
                {
                    var msg = $"Forskellig forsyningsspænding: {productA.RawCode} ({aSupply}) mod {productB.RawCode} ({bSupply}). 400 VAC 3-faset forsyning er inkompatibel med en 230 VAC 1-faset installation.";
                    blockers.Add(msg);
                    differences.Add(new EbmComparisonDifference("Electrical Supply", aSupply, bSupply, true, msg));
                }
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

        // Rank matched drop-in candidates: perfect diameter match first, then closest wattage (power) proximity
        if (matched.Count > 1)
        {
            int? sourceWatts = TryExtractWatts(sourceProduct);

            matched = matched
                .OrderBy(c =>
                {
                    int diamDiff = (c.ImpellerDiameterMm.HasValue && sourceProduct.ImpellerDiameterMm.HasValue)
                        ? Math.Abs(c.ImpellerDiameterMm.Value - sourceProduct.ImpellerDiameterMm.Value)
                        : 0;

                    int wattsDiff = 0;
                    var cWatts = TryExtractWatts(c);
                    if (sourceWatts.HasValue && cWatts.HasValue)
                    {
                        wattsDiff = Math.Abs(cWatts.Value - sourceWatts.Value);
                    }
                    else if (cWatts.HasValue)
                    {
                        wattsDiff = -cWatts.Value;
                    }

                    return (diamDiff, wattsDiff);
                })
                .ToList();
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
                var diam = sourceProduct.ImpellerDiameterMm;
                string? knownCandidate = diam switch
                {
                    250 => "type 8300102192 / VBH0250SSPFS, Ø250 mm, 230 VAC",
                    280 => "type 8300100757 / VBH0280CSPFS, Ø280 mm, 230 VAC, 800 W",
                    310 => "type 8300100875 / VBH0310CSPFS, Ø310 mm, 230 VAC",
                    355 => "type 8300100049 / VBH0355CTPMS, Ø355 mm, 400 VAC",
                    400 => "type 8300100799 / VWT0400CTPFS, Ø400 mm, 400 VAC",
                    560 => "type 8300102220 / VBH0560CTTPZ, Ø560 mm, 200..240 VAC",
                    _ => null
                };

                var suggested8300 = knownCandidate != null
                    ? $"8300-serien RadiPac EC ({knownCandidate})"
                    : $"8300-serien RadiPac EC (Ø{diameterStr} mm)";

                patterns.Add(new EbmReplacementPattern(
                    PatternType: "Direkte drop-in RadiPac EC modul (8300-serien)",
                    SuggestedModelOrPrefix: suggested8300,
                    Description: $"ebm-papsts nyeste generation af RadiPac EC centrifugalmoduler (erstatter K3G-serien som komplet drop-in enhed med bæreramme og indløbsdyse). For K3G{diameterStr} vælges en model fra 8300-serien med Ø{diameterStr} mm, samme forsyningsspænding og nominel optagen effekt (W) så tæt på kilden som muligt.",
                    Requirements: new List<string> { $"Diameter Ø{diameterStr} mm", "Samme forsyningsform (230 VAC vs 400 VAC)", "Vælg model med nominel optagen effekt (W) tættest på kilden", "Komplet drop-in udskiftning (ingen genbrug af gammel ramme påkrævet)" }
                ));

                patterns.Add(new EbmReplacementPattern(
                    PatternType: "Delvis erstatning / ombygning: Centrifugalhjul uden ramme (R-serie)",
                    SuggestedModelOrPrefix: $"R{techStr}{diameterStr}...",
                    Description: "Sekundær løsning: Kun selve motor/pumpehjulet udskiftes, mens den eksisterende bæreramme og indløbsdyse fra K3G genbruges. Dette er en delvis erstatning og bør kun vælges hvis et komplet 8300 RadiPac modul ikke ønskes.",
                    Requirements: new List<string> { $"Diameter Ø{diameterStr} mm", $"Samme motorteknologi ({sourceProduct.Technology})", "Kræver demontering og genbrug af eksisterende bæreramme og indløbsdyse (delvis mekanisk løsning)" }
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

        // Universal replacement rules (electrical form, wattage proximity, dimensions, disclosure)
        rules.Add("Forsyningsform: Erstatninger skal benytte samme forsyningsspænding og fase (1~ 230 VAC, 3~ 400 VAC eller 12/24/48 VDC). En 400 VAC 3-faset model må f.eks. ikke anvendes som direkte erstatning for en 230 VAC model uden tavleombygning.");
        rules.Add("Optagen effekt (Watt): Erstatningens nominelle effektforbrug skal ligge så tæt på kildens effekt som muligt for at undgå unødig overdimensionering eller overbelastning (f.eks. er 800 W en markant bedre erstatning for 750 W end 1300 W).");
        rules.Add("Dimensioner: Impellerdiameter og byggemål skal matche så tæt som muligt for mekanisk montering.");
        rules.Add("Alternative forslag: Assistenten (Ava) må gerne foreslå en anden erstatning, hvis den optimale mangler, men skal altid eksplicit oplyse om samtlige forskelle i spænding, faser, watt og dimensioner.");

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

    private static int? TryExtractWatts(EbmProductInfo p)
    {
        if (!string.IsNullOrWhiteSpace(p.PhaseDescription))
        {
            var m = Regex.Match(p.PhaseDescription, @"(\d+)\s*W\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var w))
                return w;
        }
        return null;
    }

    private static string? GetEcSupply(EbmProductInfo p)
    {
        // 1. Check existing PhaseDescription (which is loaded from metadata or datasheet table)
        if (!string.IsNullOrWhiteSpace(p.PhaseDescription) &&
            (p.PhaseDescription.Contains("230") || p.PhaseDescription.Contains("400") || p.PhaseDescription.Contains("3~") || p.PhaseDescription.Contains("1~") || p.PhaseDescription.Contains("DC")))
        {
            return p.PhaseDescription;
        }

        // 2. Known 8300-series model numbers from ebm-papst catalog
        if (p.CleanCode.StartsWith("8300100757") || p.CleanCode.StartsWith("8300100875") || p.CleanCode.StartsWith("8300102192") ||
            p.CleanCode.StartsWith("8300101052") || p.CleanCode.StartsWith("8300101158"))
        {
            return "1~ 230 VAC";
        }
        if (p.CleanCode.StartsWith("8300100799") || p.CleanCode.StartsWith("8300100049") || p.CleanCode.StartsWith("8300102292") ||
            p.CleanCode.StartsWith("8300102351"))
        {
            return "3~ 400 VAC";
        }
        if (p.CleanCode.StartsWith("8300102220"))
        {
            return "3~ 200..240 VAC";
        }

        // 3. Generic decoding of ebm-papst RadiPac V-series secondary type code (e.g. VBH0280CSPFS -> S = 1~ 230V, T = 3~ 400V, C = 3~ 200..240V)
        var vMatch = Regex.Match(p.RawCode, @"V[A-Z]{2}0?([1-9][0-9]{2})([A-Z0-9]+)", RegexOptions.IgnoreCase);
        if (vMatch.Success)
        {
            var suffix = vMatch.Groups[2].Value.ToUpperInvariant();
            if (suffix.Contains('T')) return "3~ 400 VAC";
            if (suffix.Contains('S')) return "1~ 230 VAC";
            if (suffix.Contains('C')) return "3~ 200..240 VAC";
        }

        // 4. Generic decoding of K3G motor variant prefixes (PR/PE = 1~ 230V, RR/PC/HR = 3~ 400V)
        if (p.CleanCode.StartsWith("K3G") && p.VariantCode.Length >= 2)
        {
            var pfx = p.VariantCode.Substring(0, 2);
            if (pfx.Equals("PR", StringComparison.OrdinalIgnoreCase) || pfx.Equals("PE", StringComparison.OrdinalIgnoreCase))
                return "1~ 230 VAC";
            if (pfx.Equals("RR", StringComparison.OrdinalIgnoreCase) || pfx.Equals("PC", StringComparison.OrdinalIgnoreCase) || pfx.Equals("HR", StringComparison.OrdinalIgnoreCase))
                return "3~ 400 VAC";
        }

        return null;
    }

    private static string SanitizeCode(string raw)
    {
        var clean = raw.Replace("-", "")
                   .Replace(" ", "")
                   .Replace("_", "")
                   .Replace("/", "")
                   .Replace(".", "")
                   .Trim()
                   .ToUpperInvariant();

        // Extra-zero diameter typo correction:
        // E.g. A4E3000AS7201 (13 chars, 3000 mm) -> A4E300AS7201 (12 chars, 300 mm)
        if (clean.Length == 13)
        {
            var match = ExtraZeroDiameterRegex.Match(clean);
            if (match.Success && int.TryParse(match.Groups[2].Value, out var diam))
            {
                if (DimensionalQueryExpander.KnownEbmDimensions.Contains(diam))
                {
                    clean = $"{match.Groups[1].Value}{match.Groups[2].Value}{match.Groups[3].Value}";
                }
            }
        }

        return clean;
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
                    return (AirflowDirection.A, "Luftretning 'A' (vejledende lige slutciffer – verificeres i produktblad)");
                }
                else
                {
                    return (AirflowDirection.V, "Luftretning 'V' (vejledende ulige slutciffer – verificeres i produktblad)");
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

        // Compact model names (e.g. 4420, 4414N, 4114N, 6318/2TDH4P, 6314H, 3258J, 8414, 4314)
        var compactMatch = Regex.Match(clean, @"^(?:ACi|AC)?(44|43|42|41|84|32|34|63|64|52|53)[0-9]{2}([A-Za-z0-9\-\/]*)$", RegexOptions.IgnoreCase);
        if (compactMatch.Success || clean.StartsWith("4114") || clean.StartsWith("6314") || clean.StartsWith("6318") || clean.StartsWith("3258"))
        {
            var prefix2 = compactMatch.Success ? compactMatch.Groups[1].Value : clean.Substring(0, 2);
            var series = clean.Length >= 4 ? clean.Substring(0, 4) : clean;
            int? diameter = prefix2 switch
            {
                "44" or "43" or "42" or "41" => 119, // 119x119 mm
                "84" => 80,                         // 80x80 mm
                "32" or "34" => 92,                 // 92x92 mm
                "63" or "64" => 172,                // Ø172 mm
                "52" => 127,                        // 127x127 mm
                "53" => 140,                        // 140x140 mm
                _ => null
            };

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
                ImpellerDiameterMm: diameter,
                VariantCode: clean.Length > 4 ? clean.Substring(4) : "",
                AirflowDirection: AirflowDirection.CustomOrUnknown,
                AirflowDescription: "Luftstrøm over ribber / struts",
                SeriesFamily: $"{series} Kompaktblæser"
            );
            return true;
        }

        // RadiPac / AxiEco / RadiCal V-series secondary designation (e.g. VBH0280CSPFS, VWT0400CTPFS, VBH0250SSPFS, VBS0206SSLES)
        if (clean.StartsWith("V"))
        {
            var m = VSeriesRegex.Match(clean);
            if (m.Success)
            {
                int diameter = int.Parse(m.Groups[1].Value);
                var suffix = m.Groups[2].Value.ToUpperInvariant();
                var phaseDesc = suffix.Contains('T') ? "3~ 400 VAC" : (suffix.Contains('S') ? "1~ 230 VAC" : "EC elektronisk");
                bool isAxial = clean.StartsWith("VWT", StringComparison.OrdinalIgnoreCase);

                productInfo = new EbmProductInfo(
                    RawCode: raw,
                    CleanCode: clean,
                    IsStandard12CharKey: false,
                    FanFamily: isAxial ? FanFamilyType.Axial : FanFamilyType.Centrifugal,
                    FanTypeDescription: isAxial ? "Aksialventilator (AxiEco V-serie)" : "Centrifugalblæser (RadiPac V-serie)",
                    AccessoryDescription: isAxial ? "Vægramme / Gitter" : "RadiPac monteringsramme / bracket",
                    Technology: MotorTechnology.EC,
                    MotorDescription: "GreenTech EC-motor (ny generation)",
                    Poles: null,
                    PhaseDescription: phaseDesc,
                    ImpellerDiameterMm: diameter,
                    VariantCode: clean,
                    AirflowDirection: isAxial ? AirflowDirection.CustomOrUnknown : AirflowDirection.AxialInRadialOut,
                    AirflowDescription: isAxial ? "Aksial luftstrøm" : "Aksial indsugning, radial udblæsning",
                    SeriesFamily: $"{clean.Substring(0, 3)} Serie"
                );
                return true;
            }
        }

        // 8300 series (replaces K3G)
        if (clean.StartsWith("8300") || clean.StartsWith("83"))
        {
            int? diameter = null;
            string phaseDesc = "EC elektronisk";

            // Known 8300-series model numbers from ebm-papst catalog
            if (clean.StartsWith("8300100757")) { diameter = 280; phaseDesc = "1~ 230 VAC (800 W)"; }
            else if (clean.StartsWith("8300100875")) { diameter = 310; phaseDesc = "1~ 230 VAC (1020 W)"; }
            else if (clean.StartsWith("8300100799")) { diameter = 400; phaseDesc = "3~ 400 VAC (1000 W)"; }
            else if (clean.StartsWith("8300100049")) { diameter = 355; phaseDesc = "3~ 400 VAC (1500 W)"; }
            else if (clean.StartsWith("8300102192")) { diameter = 250; phaseDesc = "1~ 230 VAC (750 W)"; }
            else if (clean.StartsWith("8300101052")) { diameter = 206; phaseDesc = "1~ 230 VAC (120 W)"; }
            else if (clean.StartsWith("8300101158")) { diameter = 180; phaseDesc = "1~ 230 VAC (165 W)"; }
            else if (clean.StartsWith("8300102220")) { diameter = 560; phaseDesc = "3~ 200..240 VAC (4400 W)"; }
            else if (clean.StartsWith("8300102292")) { diameter = 280; phaseDesc = "3~ 400 VAC (1300 W)"; }
            else if (clean.StartsWith("8300102351")) { diameter = 450; phaseDesc = "3~ 400 VAC (2750 W)"; }

            // Generic decoding of ebm-papst RadiPac V-series secondary designation (e.g. VBH0280TPFS, VWT0400CTPFS, VBH0280CSPFS, VBH0250SSPFS)
            var vMatch = Regex.Match(raw, @"V[A-Z]{2}0?([1-9][0-9]{2})([A-Z0-9]+)", RegexOptions.IgnoreCase);
            if (vMatch.Success)
            {
                if (!diameter.HasValue && int.TryParse(vMatch.Groups[1].Value, out var d))
                {
                    diameter = d;
                }
                var suffix = vMatch.Groups[2].Value.ToUpperInvariant();
                if (suffix.Contains('T')) phaseDesc = "3~ 400 VAC";
                else if (suffix.Contains('S')) phaseDesc = "1~ 230 VAC";
                else if (suffix.Contains('C')) phaseDesc = "3~ 200..240 VAC";
            }
            else if (!diameter.HasValue)
            {
                var diamMatch = Regex.Match(raw, @"(?:Ø\s*(\d{3})|\b(133|140|160|175|180|190|200|225|250|280|310|355|400|450|500|560|630|710|800|910)\s*mm\b)");
                if (diamMatch.Success)
                {
                    var val = diamMatch.Groups[1].Success ? diamMatch.Groups[1].Value : diamMatch.Groups[2].Value;
                    if (int.TryParse(val, out var d)) diameter = d;
                }
            }

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
                PhaseDescription: phaseDesc,
                ImpellerDiameterMm: diameter,
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
