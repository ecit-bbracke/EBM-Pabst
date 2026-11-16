using System;
using System.Text.RegularExpressions;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public record TechnicalMetadataResult(
    string? Phase,
    string? NominalVoltage,
    string? VoltageRange,
    string? ProductCode,
    string? ProductCategory,
    int? PowerWatts = null,
    int? DiameterMm = null,
    int? MinVoltage = null,
    int? MaxVoltage = null
);

public static class TechnicalMetadataExtractor
{
    private static readonly Regex PhaseRegex = new(
        @"(?:Phase|Fase)\s*[:\s]?\s*(3~|1~|3\s*phase|1\s*phase|3\s*faset|1\s*faset|DC)(?!\w)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex NominalVoltageTableRegex = new(
        @"(?:Nominal voltage|Nominel spænding|Nennspannung)\s*(?:VAC|VDC|V)?\s*[:\s]?\s*(\d{2,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex CompactNominalVoltageRegex = new(
        @"Nominal voltage\s*U\s*N\s*(\d{2,3})\s*V\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex VoltageRangeTableRegex = new(
        @"(?:Nominal voltage range|Nominelt spændingsområde|Nennspannungsbereich)\s*(?:VAC|VDC|V)?\s*[:\s]?\s*(\d{2,3}\s*(?:\.\.|\-)\s*\d{2,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex CompactVoltageRangeRegex = new(
        @"Voltage range\s*U\s*(\d{2,3})\s*V\s*(\d{2,3})\s*V\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex PowerConsumptionRegex = new(
        @"(?:Power consumption|Effektoptag|Leistungsaufnahme)\s*(?:Ped)?\s*(?:W)?\s*[:\s]?\s*(\d{2,5})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex CompactPowerRegex = new(
        @"Power consumption\s*(\d{1,4})\s*W\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex RadiPacTypeRegex = new(
        @"V[A-Z]{2}0?([1-9][0-9]{2})([A-Z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex DiameterTableRegex = new(
        @"(?:Diameter|Laufraddurchmesser)\s*[:\s]?\s*(?:Ø)?\s*(\d{3})\s*mm\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public static TechnicalMetadataResult Extract(string? text, string? fileName, EbmProductInfo? productInfo = null)
    {
        string? phase = null;
        string? nominalVoltage = null;
        string? voltageRange = null;
        int? powerWatts = null;
        int? diameter = productInfo?.ImpellerDiameterMm;

        var sampleText = string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : (text.Length > 8000 ? text.Substring(0, 8000) : text);

        var combinedSample = $"{sampleText} {fileName}";

        // 1. Phase extraction
        var phaseMatch = PhaseRegex.Match(sampleText);
        if (phaseMatch.Success)
        {
            var val = phaseMatch.Groups[1].Value.Trim();
            if (val.Contains("3") || val.Equals("3~", StringComparison.OrdinalIgnoreCase)) phase = "3~";
            else if (val.Contains("1") || val.Equals("1~", StringComparison.OrdinalIgnoreCase)) phase = "1~";
            else if (val.Equals("DC", StringComparison.OrdinalIgnoreCase)) phase = "DC";
        }

        // If product is known AC motor, typenøgle (E = 1~, D = 3~) is the authoritative ground truth
        if (productInfo != null && productInfo.Technology == MotorTechnology.AC)
        {
            if (productInfo.PhaseDescription?.Contains("1-faset", StringComparison.OrdinalIgnoreCase) == true)
            {
                phase = "1~";
            }
            else if (productInfo.PhaseDescription?.Contains("3-faset", StringComparison.OrdinalIgnoreCase) == true)
            {
                phase = "3~";
            }
        }

        // 2. Nominal voltage extraction
        var voltMatch = NominalVoltageTableRegex.Match(sampleText);
        if (voltMatch.Success && int.TryParse(voltMatch.Groups[1].Value, out var v))
        {
            var suffix = (phase == "DC" || sampleText.Contains("VDC")) ? "VDC" : "VAC";
            nominalVoltage = $"{v} {suffix}";
        }
        else
        {
            var compactVolt = CompactNominalVoltageRegex.Match(sampleText);
            if (compactVolt.Success && int.TryParse(compactVolt.Groups[1].Value, out var cv))
            {
                phase ??= "DC";
                nominalVoltage = $"{cv} VDC";
            }
        }

        // 3. Voltage range extraction
        var rangeMatch = VoltageRangeTableRegex.Match(sampleText);
        if (rangeMatch.Success)
        {
            var rawRange = rangeMatch.Groups[1].Value.Replace("-", "..").Trim();
            var parts = rawRange.Split(new[] { ".." }, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var rMin) && int.TryParse(parts[1], out var rMax))
            {
                var suffix = (phase == "DC" || sampleText.Contains("VDC")) ? "VDC" : "VAC";
                voltageRange = $"{rMin} .. {rMax} {suffix}";
            }
        }
        else
        {
            var compactRange = CompactVoltageRangeRegex.Match(sampleText);
            if (compactRange.Success &&
                int.TryParse(compactRange.Groups[1].Value, out var cMin) &&
                int.TryParse(compactRange.Groups[2].Value, out var cMax))
            {
                phase ??= "DC";
                voltageRange = $"{cMin} .. {cMax} VDC";
            }
        }

        // 4. Power consumption (Watts) extraction
        var pMatch = PowerConsumptionRegex.Match(sampleText);
        if (pMatch.Success && int.TryParse(pMatch.Groups[1].Value, out var pw))
        {
            powerWatts = pw;
        }
        else
        {
            var cpMatch = CompactPowerRegex.Match(sampleText);
            if (cpMatch.Success && int.TryParse(cpMatch.Groups[1].Value, out var cpw))
            {
                powerWatts = cpw;
            }
        }

        // 5. Generic RadiPac V-series secondary designation decoding (e.g. VBH0280SSPFS, VWT0400CTPFS, VBH0280CSPFS)
        var rpMatch = RadiPacTypeRegex.Match(combinedSample);
        if (rpMatch.Success)
        {
            if (!diameter.HasValue && int.TryParse(rpMatch.Groups[1].Value, out var d))
            {
                diameter = d;
            }
            if (string.IsNullOrWhiteSpace(phase) || string.IsNullOrWhiteSpace(nominalVoltage))
            {
                var suffix = rpMatch.Groups[2].Value.ToUpperInvariant();
                if (suffix.Contains('T'))
                {
                    phase ??= "3~";
                    nominalVoltage ??= "400 VAC";
                }
                else if (suffix.Contains('S'))
                {
                    phase ??= "1~";
                    nominalVoltage ??= "230 VAC";
                }
                else if (suffix.Contains('C'))
                {
                    phase ??= "3~";
                    nominalVoltage ??= "230 VAC";
                    voltageRange ??= "200 .. 240 VAC";
                }
            }
        }

        // 6. Diameter from table if still missing
        if (!diameter.HasValue)
        {
            var dtMatch = DiameterTableRegex.Match(sampleText);
            if (dtMatch.Success && int.TryParse(dtMatch.Groups[1].Value, out var dmm))
            {
                diameter = dmm;
            }
        }

        // 7. Typenøgle fallback if not found in text
        if (productInfo != null)
        {
            diameter ??= productInfo.ImpellerDiameterMm;

            if (string.IsNullOrWhiteSpace(phase))
            {
                if (productInfo.Technology == MotorTechnology.DC || productInfo.FanFamily == FanFamilyType.CompactOrSpecial)
                {
                    phase = "DC";
                }
                else if (productInfo.PhaseDescription?.Contains("3-faset", StringComparison.OrdinalIgnoreCase) == true)
                {
                    phase = "3~";
                }
                else if (productInfo.PhaseDescription?.Contains("1-faset", StringComparison.OrdinalIgnoreCase) == true)
                {
                    phase = "1~";
                }
            }

            if (string.IsNullOrWhiteSpace(nominalVoltage))
            {
                if (productInfo.PhaseDescription?.Contains("3-faset", StringComparison.OrdinalIgnoreCase) == true)
                {
                    nominalVoltage = "400 VAC";
                }
                else if (productInfo.PhaseDescription?.Contains("1-faset", StringComparison.OrdinalIgnoreCase) == true)
                {
                    nominalVoltage = "230 VAC";
                }
                else if (productInfo.CleanCode.StartsWith("4114", StringComparison.OrdinalIgnoreCase) ||
                         productInfo.CleanCode.StartsWith("6314", StringComparison.OrdinalIgnoreCase))
                {
                    nominalVoltage = "24 VDC";
                }
                else if (productInfo.CleanCode.StartsWith("6318", StringComparison.OrdinalIgnoreCase) ||
                         productInfo.CleanCode.StartsWith("3258", StringComparison.OrdinalIgnoreCase))
                {
                    nominalVoltage = "48 VDC";
                }
            }
        }

        var productCode = productInfo?.CleanCode;
        var productCategory = productInfo?.FanFamily.ToString();
        var (minVoltage, maxVoltage) = ExtractVoltageBounds(nominalVoltage, voltageRange);

        return new TechnicalMetadataResult(
            Phase: phase,
            NominalVoltage: nominalVoltage,
            VoltageRange: voltageRange,
            ProductCode: productCode,
            ProductCategory: productCategory,
            PowerWatts: powerWatts,
            DiameterMm: diameter,
            MinVoltage: minVoltage,
            MaxVoltage: maxVoltage
        );
    }

    public static (int? MinVoltage, int? MaxVoltage) ExtractVoltageBounds(string? nominalVoltage, string? voltageRange)
    {
        int? min = null;
        int? max = null;

        if (!string.IsNullOrWhiteSpace(voltageRange))
        {
            var m = Regex.Match(voltageRange, @"(\d{2,3})\s*(?:\.\.|\-)\s*(\d{2,3})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var rMin) && int.TryParse(m.Groups[2].Value, out var rMax))
            {
                min = rMin;
                max = rMax;
            }
        }

        if (!string.IsNullOrWhiteSpace(nominalVoltage))
        {
            var m = Regex.Match(nominalVoltage, @"\b(\d{2,3})\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var nom))
            {
                min = min.HasValue ? Math.Min(min.Value, nom) : nom;
                max = max.HasValue ? Math.Max(max.Value, nom) : nom;
            }
        }

        return (min, max);
    }
}
