using System.Linq;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class EbmProductCodeParserTests
{
    private readonly EbmProductCodeParser _parser = new();

    [Fact]
    public void Parse_AxialFan_A6E450AP0201_ShouldCorrectlyIdentifyAllAttributes()
    {
        // Act: 12-char code with odd last digit (01 -> Airflow V)
        var info = _parser.Parse("A6E450AP0201");

        // Assert
        Assert.True(info.IsStandard12CharKey);
        Assert.Equal(FanFamilyType.Axial, info.FanFamily);
        Assert.Contains("grundmodel", info.FanTypeDescription);
        Assert.Equal(MotorTechnology.AC, info.Technology);
        Assert.Equal(6, info.Poles);
        Assert.Equal("1-faset AC (Einphasen)", info.PhaseDescription);
        Assert.Equal(450, info.ImpellerDiameterMm);
        Assert.Equal(AirflowDirection.V, info.AirflowDirection);
        Assert.Contains("Luftretning 'V'", info.AirflowDescription);
    }

    [Fact]
    public void Parse_AxialFan_A6E450AP0202_ShouldIdentifyAirflowDirectionA()
    {
        // Act: 12-char code with even last digit (02 -> Airflow A)
        var info = _parser.Parse("A6E450AP0202");

        // Assert
        Assert.True(info.IsStandard12CharKey);
        Assert.Equal(FanFamilyType.Axial, info.FanFamily);
        Assert.Equal(AirflowDirection.A, info.AirflowDirection);
        Assert.Contains("Luftretning 'A'", info.AirflowDescription);
    }

    [Theory]
    [InlineData("A6E450-AP02-01", FanFamilyType.Axial, "Ingen (grundmodel uden tilbehør)")]
    [InlineData("S4E350-AN01-30", FanFamilyType.Axial, "Beskyttelsesgitter (guard grille)")]
    [InlineData("W1G200-EC95-01", FanFamilyType.Axial, "Vægring (wall ring)")]
    public void Parse_AxialAccessories_A_S_W_ShouldIdentifyAccessoryType(string code, FanFamilyType expectedFamily, string expectedAccessory)
    {
        var info = _parser.Parse(code);

        Assert.Equal(expectedFamily, info.FanFamily);
        Assert.Equal(expectedAccessory, info.AccessoryDescription);
    }

    [Theory]
    [InlineData("R3G250-RE07-07", "Enkeltindsugende hjul uden ramme/hus")]
    [InlineData("K3G560-PC04-01", "Monteringsramme / bracket")]
    [InlineData("G3G140-AV17-02", "Scroll-hus (enkeltindsugende)")]
    [InlineData("D3G133-BF05-14", "Scroll-hus (dobbeltindsugende)")]
    public void Parse_CentrifugalTypes_R_K_G_D_ShouldIdentifyCentrifugalVariants(string code, string expectedAccessory)
    {
        var info = _parser.Parse(code);

        Assert.Equal(FanFamilyType.Centrifugal, info.FanFamily);
        Assert.Equal(MotorTechnology.EC, info.Technology);
        Assert.Equal(expectedAccessory, info.AccessoryDescription);
        Assert.Equal(AirflowDirection.AxialInRadialOut, info.AirflowDirection);
    }

    [Theory]
    [InlineData("K3G560PC0401", MotorTechnology.EC, null, "EC elektronisk")]
    [InlineData("A4E400AP0201", MotorTechnology.AC, 4, "1-faset AC (Einphasen)")]
    [InlineData("A4D450AP0201", MotorTechnology.AC, 4, "3-faset AC (Dreiphasen)")]
    [InlineData("A6E450AP0201", MotorTechnology.AC, 6, "1-faset AC (Einphasen)")]
    public void Parse_MotorTechnology_ShouldDifferentiateEC_And_ACPolePhase(
        string code,
        MotorTechnology expectedTech,
        int? expectedPoles,
        string expectedPhase)
    {
        var info = _parser.Parse(code);

        Assert.Equal(expectedTech, info.Technology);
        Assert.Equal(expectedPoles, info.Poles);
        Assert.Equal(expectedPhase, info.PhaseDescription);
    }

    [Fact]
    public void Compare_IdenticalAxialFans_ExceptAirflowDirection_ShouldReportCriticalBlocker()
    {
        // Arrange
        var fanV = _parser.Parse("A6E450AP0201"); // Luftretning V
        var fanA = _parser.Parse("A6E450AP0202"); // Luftretning A

        // Act
        var evaluation = _parser.Compare(fanV, fanA);

        // Assert
        Assert.False(evaluation.IsDropInReplacement);
        Assert.NotEmpty(evaluation.CriticalBlockers);
        Assert.Contains(evaluation.CriticalBlockers, b => b.Contains("LUFTRETNING") || b.Contains("Luftretning"));
        Assert.Contains(evaluation.Differences, d => d.Dimension == "Airflow Direction" && d.IsCriticalIncompatibility);
    }

    [Fact]
    public void Compare_AxialBase_And_AxialWithGrille_ShouldReportAccessoryDifferenceWithoutBlocker()
    {
        // Arrange
        var baseFan = _parser.Parse("A4E350AN0101");
        var grilleFan = _parser.Parse("S4E350AN0101");

        // Act
        var evaluation = _parser.Compare(baseFan, grilleFan);

        // Assert
        Assert.True(evaluation.IsDropInReplacement);
        Assert.Empty(evaluation.CriticalBlockers);
        Assert.Contains(evaluation.Differences, d => d.Dimension == "Accessory / Housing" && !d.IsCriticalIncompatibility);
    }

    [Fact]
    public void Compare_AxialFan_And_CentrifugalFan_ShouldReportCriticalBlocker()
    {
        // Arrange
        var axial = _parser.Parse("A6E450AP0201");
        var centrifugal = _parser.Parse("K3G560PC0401");

        // Act
        var evaluation = _parser.Compare(axial, centrifugal);

        // Assert
        Assert.False(evaluation.IsDropInReplacement);
        Assert.Contains(evaluation.CriticalBlockers, b => b.Contains("blæsertype"));
    }

    [Fact]
    public void ExtractProductsFromText_ShouldFindAllProductCodesInQuery()
    {
        // Arrange
        var query = "Hvad er forskellen på A6E450-AP02-01 og A6E450-AP02-02, og kan en 9694300352 eller 4114N/2H6PU bruges i stedet?";

        // Act
        var products = _parser.ExtractProductsFromText(query);

        // Assert
        Assert.True(products.Count >= 3);
        Assert.Contains(products, p => p.CleanCode == "A6E450AP0201");
        Assert.Contains(products, p => p.CleanCode == "A6E450AP0202");
        Assert.Contains(products, p => p.CleanCode.Contains("9694300352") || p.CleanCode.Contains("4114N"));
    }

    [Theory]
    [InlineData("9694300352", FanFamilyType.CompactOrSpecial)]
    [InlineData("4114N/2H6PU", FanFamilyType.CompactOrSpecial)]
    [InlineData("6318/2TDH4P", FanFamilyType.CompactOrSpecial)]
    [InlineData("8300-EC", FanFamilyType.Centrifugal)]
    public void Parse_CompactAndNewerSeries_ShouldParseCorrectly(string code, FanFamilyType expectedFamily)
    {
        var info = _parser.Parse(code);

        Assert.Equal(expectedFamily, info.FanFamily);
        Assert.NotNull(info.SeriesFamily);
    }

    [Fact]
    public void AnalyzeReplacements_ForA3G910_ShouldProduce_S_and_W_Patterns_And_AirflowDirectionA_Rule()
    {
        // Arrange
        var sourceFan = _parser.Parse("A3G910-AO83-90");

        // Act
        var analysis = _parser.AnalyzeReplacements(sourceFan);

        // Assert
        Assert.NotNull(analysis);
        Assert.Equal(FanFamilyType.Axial, analysis.SourceProduct.FanFamily);
        Assert.Equal(910, analysis.SourceProduct.ImpellerDiameterMm);
        Assert.Equal(AirflowDirection.A, analysis.SourceProduct.AirflowDirection);

        // Verify theoretical patterns
        Assert.Contains(analysis.TheoreticalPatterns, p => p.SuggestedModelOrPrefix.StartsWith("S3G910"));
        Assert.Contains(analysis.TheoreticalPatterns, p => p.SuggestedModelOrPrefix.StartsWith("W3G910"));

        // Verify critical airflow rule
        Assert.Contains(analysis.ReplacementRules, r => r.Contains("LIGE ciffer") && r.Contains("Luftretning A"));
    }

    [Fact]
    public void AnalyzeReplacements_WithMatchingCatalogCandidate_ShouldIdentifyDropInCandidate()
    {
        // Arrange
        var sourceFan = _parser.Parse("A4E350AN0101"); // Airflow V (odd digit 01)
        var matchingCandidate = _parser.Parse("S4E350AN0101"); // Airflow V with guard grille
        var differentAirflowCandidate = _parser.Parse("A4E350AN0102"); // Airflow A (even digit 02)

        // Act
        var analysis = _parser.AnalyzeReplacements(sourceFan, new[] { matchingCandidate, differentAirflowCandidate });

        // Assert
        Assert.Contains(analysis.MatchedDatabaseCandidates, m => m.CleanCode == matchingCandidate.CleanCode);
        Assert.Contains(analysis.IncompatibleDatabaseCandidates, i => i.ProductB.CleanCode == differentAirflowCandidate.CleanCode);
    }
}
