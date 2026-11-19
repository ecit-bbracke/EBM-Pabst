using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentRagSystem.Core.Services;

public record ElectricalFilter(
    string? RequiredPhase,      // e.g. "3~", "1~", "DC"
    int? TargetVoltage,         // e.g. 230, 400, 24, 48
    string? VoltageType,        // "VAC", "VDC"
    string RawMatch
)
{
    public bool Matches(string? phase, string? nominalVoltage, string? voltageRange)
    {
        // 1. Phase check: If a specific phase is required (e.g. "3~" or "1~" or "DC"), candidate MUST explicitly match that phase!
        if (!string.IsNullOrWhiteSpace(RequiredPhase))
        {
            if (string.IsNullOrWhiteSpace(phase))
            {
                return false;
            }

            var pNorm = NormalizePhase(phase);
            var reqNorm = NormalizePhase(RequiredPhase);
            if (!string.Equals(pNorm, reqNorm, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 2. Voltage check: If a target voltage is required (e.g. 230V or 400V or 24V), candidate MUST match nominal voltage or range!
        if (TargetVoltage.HasValue)
        {
            var target = TargetVoltage.Value;
            bool voltageMatched = false;

            // Check nominal voltage match (e.g. "230 VAC" matches 230)
            if (!string.IsNullOrWhiteSpace(nominalVoltage))
            {
                var nums = Regex.Matches(nominalVoltage, @"\b(\d{2,3})\b");
                foreach (Match m in nums)
                {
                    if (int.TryParse(m.Value, out var v) && v == target)
                    {
                        voltageMatched = true;
                        break;
                    }
                }
            }

            // Check voltage range match (e.g. "200 .. 240 VAC" covers 230)
            if (!voltageMatched && !string.IsNullOrWhiteSpace(voltageRange))
            {
                var rangeMatch = Regex.Match(voltageRange, @"(\d{2,3})\s*(?:\.\.|\-)\s*(\d{2,3})");
                if (rangeMatch.Success &&
                    int.TryParse(rangeMatch.Groups[1].Value, out var min) &&
                    int.TryParse(rangeMatch.Groups[2].Value, out var max))
                {
                    if (target >= min && target <= max)
                    {
                        voltageMatched = true;
                    }
                }
            }

            if (!voltageMatched)
            {
                return false;
            }
        }

        return true;
    }

    public static string NormalizePhase(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;
        var clean = p.Trim().ToLowerInvariant();
        if (clean.Contains("3~") || clean.Contains("3-faset") || clean.Contains("3 fase") || clean.Contains("3-phase") || clean.Contains("dreiphasen") || clean.Contains("3 phase") || clean.Contains("3 ~") || clean.Contains("3x") || clean.Contains("3 x"))
            return "3~";
        if (clean.Contains("1~") || clean.Contains("1-faset") || clean.Contains("1 fase") || clean.Contains("1-phase") || clean.Contains("einphasen") || clean.Contains("1 phase") || clean.Contains("1 ~") || clean.Contains("1x") || clean.Contains("1 x"))
            return "1~";
        if (clean.Contains("dc"))
            return "DC";
        return p.Trim();
    }
}

public static class ElectricalQueryExpander
{
    private static readonly Regex ThreePhase230Regex = new(
        @"\b(?:3\s*[\*xX~]\s*2(?:00|08|30|40)(?:\s*(?:v(?:ac)?|volt))?|(?:3\s*[- ]?(?:fase[rt]?|phase[sd]?|phasig[en]?|phasen)|three[- ]phase|trefase[rt]?|dreiphas(?:ig[en]?|en)|3~)\s*(?:with\s+|med\s+|mit\s+)?2(?:00|08|30|40)(?:\s*(?:v(?:ac)?|volt))?|2(?:00|08|30|40)\s*v(?:ac)?\s*(?:3\s*[- ]?(?:fase[rt]?|phase[sd]?|phasig[en]?|phasen)|3~|three[- ]phase))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex ThreePhase400Regex = new(
        @"\b(?:3\s*[\*xX~]\s*4(?:00|15|60|80)(?:\s*(?:v(?:ac)?|volt))?|(?:3\s*[- ]?(?:fase[rt]?|phase[sd]?|phasig[en]?|phasen)|three[- ]phase|trefase[rt]?|dreiphas(?:ig[en]?|en)|3~)\s*(?:with\s+|med\s+|mit\s+)?4(?:00|15|60|80)(?:\s*(?:v(?:ac)?|volt))?|4(?:00|15|60|80)\s*v(?:ac)?\s*(?:3\s*[- ]?(?:fase[rt]?|phase[sd]?|phasig[en]?|phasen)|3~|three[- ]phase))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex SinglePhase230Regex = new(
        @"\b(?:1\s*[\*xX~]\s*230(?:\s*(?:v(?:ac)?|volt))?|(?:1\s*[- ]?(?:fase[rt]?|phase[sd]?|phasig[en]?|phasen)|single[- ]phase|one[- ]phase|enkeltfase[rt]?|einphas(?:ig[en]?|en)|1~)\s*(?:with\s+|med\s+|mit\s+)?230(?:\s*(?:v(?:ac)?|volt))?|230\s*v(?:ac)?\s*(?:1\s*[- ]?(?:fase[rt]?|phase[sd]?|phasig[en]?|phasen)|1~|single[- ]phase))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex GenericThreePhaseRegex = new(
        @"\b(?:3\s*[- ]?(?:fase[rt]?|phase[sd]?|phasig[en]?|phasen)|three[- ]phase|trefase[rt]?|dreiphas(?:ig[en]?|en)|3~)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex Generic230VRegex = new(
        @"\b(?:230\s*v(?:ac)?|230\s*volt)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex Generic400VRegex = new(
        @"\b(?:400\s*v(?:ac)?|400\s*volt)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex GenericSinglePhaseRegex = new(
        @"\b(?:1\s*[- ]?(?:fase[rt]?|phase[sd]?|phasig[en]?|phasen)|single[- ]phase|one[- ]phase|enkeltfase[rt]?|einphas(?:ig[en]?|en)|1~)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex Dc24Regex = new(
        @"\b(?:24\s*v(?:dc)?|24\s*volt\s*dc)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex Dc48Regex = new(
        @"\b(?:48\s*v(?:dc)?|48\s*volt\s*dc)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex GenericDcRegex = new(
        @"\b(?:dc\s*(?:blæser[e]?|ventilator[er]?|lüfter|fan[s]?|motor|forsyning|supply)?|jævnstrøm|gleichstrom|direct[- ]current)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public static string Expand(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var sb = new StringBuilder(query.Trim());

        bool expanded = false;

        if (ThreePhase230Regex.IsMatch(query))
        {
            sb.Append(" \"Phase 3~\" \"3~\" \"3-phase\" \"Dreiphasen\" \"230 VAC\" \"200 .. 240\" \"200-240\" \"200..240\" \"208\"");
            expanded = true;
        }

        if (ThreePhase400Regex.IsMatch(query) || Generic400VRegex.IsMatch(query))
        {
            sb.Append(" \"Phase 3~\" \"3~\" \"3-phase\" \"Dreiphasen\" \"400 VAC\" \"380 .. 480\" \"380-480\" \"380..480\"");
            expanded = true;
        }

        if (!expanded && GenericThreePhaseRegex.IsMatch(query))
        {
            sb.Append(" \"Phase 3~\" \"3~\" \"3-phase\" \"Dreiphasen\"");
            expanded = true;
        }

        if (SinglePhase230Regex.IsMatch(query) || (!ThreePhase230Regex.IsMatch(query) && Generic230VRegex.IsMatch(query)))
        {
            sb.Append(" \"Phase 1~\" \"1~\" \"1-phase\" \"Einphasen\" \"230 VAC\" \"200 .. 277\" \"200-277\" \"200..277\"");
            expanded = true;
        }

        if (!expanded && GenericSinglePhaseRegex.IsMatch(query))
        {
            sb.Append(" \"Phase 1~\" \"1~\" \"1-phase\" \"Einphasen\"");
            expanded = true;
        }

        if (Dc24Regex.IsMatch(query) && !expanded)
        {
            sb.Append(" \"24 VDC\" \"24V DC\" \"16 .. 30\" \"24 V\"");
            expanded = true;
        }

        if (Dc48Regex.IsMatch(query) && !expanded)
        {
            sb.Append(" \"48 VDC\" \"48V DC\" \"36 .. 60\" \"48 V\"");
            expanded = true;
        }

        if (!expanded && GenericDcRegex.IsMatch(query))
        {
            sb.Append(" \"VDC\" \"DC\" \"Gleichstrom\"");
            expanded = true;
        }

        var expandedElectrical = sb.ToString();
        var expandedDimensional = DimensionalQueryExpander.Expand(query);
        var expandedUnits = EngineeringUnitsQueryExpander.Expand(query);
        var expandedDanish = DanishTerminologyExpander.Expand(query);
        var expandedGerman = GermanTerminologyExpander.Expand(query);

        var finalResult = new StringBuilder(expandedElectrical);

        if (!string.Equals(expandedDimensional, query.Trim(), StringComparison.Ordinal))
        {
            var dimensionalAdditions = expandedDimensional.Substring(query.Trim().Length);
            finalResult.Append(dimensionalAdditions);
        }

        if (!string.Equals(expandedUnits, query.Trim(), StringComparison.Ordinal))
        {
            var unitAdditions = expandedUnits.Substring(query.Trim().Length);
            finalResult.Append(unitAdditions);
        }

        if (!string.Equals(expandedDanish, query.Trim(), StringComparison.Ordinal))
        {
            var danishAdditions = expandedDanish.Substring(query.Trim().Length);
            finalResult.Append(danishAdditions);
        }

        if (!string.Equals(expandedGerman, query.Trim(), StringComparison.Ordinal))
        {
            var germanAdditions = expandedGerman.Substring(query.Trim().Length);
            finalResult.Append(germanAdditions);
        }

        return finalResult.ToString();
    }

    public static ElectricalFilter? ExtractVoltageFilter(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var m3_230 = ThreePhase230Regex.Match(query);
        if (m3_230.Success)
        {
            return new ElectricalFilter("3~", 230, "VAC", m3_230.Value);
        }

        var m3_400 = ThreePhase400Regex.Match(query);
        if (m3_400.Success)
        {
            return new ElectricalFilter("3~", 400, "VAC", m3_400.Value);
        }

        var m3 = GenericThreePhaseRegex.Match(query);
        if (m3.Success)
        {
            return new ElectricalFilter("3~", null, "VAC", m3.Value);
        }

        var m1_230 = SinglePhase230Regex.Match(query);
        if (m1_230.Success)
        {
            return new ElectricalFilter("1~", 230, "VAC", m1_230.Value);
        }

        var m_gen230 = Generic230VRegex.Match(query);
        if (m_gen230.Success)
        {
            return new ElectricalFilter("1~", 230, "VAC", m_gen230.Value);
        }

        var m_gen400 = Generic400VRegex.Match(query);
        if (m_gen400.Success)
        {
            return new ElectricalFilter("3~", 400, "VAC", m_gen400.Value);
        }

        var m1 = GenericSinglePhaseRegex.Match(query);
        if (m1.Success)
        {
            return new ElectricalFilter("1~", null, "VAC", m1.Value);
        }

        var m24 = Dc24Regex.Match(query);
        if (m24.Success)
        {
            return new ElectricalFilter("DC", 24, "VDC", m24.Value);
        }

        var m48 = Dc48Regex.Match(query);
        if (m48.Success)
        {
            return new ElectricalFilter("DC", 48, "VDC", m48.Value);
        }

        var mDc = GenericDcRegex.Match(query);
        if (mDc.Success)
        {
            return new ElectricalFilter("DC", null, "VDC", mDc.Value);
        }

        return null;
    }
}
