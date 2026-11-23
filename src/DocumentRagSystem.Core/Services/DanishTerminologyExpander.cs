using System;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentRagSystem.Core.Services;

/// <summary>
/// Service responsible for translating Danish industry expressions and HVAC/electrical jargon
/// (e.g. "pillefyr", "røggasblæser", "tavleblæser", "sugende/trykkende", "kraftstrøm")
/// into official ebm-papst German and English catalog and datasheet terminology.
/// </summary>
public static class DanishTerminologyExpander
{
    // 1. Applications & specialized equipment
    private static readonly Regex PelletStoveFlueGasRegex = new(
        @"\b(?:pillefyr[et]?|stokerfyr[et]?|træpillefyr[et]?|røggasblæser[e]?|røgsuger[e]?|røggasventilator[er]?|kedelblæser[e]?|forbrændingsblæser[e]?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex CabinetCoolingRegex = new(
        @"\b(?:tavlekøling|tavleblæser[e]?|tavleventilator[er]?|styretavle[r]?|automatikskab[et]?|el-tavle[r]?|eltavle[r]?|skabsblæser[e]?|tavlefilter)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex CookerHoodRegex = new(
        @"\b(?:emhætte[n]?|emhætteblæser[e]?|emhættemotor[er]?|emfang[et]?|køkkenudsugning)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex RefrigerationRegex = new(
        @"\b(?:kølemøbel|kølemøbler|flaskekøler[e]?|køledisk[e]?|kølediskene|fordamperblæser[e]?|fordamper|kølerumsblæser[e]?|kølerum)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex EsmMotorRegex = new(
        @"\b(?:esm(?:\s*(?:motor[er]?|blæser[e]?|produkt[er]?|serien|serie))?|energy\s*saving\s*motor)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex HeatRecoveryAhuRegex = new(
        @"\b(?:varmegenvinding|genveks|ventilationsaggregat[er]?|aggregatblæser[e]?|aggregat|boligventilation|villaventilation)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex DuctFanRegex = new(
        @"\b(?:kanalblæser[e]?|rørblæser[e]?|kanalventilator[er]?|rørventilator[er]?|spirorør)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex AgricultureRegex = new(
        @"\b(?:staldventilation|staldblæser[e]?|staldventilator[er]?|stald)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex DustFumeExtractionRegex = new(
        @"\b(?:spånsug|punktudsugning|punktudsug|støvsugning|svejseudsugning)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // 2. Airflow directions & mechanical mounting
    private static readonly Regex SuctionAirflowRegex = new(
        @"\b(?:sugende(?:\s*(?:blæser[e]?|ventilator[er]?|aksialblæser[e]?|retning|luftretning))?|sugeblæser[e]?|sugeluft)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex BlowingAirflowRegex = new(
        @"\b(?:trykkende(?:\s*(?:blæser[e]?|ventilator[er]?|aksialblæser[e]?|retning|luftretning))?|blæsende(?:\s*(?:blæser[e]?|ventilator[er]?|aksialblæser[e]?|retning|luftretning))?|trykblæser[e]?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex OppositeAirflowRegex = new(
        @"\b(?:modsat(?:\s*(?:luft)?retning)?|omvendt(?:\s*(?:luft)?retning)?|anden\s*luftretning|opposite\s*airflow|reversed\s*airflow)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex WallRingMountingRegex = new(
        @"\b(?:vægramme[r]?|vægring[e]?|vægplade[r]?|vægmontering|i\s*væg)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex GuardGrilleRegex = new(
        @"\b(?:beskyttelsesgitter|kurvegitter|trådgitter|med\s*gitter|berøringsgitter)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex BareFanWithoutGrilleRegex = new(
        @"\b(?:uden\s*gitter|uden\s*ramme|ren\s*vinge|ren\s*motor|løs\s*blæservinge)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex InletNozzleRegex = new(
        @"\b(?:indløbsdyse[r]?|indløbsring[e]?|indløbsflange[r]?|dyse|indsugningsflange)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // 3. Danish electrical jargon
    private static readonly Regex PowerCurrent400VRegex = new(
        @"\b(?:kraftstrøm|stærkstrøm|400\s*volt\s*kraft)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex Mains230VRegex = new(
        @"\b(?:lysnet|almindelig\s*stikkontakt|230\s*volt\s*lysnet)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex ControlVoltageDcRegex = new(
        @"\b(?:styrespænding|svagstrøm)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex SteplessSpeedControlRegex = new(
        @"\b(?:trinløs(?:\s*(?:regulering|styring|hastighedsstyring|omdrejningsregulering))?|hastighedsregulering|omdrejningsregulering)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Expands the input query with equivalent English/German technical terms and model series
    /// when Danish HVAC/electrical expressions are detected.
    /// </summary>
    public static string Expand(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var sb = new StringBuilder(query.Trim());

        // 1. Applications
        if (PelletStoveFlueGasRegex.IsMatch(query))
        {
            sb.Append(" \"flue gas\" \"flue gas blower\" \"exhaust blower\" \"R2E\" \"R2E120\" \"R2E150\" \"G2E\"");
        }

        if (CabinetCoolingRegex.IsMatch(query))
        {
            sb.Append(" \"control cabinet\" \"filter fan\" \"cabinet cooling\" \"compact fan\" \"Kompaktlüfter\"");
        }

        if (CookerHoodRegex.IsMatch(query))
        {
            sb.Append(" \"range hood\" \"cooker hood\" \"D2E\" \"K3G\" \"centrifugal\"");
        }

        if (RefrigerationRegex.IsMatch(query))
        {
            sb.Append(" \"refrigeration\" \"evaporator\" \"bottle cooler\" \"W1G\" \"iQ-motor\" \"ESM\"");
        }

        if (EsmMotorRegex.IsMatch(query))
        {
            sb.Append(" \"ESM\" \"Energy Saving Motor\" \"W1G\" \"speed 1\" \"speed 2\" \"500 min-1\" \"2300 min-1\" \"refrigeration\"");
        }

        if (HeatRecoveryAhuRegex.IsMatch(query))
        {
            sb.Append(" \"heat recovery\" \"air handling unit\" \"AHU\" \"HRV\" \"RadiPac\" \"K3G\" \"R3G\"");
        }

        if (DuctFanRegex.IsMatch(query))
        {
            sb.Append(" \"duct fan\" \"in-line duct fan\" \"circular duct fan\" \"centrifugal\"");
        }

        if (AgricultureRegex.IsMatch(query))
        {
            sb.Append(" \"agricultural\" \"barn ventilation\" \"livestock\" \"A3G\" \"A4E\" \"axial\"");
        }

        if (DustFumeExtractionRegex.IsMatch(query))
        {
            sb.Append(" \"fume extraction\" \"dust extraction\" \"high pressure\" \"G2E\" \"G3G\"");
        }

        // 2. Airflow direction & Mounting
        if (SuctionAirflowRegex.IsMatch(query))
        {
            sb.Append(" \"airflow direction V\" \"Direction V\" \"Luftrichtung V\" \"suck through\"");
        }

        if (BlowingAirflowRegex.IsMatch(query))
        {
            sb.Append(" \"airflow direction A\" \"Direction A\" \"Luftrichtung A\" \"blow over\"");
        }

        if (OppositeAirflowRegex.IsMatch(query))
        {
            sb.Append(" \"Direction of air flow\" \"Air flow direction\" \"Direction A\" \"Direction V\" \"Transportretning\" \"Luftretning\"");
        }

        if (WallRingMountingRegex.IsMatch(query))
        {
            sb.Append(" \"wall ring\" \"W-series\" \"W3G\" \"W4E\" \"W2E\" \"square wall plate\"");
        }

        if (GuardGrilleRegex.IsMatch(query))
        {
            sb.Append(" \"guard grille\" \"basket guard\" \"S-series\" \"S3G\" \"S4E\" \"S2E\"");
        }

        if (BareFanWithoutGrilleRegex.IsMatch(query))
        {
            sb.Append(" \"A-series\" \"A3G\" \"A4E\" \"A2E\" \"basic fan\"");
        }

        if (InletNozzleRegex.IsMatch(query))
        {
            sb.Append(" \"inlet ring\" \"inlet nozzle\" \"inlet cone\"");
        }

        // 3. Electrical & Control jargon
        if (PowerCurrent400VRegex.IsMatch(query))
        {
            sb.Append(" \"Phase 3~\" \"3~\" \"3-phase\" \"400 VAC\" \"380 .. 480\"");
        }

        if (Mains230VRegex.IsMatch(query))
        {
            sb.Append(" \"Phase 1~\" \"1~\" \"1-phase\" \"230 VAC\" \"200 .. 240\"");
        }

        if (ControlVoltageDcRegex.IsMatch(query))
        {
            sb.Append(" \"24 VDC\" \"12 VDC\" \"DC\"");
        }

        if (SteplessSpeedControlRegex.IsMatch(query))
        {
            sb.Append(" \"0-10 V\" \"PWM\" \"stepless\" \"speed control\" \"EC motor\"");
        }

        return sb.ToString();
    }
}
