using System.Collections.Generic;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class ElectricalMetadataAndQueryExpansionTests
{
    [Theory]
    // Danish queries
    [InlineData("hvilke produkter i 3 x 230 VAC kan vi tilbyde", "3~", 230)]
    [InlineData("hvad har vi i 3x400V", "3~", 400)]
    [InlineData("hvilke modeller kan køre 1 x 230 V", "1~", 230)]
    [InlineData("hvilke blæsere i 24 VDC", "DC", 24)]
    [InlineData("3 faset", "3~", null)]
    [InlineData("trefaset", "3~", null)]
    [InlineData("1-faset", "1~", null)]
    // English queries
    [InlineData("three phase 230V fans", "3~", 230)]
    [InlineData("3-phase 400 VAC", "3~", 400)]
    [InlineData("single-phase 230V models", "1~", 230)]
    [InlineData("three-phase", "3~", null)]
    [InlineData("single phase", "1~", null)]
    // German queries
    [InlineData("dreiphasig 230V lüfter", "3~", 230)]
    [InlineData("3-phasig 400 VAC", "3~", 400)]
    [InlineData("einphasig 230V", "1~", 230)]
    [InlineData("dreiphasig", "3~", null)]
    [InlineData("einphasig", "1~", null)]
    // Case sensitivity / uppercase / lowercase variations
    [InlineData("3 FASET 230 V", "3~", 230)]
    [InlineData("3 faset 230 v", "3~", 230)]
    [InlineData("3 FaSeT 230 vAc", "3~", 230)]
    [InlineData("THREE PHASE 400 VAC", "3~", 400)]
    [InlineData("three phase 400 vac", "3~", 400)]
    [InlineData("DREIPHASIG 230 V", "3~", 230)]
    [InlineData("dReIpHaSiG 230 v", "3~", 230)]
    public void ElectricalQueryExpander_ShouldDetectAndExpandQueries(string query, string expectedPhase, int? expectedVoltage)
    {
        var expanded = ElectricalQueryExpander.Expand(query);
        Assert.NotEqual(query, expanded);
        Assert.Contains(expectedPhase, expanded);

        var filter = ElectricalQueryExpander.ExtractVoltageFilter(query);
        Assert.NotNull(filter);
        Assert.Equal(expectedPhase, filter.RequiredPhase);
        Assert.Equal(expectedVoltage, filter.TargetVoltage);
    }

    [Fact]
    public void ElectricalFilter_Matches_ShouldCorrectlyValidateCandidates()
    {
        var filter = ElectricalQueryExpander.ExtractVoltageFilter("hvilke produkter i 3 x 230 VAC kan vi tilbyde");
        Assert.NotNull(filter);

        // Positive match: 3~ with 230 VAC nominal
        Assert.True(filter.Matches("3~", "230 VAC", null));

        // Positive match: 3~ with 200 .. 240 VAC range covering 230
        Assert.True(filter.Matches("3~", "230 VAC", "200 .. 240 VAC"));
        Assert.True(filter.Matches("3-phase", null, "200 .. 240 VAC"));

        // Negative match: Wrong phase (1~ 230 VAC)
        Assert.False(filter.Matches("1~", "230 VAC", "200 .. 277 VAC"));

        // Negative match: Wrong voltage (3~ 400 VAC)
        Assert.False(filter.Matches("3~", "400 VAC", "380 .. 480 VAC"));

        // Critical negative match: Unpopulated or null phase MUST NOT match when 3-phase is required!
        Assert.False(filter.Matches(null, "230 VAC", null));
        Assert.False(filter.Matches("", "230 VAC", null));
        Assert.False(filter.Matches(null, null, null));
    }

    [Fact]
    public void TechnicalMetadataExtractor_ShouldExtractElectricalDataFromDatasheetTableText()
    {
        var datasheetText = """
            Nominal data
            Item 8300102220
            Motor E09003-28
            Phase 3~
            Nominal voltage VAC 230
            Nominal voltage range VAC 200 .. 240
            Frequency Hz 50/60
            Speed (rpm) min-1 4400
            Power consumption W 760
            """;

        var meta = TechnicalMetadataExtractor.Extract(datasheetText, "Art_8300102220-Doc_10334564.pdf");

        Assert.Equal("3~", meta.Phase);
        Assert.Equal("230 VAC", meta.NominalVoltage);
        Assert.Equal("200 .. 240 VAC", meta.VoltageRange);
    }

    [Fact]
    public async Task InputGovernor_GovernInputAsync_ShouldClassifyHvilkeProdukterAsOverview()
    {
        var mockLlm = new Mock<ILlmService>();
        var governor = new InputGovernor(mockLlm.Object, new EbmProductCodeParser());

        var result = await governor.GovernInputAsync("hvilke produkter i 3 x 230 VAC kan vi tilbyde");

        Assert.Equal("OVERVIEW", result.Intent);
        Assert.Contains(result.Constraints, c => c.Contains("3 x 230"));
    }

    [Fact]
    public async Task OverviewWorkflowExecutor_WithVoltageFilter_ShouldHighlightMatchingModels()
    {
        var mockLlm = new Mock<ILlmService>();
        var mockRepo = new Mock<IDocumentRepository>();
        var mockVector = new Mock<IVectorStore>();

        var doc1 = new Document(
            Id: "doc-1",
            FileName: "Art_8300102220-Doc_10334564.pdf",
            FilePath: "/uploads/Art_8300102220.pdf",
            UploadedAt: System.DateTime.UtcNow,
            Status: DocumentStatus.Processed,
            Phase: "3~",
            NominalVoltage: "230 VAC",
            VoltageRange: "200 .. 240 VAC"
        );

        var doc2 = new Document(
            Id: "doc-2",
            FileName: "Data_sheet_DA_-_K3G560PC0401.pdf",
            FilePath: "/uploads/K3G560PC0401.pdf",
            UploadedAt: System.DateTime.UtcNow,
            Status: DocumentStatus.Processed,
            Phase: "3~",
            NominalVoltage: "400 VAC",
            VoltageRange: "380 .. 480 VAC"
        );

        mockRepo.Setup(r => r.GetAllDocumentsAsync())
            .ReturnsAsync(new List<Document> { doc1, doc2 });

        mockVector.Setup(v => v.GetDocumentsAsync(It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<Document> { doc1, doc2 });

        mockVector.Setup(v => v.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk>());

        var executor = new OverviewWorkflowExecutor(mockLlm.Object, new EbmProductCodeParser(), mockRepo.Object);

        var govResult = new InputGovernorResult(
            Intent: "OVERVIEW",
            Confidence: 1.0,
            Entities: new List<EntityInfo>(),
            RequestedAttributes: new List<string> { "catalog" },
            Constraints: new List<string> { "voltage: 3 x 230 VAC" },
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: new List<EbmProductInfo>()
        );

        var (draft, chunks, workflowData) = await executor.ExecuteAsync(
            "hvilke produkter i 3 x 230 VAC kan vi tilbyde",
            govResult,
            mockVector.Object
        );

        Assert.Contains("8300102220", draft);
        Assert.Contains("3~ 230 VAC", draft);
        Assert.DoesNotContain("K3G560PC0401", draft);
    }

    [Fact]
    public void K3G280PR04I2_ReplacementAnalysis_ShouldIdentify8300AsOptimalAndReject400VByGenericDomainRules()
    {
        var parser = new EbmProductCodeParser();
        // The source product is a RadiPac K3G Ø280 mm with PR (1~ 230V)
        var sourceK3G = parser.Parse("K3G280-PR04-I2");

        // Candidates decoded generically via ebm-papst RadiPac designation:
        // VBH0280SSPFS (S = 1~ 230V, 280mm)
        // VBH0280TPFS (T = 3~ 400V, 280mm)
        var candidate8300Optimal = parser.Parse("8300100757 VBH0280SSPFS");
        var candidate8300IncompatibleVolt = parser.Parse("8300102292 VBH0280TPFS");

        Assert.Equal(280, sourceK3G.ImpellerDiameterMm);
        Assert.Equal(280, candidate8300Optimal.ImpellerDiameterMm);
        Assert.Equal(280, candidate8300IncompatibleVolt.ImpellerDiameterMm);

        // Optimal 230V candidate comparison
        var evalOptimal = parser.Compare(sourceK3G, candidate8300Optimal);
        Assert.True(evalOptimal.IsDropInReplacement);
        Assert.Empty(evalOptimal.CriticalBlockers);

        // Incompatible 400V candidate comparison (3~ 400V vs 1~ 230V)
        var evalIncompatible = parser.Compare(sourceK3G, candidate8300IncompatibleVolt);
        Assert.False(evalIncompatible.IsDropInReplacement);
        Assert.Contains(evalIncompatible.CriticalBlockers, b => b.Contains("400 VAC") || b.Contains("forsyningsspænding"));

        // AnalyzeReplacements with catalog candidates should match the 230V model and reject the 400V model
        var analysisWithCatalog = parser.AnalyzeReplacements(sourceK3G, new[] { candidate8300Optimal, candidate8300IncompatibleVolt });
        Assert.Contains(analysisWithCatalog.MatchedDatabaseCandidates, m => m.CleanCode.Contains("8300100757"));
        Assert.Contains(analysisWithCatalog.IncompatibleDatabaseCandidates, i => i.ProductB.CleanCode.Contains("8300102292"));

        // Generic theoretical pattern for any K3G280 recommends the 8300-series RadiPac EC with Ø280 mm and matching voltage
        var analysis = parser.AnalyzeReplacements(sourceK3G);
        Assert.Contains(analysis.TheoreticalPatterns, p => p.SuggestedModelOrPrefix.Contains("8300-serien") && p.SuggestedModelOrPrefix.Contains("280"));
    }

    [Fact]
    public void K3G280PR04I2_DirectReplacement_WithActualDatasheet_8300100757_VBH0280CSPFS()
    {
        var parser = new EbmProductCodeParser();
        var sourceK3G = parser.Parse("K3G280PR04I2");

        // The actual datasheet code from 8300100757.pdf is VBH0280CSPFS (Single phase 1~ 230V, 280mm)
        var realCandidate8300 = parser.Parse("8300100757 VBH0280CSPFS");
        Assert.Equal(280, realCandidate8300.ImpellerDiameterMm);
        Assert.Contains("230", realCandidate8300.PhaseDescription);

        // VBH0280CSPFS standalone
        var vbhDirect = parser.Parse("VBH0280CSPFS");
        Assert.Equal(280, vbhDirect.ImpellerDiameterMm);
        Assert.Equal(FanFamilyType.Centrifugal, vbhDirect.FanFamily);
        Assert.Contains("230", vbhDirect.PhaseDescription);

        // Bare 8300100757 item number
        var bareCandidate = parser.Parse("8300100757");
        Assert.Equal(280, bareCandidate.ImpellerDiameterMm);
        Assert.Contains("230", bareCandidate.PhaseDescription);

        // Compare must recognize 8300100757 / VBH0280CSPFS as direct drop-in replacement
        var eval = parser.Compare(sourceK3G, realCandidate8300);
        Assert.True(eval.IsDropInReplacement);
        Assert.Empty(eval.CriticalBlockers);

        var evalBare = parser.Compare(sourceK3G, bareCandidate);
        Assert.True(evalBare.IsDropInReplacement);
        Assert.Empty(evalBare.CriticalBlockers);

        // Replacement analysis must feature 8300100757 as matched database candidate
        var analysis = parser.AnalyzeReplacements(sourceK3G, new[] { realCandidate8300 });
        Assert.Single(analysis.MatchedDatabaseCandidates);
        Assert.Contains("8300100757", analysis.MatchedDatabaseCandidates[0].CleanCode);

        // Theoretical patterns must list 8300 drop-in first and distinguish R-series as partial/secondary rebuild
        var theoretical = parser.AnalyzeReplacements(sourceK3G);
        Assert.NotEmpty(theoretical.TheoreticalPatterns);
        var primaryPattern = theoretical.TheoreticalPatterns[0];
        Assert.Contains("drop-in", primaryPattern.PatternType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8300", primaryPattern.SuggestedModelOrPrefix);

        var partialPattern = theoretical.TheoreticalPatterns.FirstOrDefault(p => p.PatternType.Contains("Delvis", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(partialPattern);
        Assert.Contains("R", partialPattern.SuggestedModelOrPrefix);
    }

    [Fact]
    public void K3G280PR04I2_Vs_K3G280RR04I2_RadiPacVsRadiCal_ShouldBeBlockedAsDirectReplacement()
    {
        var parser = new EbmProductCodeParser();
        var radiPac = parser.Parse("K3G280-PR04-I2");
        var radiCal = parser.Parse("K3G280-RR04-I2");

        var eval = parser.Compare(radiPac, radiCal);

        // RadiCal is NOT a direct drop-in replacement for RadiPac
        Assert.False(eval.IsDropInReplacement);
        Assert.Contains(eval.CriticalBlockers, b => b.Contains("RadiPac") && b.Contains("RadiCal"));
    }

    [Fact]
    public void ExtractProductsFromText_ShouldExtract_K3G280PR04I2()
    {
        var parser = new EbmProductCodeParser();
        var extracted = parser.ExtractProductsFromText("en erstatning til type K3G280PR04I2");
        Assert.Single(extracted);
        Assert.Equal("K3G280PR04I2", extracted[0].CleanCode);
    }
}
