using System;
using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class DanishTextSanitizerTests
{
    [Theory]
    [InlineData("Dimension / Viftediameter: Ø800 mm", "Dimension / Impellerdiameter: Ø800 mm")]
    [InlineData("Elektrisk Tilslutningsöversigt (Klemrekasse)", "Elektrisk Tilslutningsoversigt (klemkasse)")]
    [InlineData("Vingetal: 5 (HyBlade seglformede klinger i S-serien)", "Vingetal: 5 (HyBlade seglformede vinger i S-serien)")]
    [InlineData("luft trækkes over rotoren/visere; fastslået ud fra ulige slutciffer", "luft trækkes over rotoren/monteringsstivere; fastslået ud fra ulige slutciffer")]
    [InlineData("integreret strömbegränsning i styringen", "integreret strømbegrænsning i styringen")]
    [InlineData("se övrige oplysninger i denne översigt", "se øvrige oplysninger i denne oversigt")]
    [InlineData("kompaktvifte til køling", "kompaktventilator til køling")]
    [InlineData("Candidatmodellen for modsat retning", "Kandidatmodellen for modsat retning")]
    public void Sanitize_ShouldReplaceNorwegianAndSwedishWordsWithAuthenticDanish(string input, string expected)
    {
        var sanitized = DanishTextSanitizer.Sanitize(input);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void Sanitize_EntireUserSampleText_ShouldEliminateAllLeakage()
    {
        var sample = """
            Produktidentifikation & Afkodning
            - Model: W3G800-GG95-01 (W3G800GG9501)
            - Dimension / Viftediameter: Ø800 mm
            - Luftretning: Retning 'V' (sugende, luft trækkes over rotoren/visere; fastslået)
            - Vingetal: 5 (HyBlade seglformede klinger i S-serien)
            - Elektrisk Tilslutningsöversigt (Klemrekasse)
            """;

        var sanitized = DanishTextSanitizer.Sanitize(sample);

        Assert.DoesNotContain("Viftediameter", sanitized);
        Assert.Contains("Impellerdiameter", sanitized);

        Assert.DoesNotContain("visere", sanitized);
        Assert.Contains("monteringsstivere", sanitized);

        Assert.DoesNotContain("klinger", sanitized);
        Assert.Contains("vinger", sanitized);

        Assert.DoesNotContain("översigt", sanitized);
        Assert.Contains("oversigt", sanitized);

        Assert.DoesNotContain("Klemrekasse", sanitized);
        Assert.Contains("klemkasse", sanitized);
    }
}
