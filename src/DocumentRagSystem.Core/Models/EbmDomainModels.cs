using System;
using System.Text.Json.Serialization;

namespace DocumentRagSystem.Core.Models;

public enum FanFamilyType
{
    Axial,
    Centrifugal,
    CompactOrSpecial,
    Unknown
}

public enum MotorTechnology
{
    EC,
    AC,
    DC,
    Unknown
}

public enum AirflowDirection
{
    A, // Even 12th digit (Axial) - Luftretning A
    V, // Odd 12th digit (Axial) - Luftretning V
    AxialInRadialOut, // Typical centrifugal
    RadialInAxialOut,
    CustomOrUnknown
}

public enum ContentLanguage
{
    Danish,
    English,
    German
}

public record EbmProductInfo(
    [property: JsonPropertyName("raw_code")] string RawCode,
    [property: JsonPropertyName("clean_code")] string CleanCode,
    [property: JsonPropertyName("is_standard_12_char_key")] bool IsStandard12CharKey,
    [property: JsonPropertyName("fan_family")] FanFamilyType FanFamily,
    [property: JsonPropertyName("fan_type_description")] string FanTypeDescription,
    [property: JsonPropertyName("accessory_description")] string AccessoryDescription,
    [property: JsonPropertyName("technology")] MotorTechnology Technology,
    [property: JsonPropertyName("motor_description")] string MotorDescription,
    [property: JsonPropertyName("poles")] int? Poles,
    [property: JsonPropertyName("phase_description")] string? PhaseDescription,
    [property: JsonPropertyName("impeller_diameter_mm")] int? ImpellerDiameterMm,
    [property: JsonPropertyName("variant_code")] string VariantCode,
    [property: JsonPropertyName("airflow_direction")] AirflowDirection AirflowDirection,
    [property: JsonPropertyName("airflow_description")] string AirflowDescription,
    [property: JsonPropertyName("series_family")] string SeriesFamily
)
{
    public string GetMotorDescription(ContentLanguage language = ContentLanguage.Danish)
    {
        if (language == ContentLanguage.Danish) return MotorDescription;

        if (language == ContentLanguage.German)
        {
            switch (Technology)
            {
                case MotorTechnology.DC:
                    if (CleanCode.Length == 10 && CleanCode.StartsWith("9"))
                        return "Elektronischer DC- / Kompaktmotor";
                    if (CleanCode.StartsWith("4114") || CleanCode.StartsWith("6314") || CleanCode.StartsWith("6318") || CleanCode.StartsWith("3258"))
                        return "Elektronischer DC-Kompaktmotor";
                    if (CleanCode.StartsWith("RLF"))
                        return "DC-Radialmotor";
                    return "DC-Motor";

                case MotorTechnology.EC:
                    if (CleanCode.StartsWith("8300") || CleanCode.StartsWith("83"))
                        return "EC-Motor (neue Generation Ersatz für K3G)";
                    if (RawCode.Contains("3G", StringComparison.OrdinalIgnoreCase))
                        return "EC-Motor (elektronisch kommutiert, energiesparend)";
                    if (RawCode.Contains("1G", StringComparison.OrdinalIgnoreCase))
                        return "EC-Motor (1-phasige Elektronik)";
                    if (RawCode.Contains("2G", StringComparison.OrdinalIgnoreCase))
                        return "EC-Motor (2-polige Elektronik)";
                    return "EC-Motor (elektronisch kommutiert)";

                case MotorTechnology.AC:
                    if (Poles.HasValue)
                    {
                        var phase = PhaseDescription != null && PhaseDescription.Contains("3-faset", StringComparison.OrdinalIgnoreCase)
                            ? "3-phasiger AC-Motor (Drehstrom / Dreiphasenwechselstrom)"
                            : "1-phasiger AC-Motor (Einphasenwechselstrom)";
                        return $"{Poles}-poliger {phase}";
                    }
                    return "AC-Motor";

                default:
                    if (MotorDescription.Contains("Kompaktmotor", StringComparison.OrdinalIgnoreCase) || MotorDescription.Contains("Elektronisk DC", StringComparison.OrdinalIgnoreCase))
                        return "Elektronischer DC- / Kompaktmotor";
                    return MotorDescription;
            }
        }

        switch (Technology)
        {
            case MotorTechnology.DC:
                if (CleanCode.Length == 10 && CleanCode.StartsWith("9"))
                    return "Electronic DC / Compact Motor";
                if (CleanCode.StartsWith("4114") || CleanCode.StartsWith("6314") || CleanCode.StartsWith("6318") || CleanCode.StartsWith("3258"))
                    return "Electronic DC compact motor";
                if (CleanCode.StartsWith("RLF"))
                    return "DC radial motor";
                return "DC motor";

            case MotorTechnology.EC:
                if (CleanCode.StartsWith("8300") || CleanCode.StartsWith("83"))
                    return "EC motor (new generation replacement for K3G)";
                if (RawCode.Contains("3G", StringComparison.OrdinalIgnoreCase))
                    return "EC motor (electronically commutated, energy-saving)";
                if (RawCode.Contains("1G", StringComparison.OrdinalIgnoreCase))
                    return "EC motor (1-phase electronics)";
                if (RawCode.Contains("2G", StringComparison.OrdinalIgnoreCase))
                    return "EC motor (2-pole electronics)";
                return "EC motor (electronically commutated)";

            case MotorTechnology.AC:
                if (Poles.HasValue)
                {
                    var phase = PhaseDescription != null && PhaseDescription.Contains("3-faset", StringComparison.OrdinalIgnoreCase)
                        ? "3-phase AC motor (three-phase)"
                        : "1-phase AC motor (single-phase)";
                    return $"{Poles}-pole {phase}";
                }
                return "AC motor";

            default:
                if (MotorDescription.Contains("Kompaktmotor", StringComparison.OrdinalIgnoreCase) || MotorDescription.Contains("Elektronisk DC", StringComparison.OrdinalIgnoreCase))
                    return "Electronic DC / Compact Motor";
                return MotorDescription;
        }
    }

    public string GetAirflowDescription(ContentLanguage language = ContentLanguage.Danish)
    {
        if (language == ContentLanguage.Danish) return AirflowDescription;

        if (language == ContentLanguage.German)
        {
            if (AirflowDirection == AirflowDirection.A)
                return "Förderrichtung 'A' (über Stege saugend/blasend je nach Montage)";
            if (AirflowDirection == AirflowDirection.V)
                return "Förderrichtung 'V' (entgegengesetzt zu A)";
            if (AirflowDirection == AirflowDirection.AxialInRadialOut)
                return "Axial saugend, radial ausblasend";

            if (AirflowDescription.Contains("Se specifik montage", StringComparison.OrdinalIgnoreCase))
                return "Spezifische Montage siehe Datenblatt (über Stege)";
            if (AirflowDescription.Contains("over ribber / struts", StringComparison.OrdinalIgnoreCase))
                return "Luftförderung über Stege";
            if (AirflowDescription.Contains("Radial luftafgang", StringComparison.OrdinalIgnoreCase))
                return "Radiale Luftausblasung";

            return AirflowDescription;
        }

        if (AirflowDirection == AirflowDirection.A)
            return "Airflow direction 'A' (intake over struts/blades depending on mounting)";
        if (AirflowDirection == AirflowDirection.V)
            return "Airflow direction 'V' (opposite direction of A)";
        if (AirflowDirection == AirflowDirection.AxialInRadialOut)
            return "Axial intake, radial discharge";

        if (AirflowDescription.Contains("Se specifik montage", StringComparison.OrdinalIgnoreCase))
            return "See datasheet for specific mounting (over struts / ribs)";
        if (AirflowDescription.Contains("over ribber / struts", StringComparison.OrdinalIgnoreCase))
            return "Airflow over struts / ribs";
        if (AirflowDescription.Contains("Radial luftafgang", StringComparison.OrdinalIgnoreCase))
            return "Radial air discharge";

        return AirflowDescription;
    }

    public string GetMotorDescription(bool isEnglish) => GetMotorDescription(isEnglish ? ContentLanguage.English : ContentLanguage.Danish);
    public string GetAirflowDescription(bool isEnglish) => GetAirflowDescription(isEnglish ? ContentLanguage.English : ContentLanguage.Danish);
}

