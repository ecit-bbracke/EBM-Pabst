using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentRagSystem.Core.Services;

/// <summary>
/// Service responsible for parsing, converting, and expanding engineering units and operational
/// parameters (e.g. Airflow CFM / l/s -> m³/h, Pressure mmVS / in.wg -> Pa, 12V DC, IP protection, control signals, acoustics).
/// </summary>
public static class EngineeringUnitsQueryExpander
{
    private static readonly Regex CfmRegex = new(
        @"\b(\d+(?:[.,]\d+)?)\s*(?:cfm|cubic\s*feet(?:\s*per\s*minute)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex LitersPerSecRegex = new(
        @"\b(\d+(?:[.,]\d+)?)\s*(?:l\/s|liter(?:\s*i\s*sekundet|\/sek(?:und)?)|litres?(?:\s*per\s*second)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex MmVsRegex = new(
        @"\b(\d+(?:[.,]\d+)?)\s*(?:mmvs|mm\s*vs|mm\s*h2o|mmh2o|millimeter\s*vandsøjle)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex InWgRegex = new(
        @"\b(\d+(?:[.,]\d+)?)\s*(?:in\.?\s*wg|in\.?\s*h2o|inches?\s*(?:of\s*)?water(?:\s*gauge)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex Dc12Regex = new(
        @"\b(?:12\s*v(?:dc)?|12\s*volt\s*dc)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex HarshEnvironmentRegex = new(
        @"\b(?:fugt(?:igt)?(?:\s*miljø)?|vådt(?:\s*miljø)?|vandtæt|vandafvisende|støvtæt|vaskbar|udendørs|outdoor|washdown|harsh\s*environment|moisture\s*protection|fugtbeskyttelse)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex PwmControlRegex = new(
        @"\b(?:pwm(?:\s*(?:styring|signal|control|hastighedsstyring))?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex AnalogControlRegex = new(
        @"\b(?:0\s*-\s*10\s*v(?:dc)?|0\s*til\s*10\s*v(?:olt)?|analog\s*(?:signal|styring|control))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex TachoSignalRegex = new(
        @"\b(?:tacho(?:\s*signal|\s*output)?|hallsensor|hall\s*sensor|hastighedssignal|speed\s*signal|omdrejningssignal)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex AcousticsRegex = new(
        @"\b(?:støjsvag[e]?|lydløs[e]?|stille|lydniveau|støjniveau|low\s*noise|quiet|silent|støj)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Multilingual Parameter Specification / Modbus register mapping (Danish, English, German)
    private static readonly Regex ModbusParameterSpecRegex = new(
        @"\b(?:parameter\s*(?:specifikation(?:er)?|specification(?:s)?|beschreibung|liste|list)|modbus\s*parameter[s]?|register\s*(?:mapping|oversigt|liste|tabelle|belegung))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Expands the input query with equivalent technical terms and converted units.
    /// E.g. "100 CFM" -> "170 m³/h", "10 mmVS" -> "98 Pa", "PWM" -> "speed control".
    /// </summary>
    public static string Expand(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var sb = new StringBuilder(query.Trim());

        // 1. Airflow: CFM -> m³/h (1 CFM ≈ 1.699 m³/h)
        var cfmMatch = CfmRegex.Match(query);
        if (cfmMatch.Success && TryParseDouble(cfmMatch.Groups[1].Value, out var cfmVal))
        {
            var m3h = Math.Round(cfmVal * 1.69901);
            sb.Append($" \"{m3h} m³/h\" \"{m3h} m3/h\" \"m³/h\" \"CFM\"");
        }

        // 2. Airflow: l/s -> m³/h (1 l/s = 3.6 m³/h)
        var lsMatch = LitersPerSecRegex.Match(query);
        if (lsMatch.Success && TryParseDouble(lsMatch.Groups[1].Value, out var lsVal))
        {
            var m3h = Math.Round(lsVal * 3.6);
            sb.Append($" \"{m3h} m³/h\" \"{m3h} m3/h\" \"m³/h\"");
        }

        // 3. Pressure: mmVS / mm H2O -> Pa (1 mmVS ≈ 9.80665 Pa)
        var mmvsMatch = MmVsRegex.Match(query);
        if (mmvsMatch.Success && TryParseDouble(mmvsMatch.Groups[1].Value, out var mmvsVal))
        {
            var paExact = Math.Round(mmvsVal * 9.80665);
            var paApprox = Math.Round(mmvsVal * 10.0);
            sb.Append($" \"{paExact} Pa\" \"{paApprox} Pa\" \"Pa\" \"pfs\"");
        }

        // 4. Pressure: in. wg -> Pa (1 in. wg ≈ 249.0889 Pa)
        var inwgMatch = InWgRegex.Match(query);
        if (inwgMatch.Success && TryParseDouble(inwgMatch.Groups[1].Value, out var inwgVal))
        {
            var pa = Math.Round(inwgVal * 249.0889);
            sb.Append($" \"{pa} Pa\" \"Pa\" \"pfs\" \"in. wg\"");
        }

        // 5. Electrical 12 VDC
        if (Dc12Regex.IsMatch(query))
        {
            sb.Append(" \"12 VDC\" \"12V DC\" \"8 .. 15\" \"8-15\" \"12 V\"");
        }

        // 6. Harsh / moist environment & IP ratings
        if (HarshEnvironmentRegex.IsMatch(query))
        {
            sb.Append(" \"IP68\" \"IP55\" \"IP54\" \"moisture protection\"");
        }

        // 7. Control: PWM
        if (PwmControlRegex.IsMatch(query))
        {
            sb.Append(" \"PWM\" \"speed control\" \"control input\"");
        }

        // 8. Control: 0-10V
        if (AnalogControlRegex.IsMatch(query))
        {
            sb.Append(" \"0-10 V\" \"0-10V\" \"0 .. 10 VDC\" \"linear control input\"");
        }

        // 9. Control: Tacho / Speed output
        if (TachoSignalRegex.IsMatch(query))
        {
            sb.Append(" \"Tach output\" \"speed signal\" \"hall sensor\"");
        }

        // 10. Acoustics / Noise
        if (AcousticsRegex.IsMatch(query))
        {
            sb.Append(" \"dB(A)\" \"dBA\" \"sound pressure\"");
        }

        // 11. Multilingual Modbus Parameter Specification
        if (ModbusParameterSpecRegex.IsMatch(query))
        {
            sb.Append(" \"MODBUS\" \"Parameter specification\" \"Parameterbeschreibung\" \"Holding register\" \"RS-485\" \"software version\" \"SW\"");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts a CFM airflow value to m³/h.
    /// </summary>
    public static double ConvertCfmToM3H(double cfm) => Math.Round(cfm * 1.69901, 1);

    /// <summary>
    /// Converts an l/s airflow value to m³/h.
    /// </summary>
    public static double ConvertLitersPerSecToM3H(double ls) => Math.Round(ls * 3.6, 1);

    /// <summary>
    /// Converts a mmVS (mm H2O) pressure value to Pascal (Pa).
    /// </summary>
    public static double ConvertMmVsToPascal(double mmVs) => Math.Round(mmVs * 9.80665, 1);

    /// <summary>
    /// Converts an in. wg pressure value to Pascal (Pa).
    /// </summary>
    public static double ConvertInWgToPascal(double inWg) => Math.Round(inWg * 249.0889, 1);

    private static bool TryParseDouble(string raw, out double val)
    {
        var normalized = raw.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
    }
}
