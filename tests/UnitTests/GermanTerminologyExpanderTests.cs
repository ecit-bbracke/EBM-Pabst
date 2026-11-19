using System;
using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class GermanTerminologyExpanderTests
{
    [Theory]
    [InlineData("Rauchgasgebläse für Pelletkessel", "flue gas", "R2E")]
    [InlineData("Schaltschranklüfter zur Schaltschrankkühlung", "control cabinet", "filter fan")]
    [InlineData("Dunstabzugshaube für Küche", "range hood", "D2E")]
    [InlineData("Verdampferlüfter für Kühlmöbel", "refrigeration", "W1G")]
    [InlineData("Lüftungsgerät mit Wärmerückgewinnung", "heat recovery", "AHU")]
    [InlineData("Rohrventilator für Lüftungskanal", "duct fan", "centrifugal")]
    [InlineData("Stalllüftung für Landwirtschaft", "agricultural", "barn ventilation")]
    public void Expand_Applications_ShouldEnrichGermanTermsWithTechnicalCatalogTerms(string query, string expected1, string expected2)
    {
        var expanded = GermanTerminologyExpander.Expand(query);

        Assert.NotEqual(query, expanded);
        Assert.Contains(expected1, expanded);
        Assert.Contains(expected2, expanded);
    }

    [Theory]
    [InlineData("über Streben saugend", "Luftrichtung V", "Direction V")]
    [InlineData("über Streben blasend", "Luftrichtung A", "Direction A")]
    [InlineData("Ventilator mit umgekehrter Luftrichtung", "Direction of air flow", "Direction A")]
    [InlineData("Axialventilator im Wandring", "im Wandring", "W-series")]
    [InlineData("Axialventilator mit Schutzgitter", "Schutzgitter", "S-series")]
    [InlineData("Axialventilator freiblasend ohne Gitter", "A-series", "basic fan")]
    [InlineData("Radialventilator mit Einströmdüse", "Einströmdüse", "inlet ring")]
    public void Expand_MechanicalAndAirflowDirection_ShouldMapToEbmConventions(string query, string expected1, string expected2)
    {
        var expanded = GermanTerminologyExpander.Expand(query);

        Assert.NotEqual(query, expanded);
        Assert.Contains(expected1, expanded);
        Assert.Contains(expected2, expanded);
    }

    [Theory]
    [InlineData("Ventilator für Drehstrom", "400 VAC", "Phase 3~")]
    [InlineData("Ventilator für Lichtstrom", "230 VAC", "Phase 1~")]
    [InlineData("Ventilator mit stufenloser Regelung", "0-10 V", "PWM")]
    public void Expand_ElectricalJargon_ShouldMapToTechnicalSupplyTypes(string query, string expected1, string expected2)
    {
        var expanded = GermanTerminologyExpander.Expand(query);

        Assert.NotEqual(query, expanded);
        Assert.Contains(expected1, expanded);
        Assert.Contains(expected2, expanded);
    }

    [Fact]
    public void ElectricalQueryExpander_Expand_ShouldEnrichComplexGermanQuery()
    {
        var query = "Axialventilator im Wandring für 400V Drehstrom über Streben saugend mit stufenloser Regelung";
        var expanded = ElectricalQueryExpander.Expand(query);

        // 1. Mounting (im Wandring -> W-series)
        Assert.Contains("im Wandring", expanded);
        Assert.Contains("W-series", expanded);
        // 2. Airflow direction (über Streben saugend -> Luftrichtung V)
        Assert.Contains("Luftrichtung V", expanded);
        // 3. Electrical (400V Drehstrom -> 400 VAC / Phase 3~)
        Assert.Contains("400 VAC", expanded);
        Assert.Contains("Phase 3~", expanded);
        // 4. Control (stufenlose Regelung -> 0-10 V / PWM)
        Assert.Contains("0-10 V", expanded);
    }
}