public record EbmComparisonDifference(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("value_a")] string ValueA,
    [property: JsonPropertyName("value_b")] string ValueB,
    [property: JsonPropertyName("is_critical_incompatibility")] bool IsCriticalIncompatibility,
    [property: JsonPropertyName("explanation")] string Explanation
);

public record EbmComparisonEvaluation(
    [property: JsonPropertyName("product_a")] EbmProductInfo ProductA,
    [property: JsonPropertyName("product_b")] EbmProductInfo ProductB,
    [property: JsonPropertyName("is_drop_in_replacement")] bool IsDropInReplacement,
    [property: JsonPropertyName("critical_blockers")] List<string> CriticalBlockers,
    [property: JsonPropertyName("differences")] List<EbmComparisonDifference> Differences,
    [property: JsonPropertyName("summary")] string Summary
);

public record EbmReplacementPattern(
    [property: JsonPropertyName("pattern_type")] string PatternType,
    [property: JsonPropertyName("suggested_model_or_prefix")] string SuggestedModelOrPrefix,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("requirements")] List<string> Requirements
);

public record EbmReplacementAnalysis(
    [property: JsonPropertyName("source_product")] EbmProductInfo SourceProduct,
    [property: JsonPropertyName("matched_database_candidates")] List<EbmProductInfo> MatchedDatabaseCandidates,
    [property: JsonPropertyName("incompatible_database_candidates")] List<EbmComparisonEvaluation> IncompatibleDatabaseCandidates,
    [property: JsonPropertyName("theoretical_patterns")] List<EbmReplacementPattern> TheoreticalPatterns,
    [property: JsonPropertyName("replacement_rules")] List<string> ReplacementRules,
    [property: JsonPropertyName("summary")] string Summary
);
