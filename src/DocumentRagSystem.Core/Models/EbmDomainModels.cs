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
);

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
