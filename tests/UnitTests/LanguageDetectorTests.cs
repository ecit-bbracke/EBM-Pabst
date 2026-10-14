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

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsSpanish_DetectsSpanish()
    {
        // Arrange
        var text = """
            Datos técnicos para ventilador axial modelo 8300.
            Tensión nominal: 24 VDC. Conexión eléctrica según esquema de bornes.
            Sentido de giro y caudal de aire medido bajo condiciones nominales.
            Temperatura ambiente permitida y grado de protección del motor.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "Art_8300100547-Doc_9876318.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("es", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsFrench_DetectsFrench()
    {
        // Arrange
        var text = """
            Données techniques pour ventilateur axial modèle 4114.
            Tension nominale: 230 VAC. Raccordement électrique selon schéma.
            Sens de rotation et débit d'air mesuré avec classe d'isolation.
            Température ambiante admissible et protection moteur.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "4114_fiche_technique.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("fr", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsItalian_DetectsItalian()
    {
        // Arrange
        var text = """
            Dati tecnici per ventilatore assiale. Tensione nominale: 230 V.
            Collegamento elettrico secondo schema. Senso di rotazione e portata d'aria.
            Temperatura ambiente ammessa e grado di protezione del motore.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "scheda_tecnica.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("it", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsSwedish_DetectsSwedish()
    {
        // Arrange
        var text = """
            Tekniska data för fläkt modell 4114. Spänning och ström vid drift.
            Elektrisk anslutning och mått. Tillåten temperatur och rotationsriktning.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "flakt_datablad.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("sv", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsPolish_DetectsPolish()
    {
        // Arrange
        var text = """
            Dane techniczne dla wentylatora osiowego. Napięcie znamionowe: 24 V.
            Podłączenie elektryczne według schematu. Prędkość obrotowa i ciśnienie.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "dane_techniczne.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("pl", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsRussian_DetectsRussian()
    {
        // Arrange
        var text = """
            Технические характеристики осевого вентилятора. Номинальное напряжение 24 В.
            Направление вращения и электрическое подключение. Рабочая температура.
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "tekhnicheskie_dannye.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("ru", result.Language);
    }

    [Fact]
    public async Task DetectLanguageAsync_WhenTextIsChinese_DetectsChinese()
    {
        // Arrange
        var text = """
            轴流风机技术参数 额定电压 24V 工作温度范围 气流方向 安装尺寸和接线图
            """;

        // Act
        var result = await _detector.DetectLanguageAsync(text, "jishu_canshu.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsEnglish);
        Assert.Equal("zh", result.Language);
    }
}
