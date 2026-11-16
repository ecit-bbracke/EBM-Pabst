using System.IO;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class RealDatasheetExtractionTests
{
    [Fact]
    public void Test_8300102220_And_R4E180_RealPdfText()
    {
        var parser = new EbmProductCodeParser();

        // 1. R4E180-AS15-10
        // An R4E fan has '4E' (1-phase AC). Even if table has 3~ in column 1, it is 1-phase AC.
        var r4eProduct = parser.Parse("R4E180-AS15-10");
        Assert.Equal(FanFamilyType.Centrifugal, r4eProduct.FanFamily);
        Assert.Equal(MotorTechnology.AC, r4eProduct.Technology);
        Assert.Equal(4, r4eProduct.Poles);
        Assert.Contains("1-faset", r4eProduct.PhaseDescription);

        // 2. 8300102220 text from PDF
        var text8300 = """
            EC centrifugal module - RadiPac
            backward-curved, single-intake
            with support bracket
            8300102220
            VBH0560CTTPZ
            Nominal data
            Item 8300102220
            Motor E15031-85
            Phase 3~
            Nominal voltage VAC 200
            Nominal voltage range VAC 200 .. 240
            Frequency Hz 50/60
            Power consumption W 4400
            """;

        var meta8300 = TechnicalMetadataExtractor.Extract(text8300, "Art_8300102220-Doc_10334564.pdf");
        Assert.Equal("3~", meta8300.Phase);
        Assert.Equal("200 VAC", meta8300.NominalVoltage);
        Assert.Equal("200 .. 240 VAC", meta8300.VoltageRange);
        Assert.Equal(4400, meta8300.PowerWatts);
        Assert.Equal(560, meta8300.DiameterMm);

        // 3. Test filter for "3X230 VAC"
        var filter = ElectricalQueryExpander.ExtractVoltageFilter("hvilke produkter i 3X230 VAC kan vi tilbyde");
        Assert.NotNull(filter);
        Assert.Equal("3~", filter.RequiredPhase);
        Assert.Equal(230, filter.TargetVoltage);

        // 8300102220 MUST MATCH because range 200 .. 240 covers 230, and phase is 3~!
        Assert.True(filter.Matches(meta8300.Phase, meta8300.NominalVoltage, meta8300.VoltageRange));

        // R4E180 MUST NOT MATCH!
        var textR4E = """
            AC centrifugal fan
            forward-curved, single-intake
            R4E180-AS15-10
            Nominal data
            Type R4E180-AS15-10
            Motor M4E074-DF
            Phase 3~ 1~ 1~
            Nominal voltage VAC 115 115 115
            Capacitor µF 16 16 16
            """;
        var metaR4E = TechnicalMetadataExtractor.Extract(textR4E, "Art_R4E180AS1510-Doc_7087420.pdf", r4eProduct);

        // Since it's an R4E (1-phase AC), its phase MUST be 1~, NOT 3~!
        Assert.NotEqual("3~", metaR4E.Phase);
        Assert.False(filter.Matches(metaR4E.Phase, metaR4E.NominalVoltage, metaR4E.VoltageRange));
    }
}
