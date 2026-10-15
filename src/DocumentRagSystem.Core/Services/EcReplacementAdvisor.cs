using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public record EcReplacementRecommendation(
    string LegacyAcModel,
    string RecommendedEcSeries,
    string RecommendedEcModelPrefix,
    int? ImpellerDiameterMm,
    string FanTypeDescription,
    string EnergySavingsEstimate,
    string KeyAdvantages,
    string MigrationNotes
);

public static class EcReplacementAdvisor
{
    private static readonly Regex LegacyAcPrefixRegex = new(
        @"^[ASWRKGD][0-9]{1,2}[ED]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public static bool IsLegacyAc(EbmProductInfo? product)
    {
        if (product == null) return false;
        if (product.Technology == MotorTechnology.AC) return true;
        if (!string.IsNullOrWhiteSpace(product.CleanCode) && LegacyAcPrefixRegex.IsMatch(product.CleanCode)) return true;
        if (product.CleanCode.StartsWith("4650", StringComparison.OrdinalIgnoreCase) ||
            product.CleanCode.StartsWith("4656", StringComparison.OrdinalIgnoreCase) ||
            product.CleanCode.StartsWith("W2S130", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    public static EcReplacementRecommendation? GetEcReplacement(EbmProductInfo product)
    {
        if (!IsLegacyAc(product)) return null;

        var clean = product.CleanCode;
        int? diameter = product.ImpellerDiameterMm;

        // Compact AC fans
        if (clean.StartsWith("4650", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("4656", StringComparison.OrdinalIgnoreCase))
        {
            return new EcReplacementRecommendation(
                LegacyAcModel: product.RawCode,
                RecommendedEcSeries: "ACi 4400 / 4300N GreenTech EC",
                RecommendedEcModelPrefix: "ACi4400 / 4314N",
                ImpellerDiameterMm: 119,
                FanTypeDescription: "119x119x38 mm kompaktblæser",
                EnergySavingsEstimate: "Op til 75% energibesparelse (forbrug falder fra typisk ~19W til ~4.5W)",
                KeyAdvantages: "Kompakt GreenTech EC-teknologi med universal 100-240V forsyning, væsentligt lavere egenopvarmning og forventet levetid på over 65.000 timer.",
                MigrationNotes: "100% identiske monteringshuller (104.8 mm hulafstand) og 119x119 mm rammemål."
            );
        }

        if (clean.StartsWith("W2S130", StringComparison.OrdinalIgnoreCase))
        {
            return new EcReplacementRecommendation(
                LegacyAcModel: product.RawCode,
                RecommendedEcSeries: "W1G130 GreenTech EC",
                RecommendedEcModelPrefix: "W1G130",
                ImpellerDiameterMm: 130,
                FanTypeDescription: "Ø130 mm aksialblæser i vægring",
                EnergySavingsEstimate: "Ca. 50-60% energibesparelse i forhold til AC-spaltpolsmotor",
                KeyAdvantages: "Trinløs styring, markant lavere vibrationer og højere virkningsgrad.",
                MigrationNotes: "Direkte mekanisk udskiftning i samme monteringsflange."
            );
        }

        // Standard series
        char firstChar = char.ToUpperInvariant(clean[0]);
        string diameterStr = diameter?.ToString() ?? "XXX";

        string ecPrefix;
        string ecSeries;
        string fanDesc;

        switch (firstChar)
        {
            case 'A':
                ecPrefix = $"A3G{diameterStr}";
                ecSeries = $"A3G (Ø{diameterStr} mm GreenTech EC aksial grundmodel)";
                fanDesc = $"Aksialventilator grundmodel (Ø{diameterStr} mm)";
                break;
            case 'S':
                ecPrefix = $"S3G{diameterStr}";
                ecSeries = $"S3G (Ø{diameterStr} mm GreenTech EC aksial med beskyttelsesgitter)";
                fanDesc = $"Aksialventilator med gitter (Ø{diameterStr} mm)";
                break;
            case 'W':
                ecPrefix = $"W3G{diameterStr}";
                ecSeries = $"W3G (Ø{diameterStr} mm GreenTech EC aksial i vægring)";
                fanDesc = $"Aksialventilator i vægring (Ø{diameterStr} mm)";
                break;
            case 'R':
                ecPrefix = $"R3G{diameterStr}";
                ecSeries = $"R3G / RadiPac K3G{diameterStr} (GreenTech EC centrifugal)";
                fanDesc = $"Centrifugalventilator bagudkrummet (Ø{diameterStr} mm)";
                break;
            case 'G':
            case 'D':
                ecPrefix = $"G3G{diameterStr} / D3G{diameterStr}";
                ecSeries = $"G3G / D3G (GreenTech EC centrifugal i hus)";
                fanDesc = $"Centrifugalventilator i sneglehus (Ø{diameterStr} mm)";
                break;
            default:
                ecPrefix = $"3G{diameterStr}";
                ecSeries = $"3G{diameterStr} GreenTech EC";
                fanDesc = product.FanTypeDescription;
                break;
        }

        string airflowNote = string.Empty;
        if (product.FanFamily == FanFamilyType.Axial)
        {
            if (product.AirflowDirection == AirflowDirection.A)
            {
                airflowNote = " Husk at vælge et LIGE slutciffer (f.eks. ..02 / ..04) på EC-modellen for at bevare Luftretning A.";
            }
            else if (product.AirflowDirection == AirflowDirection.V)
            {
                airflowNote = " Husk at vælge et ULIGE slutciffer (f.eks. ..01 / ..03) på EC-modellen for at bevare Luftretning V.";
            }
        }

        return new EcReplacementRecommendation(
            LegacyAcModel: product.RawCode,
            RecommendedEcSeries: ecSeries,
            RecommendedEcModelPrefix: ecPrefix,
            ImpellerDiameterMm: diameter,
            FanTypeDescription: fanDesc,
            EnergySavingsEstimate: "Typisk 30-50% energibesparelse ved fuld last (og op til 70% ved dellast via trinløs styring)",
            KeyAdvantages: "Integreret elektronisk kommutering (EC), trinløs 0-10V / PWM / Modbus RTU hastighedsregulering, væsentligt lavere støjniveau ved dellast og minimal varmeudvikling.",
            MigrationNotes: $"Identiske monterings- og kapslingsmål (samme Ø{diameterStr} mm geometri).{airflowNote}"
        );
    }

    public static List<EcReplacementRecommendation> FindEcReplacements(IEnumerable<EbmProductInfo> products)
    {
        var list = new List<EcReplacementRecommendation>();
        if (products == null) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products)
        {
            if (seen.Add(p.CleanCode))
            {
                var rec = GetEcReplacement(p);
                if (rec != null)
                {
                    list.Add(rec);
                }
            }
        }
        return list;
    }

    public static string FormatRecommendationMarkdown(EcReplacementRecommendation rec)
    {
        return $"""
            ### 💡 Moderne EC-erstatning findes (GreenTech EC)
            * **Nuværende AC-model**: `{rec.LegacyAcModel}` ({rec.FanTypeDescription})
            * **Anbefalet GreenTech EC-serie**: `{rec.RecommendedEcSeries}` (`{rec.RecommendedEcModelPrefix}...`)
            * **Forventet energibesparelse**: **{rec.EnergySavingsEstimate}**
            * **Nøglefordele**: {rec.KeyAdvantages}
            * **Mekanisk kompatibilitet**: {rec.MigrationNotes}
            """;
    }
}
