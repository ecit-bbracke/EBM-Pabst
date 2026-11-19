using System;
using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class EngineeringUnitsQueryExpanderTests
{
    [Theory]
    [InlineData(100, 169.9)]
    [InlineData(50, 85.0)]
    [InlineData(200, 339.8)]
    public void ConvertCfmToM3H_ShouldConvertAccurately(double cfm, double expectedM3h)
    {
        var result = EngineeringUnitsQueryExpander.ConvertCfmToM3H(cfm);
        Assert.Equal(expectedM3h, result);
    }

    [Theory]
    [InlineData(50, 180.0)]
    [InlineData(25, 90.0)]
    [InlineData(100, 360.0)]
    public void ConvertLitersPerSecToM3H_ShouldConvertAccurately(double ls, double expectedM3h)
    {
        var result = EngineeringUnitsQueryExpander.ConvertLitersPerSecToM3H(ls);
        Assert.Equal(expectedM3h, result);
    }

    [Theory]
    [InlineData(10, 98.1)]
    [InlineData(5, 49.0)]
    [InlineData(20, 196.1)]
    public void ConvertMmVsToPascal_ShouldConvertAccurately(double mmVs, double expectedPa)
    {
        var result = EngineeringUnitsQueryExpander.ConvertMmVsToPascal(mmVs);
        Assert.Equal(expectedPa, result);
    }

    [Theory]
    [InlineData(1.0, 249.1)]
    [InlineData(0.5, 124.5)]
    public void ConvertInWgToPascal_ShouldConvertAccurately(double inWg, double expectedPa)
    {
        var result = EngineeringUnitsQueryExpander.ConvertInWgToPascal(inWg);
        Assert.Equal(expectedPa, result);
    }

    [Theory]
    [InlineData("blæser på 100 CFM", "170 m³/h", "CFM")]
    [InlineData("ventilation med 50 l/s", "180 m³/h", "m³/h")]
    [InlineData("modtryk på 10 mmVS", "98 Pa", "Pa")]
    [InlineData("statisk tryk 0.5 in. wg", "125 Pa", "Pa")]
    [InlineData("blæser i 12 VDC", "12 VDC", "8 .. 15")]
    [InlineData("blæser til fugtigt miljø", "IP68", "IP55")]
    [InlineData("blæser med PWM styring", "PWM", "speed control")]
    [InlineData("blæser med tachosignal", "Tach output", "speed signal")]
    [InlineData("støjsvag blæser under 40 dB", "dB(A)", "sound pressure")]
    [InlineData("gældende parameterspecifikationer for W3G800GG9501", "MODBUS", "software version")]
    [InlineData("hvilken modbus version er der på W3G800GG9501", "MODBUS", "software version")]
    [InlineData("which modbus version is used", "MODBUS", "Parameter specification")]
    [InlineData("welche Modbus Version wird verwendet", "MODBUS", "Parameterbeschreibung")]
    [InlineData("W3G800 V5.01", "MODBUS", "software version")]
    [InlineData("parameter specifications for W3G800", "MODBUS", "Parameter specification")]
    [InlineData("Parameterbeschreibung und Registerbelegung für W3G800", "MODBUS", "Parameterbeschreibung")]
    public void Expand_ShouldEnrichQueryWithTechnicalUnitsAndParameters(string query, string expected1, string expected2)
    {
        var expanded = EngineeringUnitsQueryExpander.Expand(query);

        Assert.NotEqual(query, expanded);
        Assert.Contains(expected1, expanded);
        Assert.Contains(expected2, expanded);
    }

    [Fact]
    public void ElectricalQueryExpander_Expand_ShouldCombineAllTechnicalExpansions()
    {
        var query = "100 CFM computerblæser på 120x120 mm i 24 VDC til fugtigt miljø med PWM styring";
        var expanded = ElectricalQueryExpander.Expand(query);

        // 1. Electrical expansion (24 VDC)
        Assert.Contains("24 VDC", expanded);
        // 2. Dimensional expansion (120x120 -> 119 mm)
        Assert.Contains("119 mm", expanded);
        Assert.Contains("119x119", expanded);
        // 3. Airflow unit conversion (100 CFM -> 170 m³/h)
        Assert.Contains("170 m³/h", expanded);
        // 4. Ingress protection (fugtigt miljø -> IP68/IP55)
        Assert.Contains("IP68", expanded);
        // 5. Control signal (PWM -> speed control)
        Assert.Contains("PWM", expanded);
    }
}
