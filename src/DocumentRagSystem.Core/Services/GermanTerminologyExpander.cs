using System;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentRagSystem.Core.Services;

/// <summary>
/// Service responsible for recognizing and expanding German technical terminology,
/// airflow directions, mounting configurations, and HVAC applications into standard
/// ebm-papst catalog and datasheet keywords.
/// </summary>
public static class GermanTerminologyExpander
{
    // 1. Applications & specialized equipment
    private static readonly Regex FlueGasGermanRegex = new(
        @"\b(?:rauchgasgebläse|abgasgebläse|rauchgaslüfter|pelletkessel|hackschnitzelkessel|kesselgebläse)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex CabinetCoolingGermanRegex = new(
        @"\b(?:schaltschrankkühlung|schaltschranklüfter|schaltschrankventilator|filterlüfter|gehäuselüfter)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex CookerHoodGermanRegex = new(
        @"\b(?:dunstabzugshaube|dunstabzug|küchenlüfter|abzugshaube)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex RefrigerationGermanRegex = new(
        @"\b(?:kühlmöbellüfter|verdampferlüfter|kühlregal|flaschenkühler|verdampferventilator)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex HeatRecoveryGermanRegex = new(
        @"\b(?:wärmerückgewinnung|lüftungsgerät|klimagerät|wohnungslüftung)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex DuctFanGermanRegex = new(
        @"\b(?:rohrventilator|kanalventilator|rohrlüfter|kanallüfter)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex AgricultureGermanRegex = new(
        @"\b(?:stalllüftung|stallventilator|agrarventilator)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // 2. Airflow directions & mechanical mounting
    private static readonly Regex SuctionAirflowGermanRegex = new(
        @"\b(?:saugend(?:e[rn]?)?|über\s+streben\s+saugend|über\s+rotor\s+saugend|ansaugend)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex BlowingAirflowGermanRegex = new(
        @"\b(?:blasend(?:e[rn]?)?|über\s+streben\s+blasend|über\s+rotor\s+blasend|ausblasend|druckseitig)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex OppositeAirflowGermanRegex = new(
        @"\b(?:umgekehrte[rn]?\s+luftrichtung|entgegengesetzte[rn]?\s+luftrichtung|gegensätzliche[rn]?\s+luftrichtung|andere[rn]?\s+luftrichtung|andere[rn]?\s+förderrichtung|umgekehrte[rn]?\s+förderrichtung)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex WallRingGermanRegex = new(
        @"\b(?:im\s+wandring|wandring|wandplatte|quadratische\s+wandplatte)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex GuardGrilleGermanRegex = new(
        @"\b(?:mit\s+schutzgitter|schutzgitter|korbschutzgitter|berührungsschutz)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex BareFanGermanRegex = new(
        @"\b(?:ohne\s+gitter|ohne\s+wandring|freiblasend|nur\s+laufrad|nur\s+motor)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex InletNozzleGermanRegex = new(
        @"\b(?:einströmdüse|düse|einströmring)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // 3. Electrical & Control jargon in German
    private static readonly Regex ThreePhaseGermanRegex = new(
        @"\b(?:drehstrom|starkstrom|dreiphasenwechselstrom)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex SinglePhaseGermanRegex = new(
        @"\b(?:lichtstrom|einphasenwechselstrom|netzspannung\s+230v)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex SteplessControlGermanRegex = new(
        @"\b(?:stufenlose[rn]?\s+(?:regelung|steuerung|drehzahlregelung)|drehzahlregelung|drehzahlsteuerung)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Expands the input query with equivalent technical terms and ebm-papst model series
    /// when German technical expressions are detected.
    /// </summary>
    public static string Expand(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var sb = new StringBuilder(query.Trim());

        // 1. Applications
        if (FlueGasGermanRegex.IsMatch(query))
        {
            sb.Append(" \"flue gas\" \"flue gas blower\" \"exhaust blower\" \"R2E\" \"R2E120\" \"R2E150\" \"G2E\"");
        }

        if (CabinetCoolingGermanRegex.IsMatch(query))
        {
            sb.Append(" \"control cabinet\" \"filter fan\" \"cabinet cooling\" \"compact fan\" \"Kompaktlüfter\"");
        }

        if (CookerHoodGermanRegex.IsMatch(query))
        {
            sb.Append(" \"range hood\" \"cooker hood\" \"D2E\" \"K3G\" \"centrifugal\"");
        }

        if (RefrigerationGermanRegex.IsMatch(query))
        {
            sb.Append(" \"refrigeration\" \"evaporator\" \"bottle cooler\" \"W1G\" \"iQ-motor\" \"ESM\"");
        }

        if (HeatRecoveryGermanRegex.IsMatch(query))
        {
            sb.Append(" \"heat recovery\" \"air handling unit\" \"AHU\" \"HRV\" \"RadiPac\" \"K3G\" \"R3G\"");
        }

        if (DuctFanGermanRegex.IsMatch(query))
        {
            sb.Append(" \"duct fan\" \"in-line duct fan\" \"circular duct fan\" \"centrifugal\"");
        }

        if (AgricultureGermanRegex.IsMatch(query))
        {
            sb.Append(" \"agricultural\" \"barn ventilation\" \"livestock\" \"A3G\" \"A4E\" \"axial\"");
        }

        // 2. Airflow directions & Mounting
        if (SuctionAirflowGermanRegex.IsMatch(query))
        {
            sb.Append(" \"Luftrichtung V\" \"Direction V\" \"airflow direction V\" \"saugend\"");
        }

        if (BlowingAirflowGermanRegex.IsMatch(query))
        {
            sb.Append(" \"Luftrichtung A\" \"Direction A\" \"airflow direction A\" \"blasend\"");
        }

        if (OppositeAirflowGermanRegex.IsMatch(query))
        {
            sb.Append(" \"Luftrichtung\" \"Direction of air flow\" \"Air flow direction\" \"Direction A\" \"Direction V\"");
        }

        if (WallRingGermanRegex.IsMatch(query))
        {
            sb.Append(" \"im Wandring\" \"W-series\" \"W3G\" \"W4E\" \"W2E\" \"wall ring\"");
        }

        if (GuardGrilleGermanRegex.IsMatch(query))
        {
            sb.Append(" \"Schutzgitter\" \"S-series\" \"S3G\" \"S4E\" \"S2E\" \"guard grille\"");
        }

        if (BareFanGermanRegex.IsMatch(query))
        {
            sb.Append(" \"A-series\" \"A3G\" \"A4E\" \"A2E\" \"basic fan\"");
        }

        if (InletNozzleGermanRegex.IsMatch(query))
        {
            sb.Append(" \"Einströmdüse\" \"inlet ring\" \"inlet nozzle\"");
        }

        // 3. Electrical & Control
        if (ThreePhaseGermanRegex.IsMatch(query))
        {
            sb.Append(" \"Phase 3~\" \"3~\" \"3-phase\" \"400 VAC\" \"Dreiphasen\"");
        }

        if (SinglePhaseGermanRegex.IsMatch(query))
        {
            sb.Append(" \"Phase 1~\" \"1~\" \"1-phase\" \"230 VAC\" \"Einphasen\"");
        }

        if (SteplessControlGermanRegex.IsMatch(query))
        {
            sb.Append(" \"0-10 V\" \"PWM\" \"stepless\" \"stufenlos\" \"EC motor\"");
        }

        return sb.ToString();
    }
}
