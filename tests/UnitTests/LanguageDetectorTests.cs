using System.Threading.Tasks;
using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class LanguageDetectorTests
{
    private readonly DefaultLanguageDetector _detector = new();

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsEnglish_DetectsEnglish()
    {
        // Arrange
        var text = """
            Technical specifications for axial fan model 4114N.
            Nominal voltage: 24 VDC. Airflow direction: exhaust over struts.
            Operating temperature range is from -20 to +65 degrees Celsius.
            The fan is equipped with ball bearings and locked rotor protection.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "4114N_datasheet.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsEnglish);
        Assert.Equal("en", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsDanish_DetectsDanish()
    {
        // Arrange
        var text = """
            Tekniske data og specifikationer for ventilator model K3G560.
            Mærkespænding: 400 VAC trefaset tilslutning ved 50/60 Hz.
            Maksimal luftstrøm og omdrejningstal under fuld belastning.
            Elektrisk tilslutning og montering skal udføres i henhold til gældende standarder.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "K3G560_datablad.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("da", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsGerman_DetectsGerman()
    {
        // Arrange
        var text = """
            Technische Daten für Axiallüfter Modell 6314H.
            Nennspannung: 24 VDC. Betriebsdaten und Drehzahl unter Nennbedingungen.
            Elektrischer Anschluss gemäß Zeichnung und Anschlussplan.
            Zulässige Umgebungstemperatur und Motorschutz bei blockiertem Rotor.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "6314H_datenblatt.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("de", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenFileNameHasDanishTag_PrioritizesDanish()
    {
        // Arrange
        var text = "Specifikationer for ventilator";

        // Act
        var result = await _detector.DetectLanguageAsync(text, "Data_sheet_DA_-_8300100049.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("da", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenFileNameHasEnglishTag_PrioritizesEnglish()
    {
        // Arrange
        var text = "Specifications for fan model";

        // Act
        var result = await _detector.DetectLanguageAsync(text, "Data_sheet_US_-_8300100799.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsEnglish);
        Assert.Equal("en", result.Language);
    }
}
