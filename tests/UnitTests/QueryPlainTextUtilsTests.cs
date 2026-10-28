using DocumentRagSystem.WebApi.Endpoints;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class QueryPlainTextUtilsTests
{
    [Fact]
    public void ConvertToPlainText_WhenInputContainsHtmlTags_StripsAllTags()
    {
        // Arrange
        var input = "<p>Driftsspændingen for <strong>K3G355HA3401</strong> er <b>380–480 VAC</b>.<br/>Maksimalt omdrejningstal: 2800 rpm.</p>";

        // Act
        var result = QueryEndpoints.ConvertToPlainText(input);

        // Assert
        Assert.DoesNotContain("<p>", result);
        Assert.DoesNotContain("</p>", result);
        Assert.DoesNotContain("<strong>", result);
        Assert.DoesNotContain("</strong>", result);
        Assert.DoesNotContain("<b>", result);
        Assert.DoesNotContain("</b>", result);
        Assert.DoesNotContain("<br", result);
        Assert.Contains("Driftsspændingen for K3G355HA3401 er 380–480 VAC.", result);
        Assert.Contains("Maksimalt omdrejningstal: 2800 rpm.", result);
    }

    [Fact]
    public void ConvertToPlainText_WhenInputContainsMarkdown_ConvertsToCleanPlainText()
    {
        // Arrange
        var input = "### Specifikationer for K3G355HA3401\n\n* **Spænding**: 400 VAC\n* **Luftmængde**: 3.500 m³/h\n* **Effekt**: 1.250 W";

        // Act
        var result = QueryEndpoints.ConvertToPlainText(input);

        // Assert
        Assert.DoesNotContain("###", result);
        Assert.DoesNotContain("**", result);
        Assert.Contains("Specifikationer for K3G355HA3401", result);
        Assert.Contains("Spænding: 400 VAC", result);
        Assert.Contains("Luftmængde: 3.500 m³/h", result);
    }

    [Fact]
    public void ConvertToPlainText_WhenInputContainsHtmlEntities_DecodesProperly()
    {
        // Arrange
        var input = "ebm-papst &amp; ventilatorer &quot;GreenTech&quot; &lt;EC-motor&gt;";

        // Act
        var result = QueryEndpoints.ConvertToPlainText(input);

        // Assert
        Assert.Equal("ebm-papst & ventilatorer \"GreenTech\" <EC-motor>", result);
    }

    [Fact]
    public void ConvertToPlainText_WhenInputNullOrEmpty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, QueryEndpoints.ConvertToPlainText(null));
        Assert.Equal(string.Empty, QueryEndpoints.ConvertToPlainText(""));
        Assert.Equal(string.Empty, QueryEndpoints.ConvertToPlainText("   "));
    }
}
