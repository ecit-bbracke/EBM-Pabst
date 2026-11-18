using System;
using DocumentRagSystem.Core.Services;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class DimensionalQueryExpanderTests
{
    [Theory]
    [InlineData(119, 119, 0)]
    [InlineData(120, 119, 1)]
    [InlineData(121, 119, 2)]
    [InlineData(118, 119, -1)]
    [InlineData(122, 119, 3)]
    [InlineData(92, 92, 0)]
    [InlineData(90, 92, -2)]
    [InlineData(80, 80, 0)]
    [InlineData(82, 80, 2)]
    [InlineData(140, 140, 0)]
    [InlineData(142, 140, 2)]
    [InlineData(300, 300, 0)]
    [InlineData(301, 300, 1)]
    public void ResolveNearestStandardSize_ShouldMapCloseDimensionsToEbmStandard(int requested, int expectedStandard, int expectedDelta)
    {
        var result = DimensionalQueryExpander.ResolveNearestStandardSize(requested, maxToleranceMm: 3);

        Assert.NotNull(result);
        Assert.Equal(expectedStandard, result.Value.StandardSize);
        Assert.Equal(expectedDelta, result.Value.Delta);
    }

    [Fact]
    public void ResolveNearestStandardSize_WhenOutOfTolerance_ShouldReturnNull()
    {
        var result = DimensionalQueryExpander.ResolveNearestStandardSize(999, maxToleranceMm: 3);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("kompakt computerblæser på 120x120 mm", "119 mm", "119x119")]
    [InlineData("blæser med mål 121 mm", "119 mm", "compact fan")]
    [InlineData("121x121 mm ventilator", "119 mm", "119x119")]
    [InlineData("hvilken computerblæser har vi på ca. 180 m3/h", "119 mm", "compact fan")]
    [InlineData("kabinetblæser til rack", "compact fan", "119 mm")]
    [InlineData("80x80 mm kompaktblæser", "80 mm", "80x80")]
    [InlineData("92x92 mm køleblæser", "92 mm", "92x92")]
    public void Expand_ShouldEnrichQueryWithEbmStandardTerms(string query, string expectedTerm1, string expectedTerm2)
    {
        var expanded = DimensionalQueryExpander.Expand(query);

        Assert.NotEqual(query, expanded);
        Assert.Contains(expectedTerm1, expanded);
        Assert.Contains(expectedTerm2, expanded);
    }

    [Fact]
    public void ElectricalQueryExpander_Expand_ShouldCombineElectricalAndDimensionalTerms()
    {
        var query = "computerblæser på 120x120 mm i 24 VDC";
        var expanded = ElectricalQueryExpander.Expand(query);

        // Electrical expansion check:
        Assert.Contains("24 VDC", expanded);
        // Dimensional expansion check:
        Assert.Contains("119 mm", expanded);
        Assert.Contains("119x119", expanded);
        Assert.Contains("compact fan", expanded);
    }
}
