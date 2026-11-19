using System;
using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class DanishTerminologyExpanderTests
{
    [Theory]
    [InlineData("røggasblæser til pillefyr", "flue gas", "R2E")]
    [InlineData("stokerfyr med røgsuger", "flue gas", "R2E")]
    [InlineData("tavleblæser til styretavle", "control cabinet", "filter fan")]
    [InlineData("køling af automatikskab", "control cabinet", "compact fan")]
    [InlineData("emhætteblæser til køkken", "range hood", "D2E")]
    [InlineData("fordamperblæser til kølemøbel", "refrigeration", "W1G")]
    [InlineData("Hvad er den laveste hastighed på ESM produkterne", "W1G", "500 min-1")]
    [InlineData("ESM motor til fordamper", "Energy Saving Motor", "W1G")]
    [InlineData("aggregat til varmegenvinding", "heat recovery", "AHU")]
    [InlineData("kanalblæser til spirorør", "duct fan", "centrifugal")]
    [InlineData("staldventilation til landbrug", "agricultural", "barn ventilation")]
    [InlineData("punktudsugning til spånsug", "fume extraction", "dust extraction")]
    public void Expand_Applications_ShouldEnrichDanishTermsWithTechnicalCatalogTerms(string query, string expected1, string expected2)
    {
        var expanded = DanishTerminologyExpander.Expand(query);

        Assert.NotEqual(query, expanded);
        Assert.Contains(expected1, expanded);
        Assert.Contains(expected2, expanded);
    }

    [Theory]
    [InlineData("sugende aksialblæser", "Direction V", "airflow direction V")]
    [InlineData("trykkende blæser", "Direction A", "airflow direction A")]
    [InlineData("blæser i vægramme", "wall ring", "W-series")]
    [InlineData("aksialblæser med kurvegitter", "guard grille", "S-series")]
    [InlineData("ren vinge uden gitter", "A-series", "basic fan")]
    [InlineData("centrifugal med indløbsdyse", "inlet ring", "inlet nozzle")]
    public void Expand_MechanicalAndAirflowDirection_ShouldMapToEbmConventions(string query, string expected1, string expected2)
    {
        var expanded = DanishTerminologyExpander.Expand(query);

        Assert.NotEqual(query, expanded);
        Assert.Contains(expected1, expanded);
        Assert.Contains(expected2, expanded);
    }

    [Theory]
    [InlineData("blæser til kraftstrøm", "400 VAC", "3~")]
    [InlineData("blæser til almindeligt lysnet", "230 VAC", "1~")]
    [InlineData("blæser med trinløs regulering", "0-10 V", "PWM")]
    [InlineData("styrespænding til blæser", "24 VDC", "DC")]
    public void Expand_ElectricalJargon_ShouldMapToTechnicalSupplyTypes(string query, string expected1, string expected2)
    {
        var expanded = DanishTerminologyExpander.Expand(query);

        Assert.NotEqual(query, expanded);
        Assert.Contains(expected1, expanded);
        Assert.Contains(expected2, expanded);
    }

    [Fact]
    public void ElectricalQueryExpander_Expand_ShouldEnrichComplexDanishQuery()
    {
        var query = "sugende røggasblæser til pillefyr i vægramme på 230V med trinløs styring";
        var expanded = ElectricalQueryExpander.Expand(query);

        // 1. Danish application (pillefyr / røggas -> flue gas, R2E)
        Assert.Contains("flue gas", expanded);
        Assert.Contains("R2E", expanded);
        // 2. Airflow direction (sugende -> Direction V)
        Assert.Contains("Direction V", expanded);
        // 3. Mounting (vægramme -> wall ring / W-series)
        Assert.Contains("wall ring", expanded);
        Assert.Contains("W-series", expanded);
        // 4. Electrical (230V -> 230 VAC)
        Assert.Contains("230 VAC", expanded);
        // 5. Control (trinløs styring -> 0-10 V / PWM)
        Assert.Contains("0-10 V", expanded);
    }
}
