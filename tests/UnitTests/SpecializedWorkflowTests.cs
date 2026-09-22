using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class SpecializedWorkflowTests
{
    [Fact]
    public async Task ComparisonWorkflow_ShouldPerformIndependentRetrievalsAndFormatStructurally()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "entities": {
                "Model A": {
                  "name": "Model A",
                  "attributes": {
                    "supply_voltage": {
                      "value": "18-30 VDC",
                      "source": "Model A manual"
                    },
                    "communication_protocol": {
                      "value": "Modbus RTU",
                      "source": "Model A datasheet"
                    }
                  }
                },
                "Model B": {
                  "name": "Model B",
                  "attributes": {
                    "supply_voltage": {
                      "value": "24 VDC",
                      "source": "Model B specifications"
                    },
                    "communication_protocol": {
                      "value": "CANopen",
                      "source": "Model B manual"
                    }
                  }
                }
              }
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync("Model A specifications supply_voltage communication_protocol", It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "docA", "Model A operates on 18-30 VDC and supports Modbus RTU.", 0) });
        
        mockVectorStore.Setup(x => x.SearchAsync("Model B specifications supply_voltage communication_protocol", It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c2", "docB", "Model B has 24 VDC nominal supply and CANopen interface.", 0) });

        var executor = new ComparisonWorkflowExecutor(mockLlm.Object);
        var governorResult = new InputGovernorResult(
            Intent: "COMPARISON",
            Confidence: 0.99,
            Entities: new List<EntityInfo>
            {
                new EntityInfo("controller", "Model A"),
                new EntityInfo("controller", "Model B")
            },
            RequestedAttributes: new List<string> { "supply_voltage", "communication_protocol" },
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null
        );

        // Act
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(
            "Compare Model A and Model B supply voltage and communication protocol",
            governorResult,
            mockVectorStore.Object
        );

        // Assert
        Assert.NotNull(draftResponse);
        Assert.Contains("**Model A**", draftResponse);
        Assert.Contains("supply_voltage: 18-30 VDC", draftResponse);
        Assert.Contains("**Model B**", draftResponse);
        Assert.Contains("communication_protocol: CANopen", draftResponse);

        Assert.Equal(2, contextChunks.Count);
        Assert.Contains(contextChunks, c => c.Id == "c1");
        Assert.Contains(contextChunks, c => c.Id == "c2");

        var comparisonResult = workflowData as ComparisonResult;
        Assert.NotNull(comparisonResult);
        Assert.True(comparisonResult.Entities.ContainsKey("Model A"));
        Assert.True(comparisonResult.Entities.ContainsKey("Model B"));
        Assert.Equal("18-30 VDC", comparisonResult.Entities["Model A"].Attributes["supply_voltage"].Value);
    }

    [Fact]
    public async Task CompatibilityWorkflow_ShouldCheckSpecificDimensionsAndComputeOverallStatus()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "checks": [
                {
                  "dimension": "supply_voltage",
                  "source_value": "24 VDC +/-10%",
                  "target_requirement": "18-30 VDC",
                  "status": "COMPATIBLE",
                  "reason": "The voltage ranges match."
                },
                {
                  "dimension": "communication_protocol",
                  "source_value": "Modbus RTU",
                  "target_requirement": "CANopen",
                  "status": "INCOMPATIBLE",
                  "reason": "Protocols are fundamentally incompatible without an adapter."
                }
              ],
              "status": "INCOMPATIBLE",
              "reason": "The communication protocols do not match."
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "doc1", "Wiring and protocols compatibility context.", 0) });

        var executor = new CompatibilityWorkflowExecutor(mockLlm.Object);
        var governorResult = new InputGovernorResult(
            Intent: "COMPATIBILITY",
            Confidence: 0.95,
            Entities: new List<EntityInfo>
            {
                new EntityInfo("controller", "ABC-500"),
                new EntityInfo("sensor", "XYZ-20")
            },
            RequestedAttributes: new List<string> { "supply_voltage", "communication_protocol" },
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null
        );

        // Act
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(
            "Can controller ABC-500 work with sensor XYZ-20?",
            governorResult,
            mockVectorStore.Object
        );

        // Assert
        Assert.NotNull(draftResponse);
        Assert.Contains("**Overall Status: INCOMPATIBLE**", draftResponse);
        Assert.Contains("The communication protocols do not match.", draftResponse);
        Assert.Contains("supply_voltage", draftResponse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("communication_protocol", draftResponse, StringComparison.OrdinalIgnoreCase);

        var compatResult = workflowData as CompatibilityResult;
        Assert.NotNull(compatResult);
        Assert.Equal("INCOMPATIBLE", compatResult.Status);
        Assert.Equal(2, compatResult.Checks.Count);
        Assert.Equal("COMPATIBLE", compatResult.Checks[0].Status);
        Assert.Equal("INCOMPATIBLE", compatResult.Checks[1].Status);
    }

    [Fact]
    public async Task DiagnosticWorkflow_ShouldExtractTroubleshootingStepsAndCandidateCauses()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "causes": [
                {
                  "cause": "Input voltage drops below 18V under load",
                  "status": "SUPPORTED",
                  "evidence": ["Section 4.1.2 Power Supply Troubleshooting"],
                  "check": "Measure voltage across terminals 1 and 2 with fan running at max speed.",
                  "expected_result": "24 VDC +/- 10%"
                }
              ],
              "troubleshooting_steps": [
                "Verify mains power is present.",
                "Measure terminal voltage under active load.",
                "Inspect wiring for signs of high resistance or degradation."
              ]
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "doc1", "Troubleshooting guide for error 4 power drop issues.", 0) });

        var executor = new DiagnosticWorkflowExecutor(mockLlm.Object);
        var governorResult = new InputGovernorResult(
            Intent: "TROUBLESHOOTING",
            Confidence: 0.95,
            Entities: new List<EntityInfo>
            {
                new EntityInfo("device", "ABC-500")
            },
            RequestedAttributes: new(),
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null
        );

        // Act
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(
            "Why does the ABC-500 show error code 4?",
            governorResult,
            mockVectorStore.Object
        );

        // Assert
        Assert.NotNull(draftResponse);
        Assert.Contains("### Diagnostic Analysis", draftResponse);
        Assert.Contains("Input voltage drops below 18V under load", draftResponse);
        Assert.Contains("Measure terminal voltage under active load.", draftResponse);

        var diagnosticResult = workflowData as DiagnosticResult;
        Assert.NotNull(diagnosticResult);
        Assert.Single(diagnosticResult.Causes);
        Assert.Equal("Input voltage drops below 18V under load", diagnosticResult.Causes[0].Cause);
        Assert.Equal("SUPPORTED", diagnosticResult.Causes[0].Status);
        Assert.Equal("Measure voltage across terminals 1 and 2 with fan running at max speed.", diagnosticResult.Causes[0].Check);
        Assert.Equal(3, diagnosticResult.TroubleshootingSteps.Count);
        Assert.Equal("Verify mains power is present.", diagnosticResult.TroubleshootingSteps[0]);
    }

    [Fact]
    public async Task ComparisonWorkflow_WithEbmAxialFans_ShouldHighlightAirflowDirectionDifference()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "entities": {
                "A6E450AP0201": {
                  "name": "A6E450AP0201",
                  "attributes": {
                    "voltage": { "value": "230 VAC", "source": "Datasheet" }
                  }
                },
                "A6E450AP0202": {
                  "name": "A6E450AP0202",
                  "attributes": {
                    "voltage": { "value": "230 VAC", "source": "Datasheet" }
                  }
                }
              }
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "doc1", "A6E450 series specifications", 0) });

        var parser = new EbmProductCodeParser();
        var executor = new ComparisonWorkflowExecutor(mockLlm.Object, parser);
        var governorResult = new InputGovernorResult(
            Intent: "COMPARISON",
            Confidence: 0.99,
            Entities: new List<EntityInfo>
            {
                new EntityInfo("axial_fan", "A6E450AP0201"),
                new EntityInfo("axial_fan", "A6E450AP0202")
            },
            RequestedAttributes: new List<string> { "airflow_direction", "technology" },
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: new List<EbmProductInfo>
            {
                parser.Parse("A6E450AP0201"),
                parser.Parse("A6E450AP0202")
            }
        );

        // Act
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(
            "Sammenlign A6E450AP0201 og A6E450AP0202",
            governorResult,
            mockVectorStore.Object
        );

        // Assert
        Assert.NotNull(draftResponse);
        Assert.Contains("Kritisk", draftResponse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LUFTRETNING", draftResponse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A6E450AP0201", draftResponse);
        Assert.Contains("A6E450AP0202", draftResponse);

        var comparisonResult = workflowData as ComparisonResult;
        Assert.NotNull(comparisonResult);
        Assert.NotNull(comparisonResult.EbmEvaluation);
        Assert.False(comparisonResult.EbmEvaluation.IsDropInReplacement);
        Assert.NotEmpty(comparisonResult.EbmEvaluation.CriticalBlockers);
    }

    [Fact]
    public async Task CompatibilityWorkflow_WithOppositeAirflowAxialFans_ShouldSetIncompatibleWithAirflowBlocker()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "checks": [
                {
                  "dimension": "supply_voltage",
                  "source_value": "230 VAC",
                  "target_requirement": "230 VAC",
                  "status": "COMPATIBLE",
                  "reason": "Both are 230 VAC 1-phase."
                }
              ],
              "status": "COMPATIBLE",
              "reason": "Electrically matching."
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "doc1", "Datasheet for A6E450 fans", 0) });

        var parser = new EbmProductCodeParser();
        var executor = new CompatibilityWorkflowExecutor(mockLlm.Object, parser);
        var governorResult = new InputGovernorResult(
            Intent: "COMPATIBILITY",
            Confidence: 0.98,
            Entities: new List<EntityInfo>
            {
                new EntityInfo("axial_fan", "A6E450AP0201"),
                new EntityInfo("axial_fan", "A6E450AP0202")
            },
            RequestedAttributes: new List<string> { "airflow_direction" },
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: new List<EbmProductInfo>
            {
                parser.Parse("A6E450AP0201"),
                parser.Parse("A6E450AP0202")
            }
        );

        // Act
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(
            "Kan A6E450AP0201 erstatte A6E450AP0202?",
            governorResult,
            mockVectorStore.Object
        );

        // Assert
        Assert.NotNull(draftResponse);
        var compatResult = workflowData as CompatibilityResult;
        Assert.NotNull(compatResult);
        Assert.Equal("INCOMPATIBLE", compatResult.Status);
        Assert.NotNull(compatResult.EbmEvaluation);
        Assert.False(compatResult.EbmEvaluation.IsDropInReplacement);
        Assert.Contains(compatResult.Checks, c => c.Dimension == "Airflow Direction" && c.Status == "INCOMPATIBLE");
    }

    [Fact]
    public async Task OverviewWorkflow_ShouldExtractAllModelsFromIndexedDocumentsAndCategorizeThem()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), false))
            .ReturnsAsync("""
            ### ebm-papst Ventilator Oversigt
            
            Følgende modeller er tilgængelige i systemet:
            
            #### 1. Aksialventilatorer (Axial Fans)
            - **S4E315BS2035**: 4-polet 1-faset AC med beskyttelsesgitter, Ø315 mm (Kilde: Data_sheet_US_-_S4E315BS2035_VNA0315H4MGZ_KM312852_.pdf)
            
            #### 2. Centrifugalventilatorer & RadiPac (Centrifugal Fans)
            - **K3G560PC0401**: EC RadiPac modul i ramme, Ø560 mm (Kilde: Data_sheet_DA_-_K3G560PC0401_KM260717_.pdf)
            
            #### 3. Kompaktblæsere & DC-ventilatorer (Compact Fans)
            - **4114N/2H6PU** (9694300352): 119x119 mm kompakt DC blæser (Kilde: 9694300352_4114N_2H6PU_PDB_EN.PDF)
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>()))
            .ReturnsAsync(new[]
            {
                new Document("doc1", "Data_sheet_US_-_S4E315BS2035_VNA0315H4MGZ_KM312852_.pdf", "/data/s4e.pdf", DateTime.UtcNow),
                new Document("doc2", "Data_sheet_DA_-_K3G560PC0401_KM260717_ (1).pdf", "/data/k3g.pdf", DateTime.UtcNow),
                new Document("doc3", "9694300352_4114N_2H6PU_PDB_EN.PDF", "/data/4114.pdf", DateTime.UtcNow)
            });

        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[]
            {
                new DocumentChunk("c1", "doc1", "S4E315 series axial fan", 0, "Data_sheet_US_-_S4E315BS2035_VNA0315H4MGZ_KM312852_.pdf"),
                new DocumentChunk("c2", "doc2", "K3G560 series centrifugal fan", 0, "Data_sheet_DA_-_K3G560PC0401_KM260717_ (1).pdf")
            });

        var parser = new EbmProductCodeParser();
        var executor = new OverviewWorkflowExecutor(mockLlm.Object, parser);
        var governorResult = new InputGovernorResult(
            Intent: "OVERVIEW",
            Confidence: 0.95,
            Entities: new(),
            RequestedAttributes: new(),
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null
        );

        // Act
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(
            "What ventilators are there?",
            governorResult,
            mockVectorStore.Object
        );

        // Assert
        Assert.NotNull(draftResponse);
        Assert.Contains("S4E315BS2035", draftResponse);
        Assert.Contains("K3G560PC0401", draftResponse);
        Assert.Contains("4114N", draftResponse);

        var catalogResult = workflowData as CatalogOverviewResult;
        Assert.NotNull(catalogResult);
        Assert.True(catalogResult.TotalModels >= 3);
        Assert.True(catalogResult.Categories.ContainsKey("Aksialventilatorer (Axial Fans)"));
        Assert.True(catalogResult.Categories.ContainsKey("Centrifugalventilatorer & RadiPac (Centrifugal Fans)"));
        Assert.True(catalogResult.Categories.ContainsKey("Kompaktblæsere & DC-ventilatorer (Compact Fans)"));

        // Verify that LLM draft generation was completely eliminated for Overview workflow
        mockLlm.Verify(x => x.GenerateCompletionAsync(It.IsAny<string>(), false), Times.Never);
    }

    [Fact]
    public async Task CompatibilityWorkflow_WithSingleProduct_OpenReplacementQuery_ShouldProduceConcreteRecommendations()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), false))
            .ReturnsAsync("""
            ### Erstatningsforslag for A3G910-AO83-90
            
            A3G910-AO83-90 er en Ø910 mm EC-aksialventilator grundmodel med **Luftretning A** (lige slutciffer 90).
            
            #### Anbefalede erstatningsmodeller fra ebm-papst sortimentet:
            - **S3G910...**: Samme Ø910 mm EC aksialventilator monteret med **beskyttelsesgitter**.
            - **W3G910...**: Samme Ø910 mm EC aksialventilator monteret i **vægring**.
            
            #### Kritiske krav:
            - **Luftretning**: Erstatningsmodellen SKAL have et **lige slutciffer** (f.eks. ..02, ..04, ..90) for at sikre Luftretning A.
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>()))
            .ReturnsAsync(new[]
            {
                new Document("doc1", "Data_sheet_US_-_A3G910AO8390_KM274579_.pdf", "/data/a3g910.pdf", DateTime.UtcNow),
                new Document("doc2", "Data_sheet_US_-_S4E315BS2035_VNA0315H4MGZ_KM312852_.pdf", "/data/s4e315.pdf", DateTime.UtcNow)
            });

        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[]
            {
                new DocumentChunk("c1", "doc1", "A3G910-AO83-90 axial fan specifications 910mm", 0)
            });

        var parser = new EbmProductCodeParser();
        var executor = new CompatibilityWorkflowExecutor(mockLlm.Object, parser);
        var governorResult = new InputGovernorResult(
            Intent: "COMPATIBILITY",
            Confidence: 0.98,
            Entities: new List<EntityInfo>
            {
                new EntityInfo("axial_fan", "A3G910-AO83-90")
            },
            RequestedAttributes: new List<string> { "airflow_direction", "technology", "diameter" },
            Constraints: new(),
            ClarificationRequired: false,
            ClarificationReason: null,
            ParsedEbmProducts: new List<EbmProductInfo>
            {
                parser.Parse("A3G910-AO83-90")
            }
        );

        // Act
        var (draftResponse, contextChunks, workflowData) = await executor.ExecuteAsync(
            "hvilken ventilator kan jeg erstatte en A3G910-AO83-90 med?",
            governorResult,
            mockVectorStore.Object
        );

        // Assert
        Assert.NotNull(draftResponse);
        Assert.Contains("S3G910", draftResponse);
        Assert.Contains("W3G910", draftResponse);
        Assert.Contains("Luftretning A", draftResponse);

        var compatResult = workflowData as CompatibilityResult;
        Assert.NotNull(compatResult);
        Assert.NotNull(compatResult.ReplacementAnalysis);
        Assert.Equal("A3G910AO8390", compatResult.ReplacementAnalysis.SourceProduct.CleanCode);
        Assert.Contains(compatResult.ReplacementAnalysis.TheoreticalPatterns, p => p.SuggestedModelOrPrefix.StartsWith("S3G910"));
        Assert.Contains(compatResult.ReplacementAnalysis.TheoreticalPatterns, p => p.SuggestedModelOrPrefix.StartsWith("W3G910"));

        // Verify that LLM draft generation was completely eliminated (0 LLM round-trips for draft generation)
        mockLlm.Verify(x => x.GenerateCompletionAsync(It.IsAny<string>(), false), Times.Never);
    }
}
