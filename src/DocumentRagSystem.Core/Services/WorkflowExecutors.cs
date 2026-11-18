using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

internal static class CatalogCacheHelper
{
    private static readonly ConcurrentDictionary<string, (DateTime CachedAt, Dictionary<string, Document> Docs, List<EbmProductInfo> Products)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public static async Task<(Dictionary<string, Document> AllDocs, List<EbmProductInfo> CatalogProducts)> GetCatalogDocumentsAndProductsAsync(
        IVectorStore vectorStore,
        IDocumentRepository? documentRepository,
        IEbmProductCodeParser ebmParser
    )
    {
        var cacheKey = $"catalog_{documentRepository?.GetHashCode()}_{vectorStore.GetHashCode()}";
        if (_cache.TryGetValue(cacheKey, out var entry) && (DateTime.UtcNow - entry.CachedAt) < CacheTtl)
        {
            return (entry.Docs, entry.Products);
        }

        var allDocsDict = new Dictionary<string, Document>();

        if (documentRepository != null)
        {
            try
            {
                var repoDocs = await documentRepository.GetAllDocumentsAsync();
                foreach (var doc in repoDocs)
                {
                    allDocsDict[doc.Id] = doc;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error querying document repository: {ex.Message}");
            }
        }

        try
        {
            var vectorDocs = await vectorStore.GetDocumentsAsync(1000);
            foreach (var doc in vectorDocs)
            {
                if (!allDocsDict.ContainsKey(doc.Id))
                {
                    allDocsDict[doc.Id] = doc;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving vector store docs: {ex.Message}");
        }

        var catalogProducts = new List<EbmProductInfo>();
        foreach (var doc in allDocsDict.Values)
        {
            var docName = !string.IsNullOrWhiteSpace(doc.FileName) ? doc.FileName : doc.Id;
            var combinedText = $"{docName} {doc.ProductCode} {doc.ArticleId}".Trim();
            var extracted = ebmParser.ExtractProductsFromText(combinedText);
            if (extracted.Count > 0)
            {
                foreach (var prod in extracted)
                {
                    var enrichedProd = prod;
                    if (!enrichedProd.ImpellerDiameterMm.HasValue && doc.DiameterMm.HasValue)
                    {
                        enrichedProd = enrichedProd with { ImpellerDiameterMm = doc.DiameterMm.Value };
                    }
                    if (doc.Phase != null)
                    {
                        var powerStr = doc.PowerWatts.HasValue ? $" ({doc.PowerWatts.Value} W)" : "";
                        var voltStr = !string.IsNullOrWhiteSpace(doc.NominalVoltage) ? $" {doc.NominalVoltage}" : "";
                        enrichedProd = enrichedProd with { PhaseDescription = $"{doc.Phase}{voltStr}{powerStr}".Trim() };
                    }
                    catalogProducts.Add(enrichedProd);
                }
            }
            else if (!string.IsNullOrWhiteSpace(doc.ProductCode) && ebmParser.TryParse(doc.ProductCode, out var singleProd))
            {
                var enrichedProd = singleProd;
                if (!enrichedProd.ImpellerDiameterMm.HasValue && doc.DiameterMm.HasValue)
                {
                    enrichedProd = enrichedProd with { ImpellerDiameterMm = doc.DiameterMm.Value };
                }
                if (doc.Phase != null)
                {
                    var powerStr = doc.PowerWatts.HasValue ? $" ({doc.PowerWatts.Value} W)" : "";
                    var voltStr = !string.IsNullOrWhiteSpace(doc.NominalVoltage) ? $" {doc.NominalVoltage}" : "";
                    enrichedProd = enrichedProd with { PhaseDescription = $"{doc.Phase}{voltStr}{powerStr}".Trim() };
                }
                catalogProducts.Add(enrichedProd);
            }
        }

        _cache[cacheKey] = (DateTime.UtcNow, allDocsDict, catalogProducts);
        return (allDocsDict, catalogProducts);
    }
}

public interface IWorkflowExecutor
{
    Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    );
}

public class SimpleWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public SimpleWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var chunks = (initialSearchTask != null ? await initialSearchTask : await vectorStore.SearchAsync(question)).ToList();
        var answer = await _llmService.GenerateResponseAsync(question, chunks);
        return (answer, chunks, null);
    }
}

public class ComparisonWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;
    private readonly IEbmProductCodeParser _ebmParser;

    public ComparisonWorkflowExecutor(ILlmService llmService, IEbmProductCodeParser? ebmParser = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var allChunks = new List<DocumentChunk>();
        var entities = governorResult.Entities ?? new List<EntityInfo>();

        var ebmProducts = (governorResult.ParsedEbmProducts != null && governorResult.ParsedEbmProducts.Count > 0)
            ? governorResult.ParsedEbmProducts
            : _ebmParser.ExtractProductsFromText(question);

        EbmComparisonEvaluation? ebmEval = null;
        if (ebmProducts.Count >= 2)
        {
            ebmEval = _ebmParser.Compare(ebmProducts[0], ebmProducts[1]);
        }

        if (entities.Count > 0)
        {
            var searchTasks = entities.Select(entity =>
            {
                var attrs = governorResult.RequestedAttributes != null ? string.Join(" ", governorResult.RequestedAttributes) : "";
                var query = $"{entity.Name} specifications {attrs}".Trim();
                return vectorStore.SearchAsync(query);
            }).ToList();

            var searchResults = await Task.WhenAll(searchTasks);
            foreach (var entityChunks in searchResults)
            {
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = initialSearchTask != null
                ? await initialSearchTask
                : await vectorStore.SearchAsync(question);
            allChunks.AddRange(fallbackChunks);
        }

        // Deduplicate chunks
        allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

        var ebmPromptContext = ebmEval != null
            ? $"\n\nDeterministic ebm-papst Domain Evaluation:\nSummary: {ebmEval.Summary}\nCritical Blockers:\n{string.Join("\n", ebmEval.CriticalBlockers.Select(b => $"- {b}"))}\nDifferences:\n{string.Join("\n", ebmEval.Differences.Select(d => $"- {d.Dimension}: {d.ValueA} vs {d.ValueB} (Critical: {d.IsCriticalIncompatibility}) - {d.Explanation}"))}"
            : "";

        var prompt = $$"""
            You are the Comparison RAG workflow executor for ventilation and motor systems (ebm-papst domain).
            Based on the retrieved technical document chunks and domain knowledge, compare the entities mentioned in the query.
            Only compare documented or deterministically decoded attributes. Do not invent missing specifications.
            If an attribute is unknown, set its value to "UNKNOWN".

            Pay special attention to:
            1. Airflow direction (A vs V on axial fans based on even/odd 12th digit in product code).
            2. Fan type & accessories (A = base, S = guard grille, W = wall ring; R = motorized impeller, K = RadiPac/bracket, G = single-inlet scroll, D = dual-inlet scroll).
            3. Motor technology (3G = EC; poles & 1-phase E / 3-phase D for AC).
            4. Impeller diameter (mm).
            {{ebmPromptContext}}

            Return a structured JSON object strictly matching this schema:
            {
              "entities": {
                "EntityNameA": {
                  "name": "EntityNameA",
                  "attributes": {
                    "attribute_name": {
                      "value": "attribute value",
                      "source": "source document or section"
                    }
                  }
                }
              }
            }

            Context chunks:
            {{string.Join("\n\n", allChunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Query: {{question}}
            """;

        var jsonResponse = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
        ComparisonResult? comparisonResult = null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            comparisonResult = JsonSerializer.Deserialize<ComparisonResult>(jsonResponse, options);
            if (comparisonResult != null)
            {
                comparisonResult = comparisonResult with { EbmEvaluation = ebmEval };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Comparison RAG output: {ex.Message}");
            if (ebmEval != null)
            {
                comparisonResult = new ComparisonResult(new Dictionary<string, EntityComparisonData>(), ebmEval);
            }
        }

        var textResponse = "";

        if (ebmEval != null)
        {
            if (ebmEval.CriticalBlockers.Count > 0)
            {
                textResponse += "### ⚠️ Kritisk ebm-papst Typenøgle & Kompatibilitetsadvarsel\n\n";
                foreach (var blocker in ebmEval.CriticalBlockers)
                {
                    textResponse += $"- ❌ **{blocker}**\n";
                }
                textResponse += "\n";
            }

            textResponse += "### Typenøgle-afkodning\n";
            textResponse += $"* **{ebmEval.ProductA.RawCode}**: {ebmEval.ProductA.FanTypeDescription}, {ebmEval.ProductA.MotorDescription}, Ø{ebmEval.ProductA.ImpellerDiameterMm}mm, {ebmEval.ProductA.AirflowDescription}\n";
            textResponse += $"* **{ebmEval.ProductB.RawCode}**: {ebmEval.ProductB.FanTypeDescription}, {ebmEval.ProductB.MotorDescription}, Ø{ebmEval.ProductB.ImpellerDiameterMm}mm, {ebmEval.ProductB.AirflowDescription}\n\n";
        }

        textResponse += "### Comparison Summary\n\n";
        if (comparisonResult?.Entities != null && comparisonResult.Entities.Count > 0)
        {
            foreach (var kvp in comparisonResult.Entities)
            {
                textResponse += $"**{kvp.Key}**:\n";
                foreach (var attr in kvp.Value.Attributes)
                {
                    textResponse += $"- {attr.Key}: {attr.Value.Value} (Source: {attr.Value.Source})\n";
                }
                textResponse += "\n";
            }
        }
        else
        {
            textResponse += jsonResponse;
        }

        return (textResponse, allChunks, comparisonResult);
    }
}

public class CompatibilityWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly IDocumentRepository? _documentRepository;

    public CompatibilityWorkflowExecutor(
        ILlmService llmService,
        IEbmProductCodeParser? ebmParser = null,
        IDocumentRepository? documentRepository = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
        _documentRepository = documentRepository;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var allChunks = new List<DocumentChunk>();
        var entities = governorResult.Entities ?? new List<EntityInfo>();

        var ebmProducts = (governorResult.ParsedEbmProducts != null && governorResult.ParsedEbmProducts.Count > 0)
            ? governorResult.ParsedEbmProducts
            : _ebmParser.ExtractProductsFromText(question);

        EbmComparisonEvaluation? ebmEval = null;
        EbmReplacementAnalysis? replacementAnalysis = null;

        // CASE 1: Open replacement / single product query
        if (ebmProducts.Count == 1)
        {
            var sourceProduct = ebmProducts[0];
            var (allDocsDict, catalogProducts) = await CatalogCacheHelper.GetCatalogDocumentsAndProductsAsync(vectorStore, _documentRepository, _ebmParser);

            // Enrich source product with physical/electrical specs from database if available
            var matchingSourceDoc = allDocsDict.Values.FirstOrDefault(d =>
                (!string.IsNullOrWhiteSpace(d.ProductCode) && d.ProductCode.Equals(sourceProduct.CleanCode, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.FileName) && d.FileName.Contains(sourceProduct.CleanCode, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.ArticleId) && d.ArticleId.Contains(sourceProduct.CleanCode, StringComparison.OrdinalIgnoreCase)));

            if (matchingSourceDoc != null)
            {
                if (!sourceProduct.ImpellerDiameterMm.HasValue && matchingSourceDoc.DiameterMm.HasValue)
                {
                    sourceProduct = sourceProduct with { ImpellerDiameterMm = matchingSourceDoc.DiameterMm.Value };
                }
                if (matchingSourceDoc.PowerWatts.HasValue && (sourceProduct.PhaseDescription == null || !sourceProduct.PhaseDescription.Contains(" W")))
                {
                    var voltStr = !string.IsNullOrWhiteSpace(matchingSourceDoc.NominalVoltage) ? $" {matchingSourceDoc.NominalVoltage}" : "";
                    var phaseStr = matchingSourceDoc.Phase ?? sourceProduct.PhaseDescription ?? "";
                    sourceProduct = sourceProduct with { PhaseDescription = $"{phaseStr}{voltStr} ({matchingSourceDoc.PowerWatts.Value} W)".Trim() };
                }
            }

            replacementAnalysis = _ebmParser.AnalyzeReplacements(sourceProduct, catalogProducts);

            // 1. Directly fetch chunks for the source product from local database if available
            var sourceDocs = allDocsDict.Values
                .Where(d => (!string.IsNullOrWhiteSpace(d.FileName) && d.FileName.Contains(sourceProduct.CleanCode, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrWhiteSpace(d.ArticleId) && d.ArticleId.Contains(sourceProduct.CleanCode, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrWhiteSpace(d.ProductCode) && d.ProductCode.Contains(sourceProduct.CleanCode, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var doc in sourceDocs)
            {
                try
                {
                    var docChunks = (await vectorStore.GetChunksByDocumentAndIndicesAsync(doc.Id, new[] { 0, 1, 2, 3 })).ToList();
                    if (docChunks.Count == 0)
                    {
                        docChunks = (await vectorStore.GetAllChunksByDocumentIdAsync(doc.Id)).Take(4).ToList();
                    }
                    var enrichedChunks = docChunks.Select(c => c with
                    {
                        FileName = !string.IsNullOrWhiteSpace(c.FileName) ? c.FileName : doc.FileName,
                        FilePath = !string.IsNullOrWhiteSpace(c.FilePath) ? c.FilePath : doc.FilePath,
                        ArticleId = !string.IsNullOrWhiteSpace(c.ArticleId) ? c.ArticleId : sourceProduct.CleanCode
                    }).ToList();
                    allChunks.AddRange(enrichedChunks);
                }
                catch { }
            }

            // 2. Directly fetch chunks for top matched database candidates (prioritizing closest drop-ins)
            foreach (var match in replacementAnalysis.MatchedDatabaseCandidates.Take(3))
            {
                var candidateDocs = allDocsDict.Values
                    .Where(d => (!string.IsNullOrWhiteSpace(d.FileName) && d.FileName.Contains(match.CleanCode, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrWhiteSpace(d.ArticleId) && d.ArticleId.Contains(match.CleanCode, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrWhiteSpace(d.ProductCode) && d.ProductCode.Contains(match.CleanCode, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                foreach (var doc in candidateDocs)
                {
                    try
                    {
                        var docChunks = (await vectorStore.GetChunksByDocumentAndIndicesAsync(doc.Id, new[] { 0, 1, 2, 3 })).ToList();
                        if (docChunks.Count == 0)
                        {
                            docChunks = (await vectorStore.GetAllChunksByDocumentIdAsync(doc.Id)).Take(4).ToList();
                        }
                        var enrichedChunks = docChunks.Select(c => c with
                        {
                            FileName = !string.IsNullOrWhiteSpace(c.FileName) ? c.FileName : doc.FileName,
                            FilePath = !string.IsNullOrWhiteSpace(c.FilePath) ? c.FilePath : doc.FilePath,
                            ArticleId = !string.IsNullOrWhiteSpace(c.ArticleId) ? c.ArticleId : match.CleanCode
                        }).ToList();

                        allChunks.AddRange(enrichedChunks);
                    }
                    catch { }
                }
            }

            // Fetch source chunks and candidate chunks in parallel
            var searchTasks = new List<Task<IEnumerable<DocumentChunk>>>
            {
                vectorStore.SearchAsync($"{sourceProduct.RawCode} {sourceProduct.CleanCode} specifications")
            };

            foreach (var match in replacementAnalysis.MatchedDatabaseCandidates.Take(3))
            {
                searchTasks.Add(vectorStore.SearchAsync($"{match.RawCode} {match.CleanCode} specifications"));
            }

            // Proactively search for recommended theoretical series from vector store / catalog
            if (sourceProduct.FanFamily == FanFamilyType.Centrifugal && sourceProduct.CleanCode.StartsWith("K"))
            {
                var diam = sourceProduct.ImpellerDiameterMm;
                if (diam.HasValue)
                {
                    searchTasks.Add(vectorStore.SearchAsync($"8300 {diam.Value} RadiPac specifications"));
                }
            }
            else if (sourceProduct.FanFamily == FanFamilyType.Axial && sourceProduct.Technology == MotorTechnology.AC)
            {
                var diam = sourceProduct.ImpellerDiameterMm;
                if (diam.HasValue)
                {
                    searchTasks.Add(vectorStore.SearchAsync($"S3G {diam.Value} W3G specifications"));
                }
            }

            var searchResults = await Task.WhenAll(searchTasks);
            foreach (var res in searchResults)
            {
                allChunks.AddRange(res);
            }

            // Purge chunks from clearly incompatible dimensions when replacing a fan with defined diameter
            if (sourceProduct.ImpellerDiameterMm.HasValue)
            {
                var targetDiam = sourceProduct.ImpellerDiameterMm.Value;
                allChunks.RemoveAll(c =>
                {
                    if (allDocsDict.TryGetValue(c.DocumentId, out var doc) && doc.DiameterMm.HasValue)
                    {
                        return Math.Abs(doc.DiameterMm.Value - targetDiam) > 50;
                    }
                    return false;
                });
            }

            allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

            var checks = new List<CompatibilityCheck>
            {
                new CompatibilityCheck(
                    Dimension: "Impeller Diameter",
                    SourceValue: $"Ø{sourceProduct.ImpellerDiameterMm} mm",
                    TargetRequirement: $"Ø{sourceProduct.ImpellerDiameterMm} mm (match installation cutout)",
                    Status: "COMPATIBLE",
                    Reason: $"Samme impellerdiameter er påkrævet for mekanisk montering."
                ),
                new CompatibilityCheck(
                    Dimension: "Airflow Direction",
                    SourceValue: sourceProduct.AirflowDescription,
                    TargetRequirement: sourceProduct.AirflowDescription,
                    Status: "COMPATIBLE",
                    Reason: sourceProduct.FanFamily == FanFamilyType.Axial
                        ? $"12. ciffer skal have {(sourceProduct.AirflowDirection == AirflowDirection.A ? "et LIGE ciffer for Luftretning A" : "et ULIGE ciffer for Luftretning V")}."
                        : "Aksial indsugning, radial udblæsning."
                ),
                new CompatibilityCheck(
                    Dimension: "Motor Technology",
                    SourceValue: sourceProduct.MotorDescription,
                    TargetRequirement: sourceProduct.Technology == MotorTechnology.EC ? "EC motor (3G) med 0-10V/Modbus" : "AC motor",
                    Status: "COMPATIBLE",
                    Reason: sourceProduct.Technology == MotorTechnology.EC
                        ? "EC 3G teknologi sikrer integreret motorstyring."
                        : "AC motorstyring."
                )
            };

            var overallStatus = replacementAnalysis.MatchedDatabaseCandidates.Count > 0 ? "COMPATIBLE" : "CONDITIONAL";
            var overallReason = replacementAnalysis.MatchedDatabaseCandidates.Count > 0
                ? $"Fandt {replacementAnalysis.MatchedDatabaseCandidates.Count} direkte erstatningskandidat(er) i databasen: {string.Join(", ", replacementAnalysis.MatchedDatabaseCandidates.Select(m => m.RawCode))}."
                : replacementAnalysis.Summary;

            var draftSb = new StringBuilder();
            draftSb.AppendLine($"### Erstatnings- og kompatibilitetsanalyse for {sourceProduct.RawCode}\n");
            draftSb.AppendLine($"**Produkt**: {sourceProduct.RawCode} ({sourceProduct.CleanCode})");
            draftSb.AppendLine($"**Type**: {sourceProduct.FanFamily} ({sourceProduct.FanTypeDescription})");
            draftSb.AppendLine($"**Motorteknologi**: {sourceProduct.Technology} ({sourceProduct.MotorDescription})");
            draftSb.AppendLine($"**Diameter**: Ø{sourceProduct.ImpellerDiameterMm} mm");
            draftSb.AppendLine($"**Luftretning**: {sourceProduct.AirflowDescription}\n");
            draftSb.AppendLine($"**Status**: {overallStatus}");
            draftSb.AppendLine($"**Konklusion**: {overallReason}\n");

            if (replacementAnalysis.MatchedDatabaseCandidates.Count > 0)
            {
                draftSb.AppendLine("#### Direkte erstatningsmodeller i databasen:");
                foreach (var match in replacementAnalysis.MatchedDatabaseCandidates)
                {
                    var phaseStr = !string.IsNullOrWhiteSpace(match.PhaseDescription) ? $", {match.PhaseDescription}" : "";
                    draftSb.AppendLine($"- **{match.RawCode}**: {match.CleanCode} (Ø{match.ImpellerDiameterMm} mm, {match.MotorDescription}{phaseStr}, {match.AirflowDescription})");
                }
                draftSb.AppendLine("\n*Bemærk: Ovenstående model(ler) findes i systemets datablade og udgør den optimale direkte drop-in erstatning.*");
                draftSb.AppendLine();
            }
            else
            {
                draftSb.AppendLine("Der er ingen direkte drop-in erstatningsmodeller tilgængelige i den lokale database.\n");
            }

            if (replacementAnalysis.TheoreticalPatterns.Count > 0)
            {
                draftSb.AppendLine("#### Anbefalede erstatningsmodeller fra ebm-papst sortimentet:");
                foreach (var pat in replacementAnalysis.TheoreticalPatterns)
                {
                    draftSb.AppendLine($"- **{pat.SuggestedModelOrPrefix}**: {pat.PatternType} - {pat.Description}");
                    if (pat.Requirements != null && pat.Requirements.Count > 0)
                    {
                        foreach (var req in pat.Requirements)
                        {
                            draftSb.AppendLine($"  * {req}");
                        }
                    }
                }
                draftSb.AppendLine();
            }

            if (replacementAnalysis.ReplacementRules.Count > 0)
            {
                draftSb.AppendLine("#### Kritiske kompatibilitetskrav og regler:");
                foreach (var rule in replacementAnalysis.ReplacementRules)
                {
                    draftSb.AppendLine($"- {rule}");
                }
            }

            var structuredDraft = draftSb.ToString().Trim();
            var result = new CompatibilityResult(checks, overallStatus, overallReason, null, replacementAnalysis);
            return (structuredDraft, allChunks, result);
        }

        // CASE 2: Two or more products point-to-point comparison
        if (ebmProducts.Count >= 2)
        {
            ebmEval = _ebmParser.Compare(ebmProducts[0], ebmProducts[1]);
        }

        if (entities.Count > 0)
        {
            var searchTasks = entities.Select(entity =>
            {
                var attrs = governorResult.RequestedAttributes != null ? string.Join(" ", governorResult.RequestedAttributes) : "";
                var query = $"{entity.Name} compatibility {attrs}".Trim();
                return vectorStore.SearchAsync(query);
            }).ToList();

            var searchResults = await Task.WhenAll(searchTasks);
            foreach (var entityChunks in searchResults)
            {
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = initialSearchTask != null
                ? await initialSearchTask
                : await vectorStore.SearchAsync(question);
            allChunks.AddRange(fallbackChunks);
        }

        allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

        var ebmPromptContext = ebmEval != null
            ? $"\n\nDeterministic ebm-papst Domain Evaluation:\nSummary: {ebmEval.Summary}\nCritical Blockers:\n{string.Join("\n", ebmEval.CriticalBlockers.Select(b => $"- {b}"))}\nDifferences:\n{string.Join("\n", ebmEval.Differences.Select(d => $"- {d.Dimension}: {d.ValueA} vs {d.ValueB} (Critical: {d.IsCriticalIncompatibility}) - {d.Explanation}"))}"
            : "";

        var prompt2 = $$"""
            You are the Compatibility RAG workflow executor for industrial fans and motor drives (ebm-papst domain).
            Evaluate compatibility and direct replacement viability between the components or models mentioned in the query.
            Examine dimensions like airflow direction, voltage, motor technology, diameter, protocol, baud rate, signal type, physical interface, mounting, etc.

            CRITICAL RULES:
            - For axial fans, airflow direction A vs V (even vs odd 12th digit in product code) blurs 180 degrees reversed airflow. This makes direct substitution INCOMPATIBLE.
            - S and W are A-axial fans with accessories (S = guard grille, W = wall ring).
            - R, K, G, D are centrifugal fans with distinct mounting/housings.
            {{ebmPromptContext}}

            Return a structured JSON object strictly matching this schema:
            {
              "checks": [
                {
                  "dimension": "dimension checked (e.g. airflow_direction, voltage, diameter)",
                  "sourceValue": "source value or spec",
                  "targetRequirement": "target requirement or spec",
                  "status": "COMPATIBLE" or "INCOMPATIBLE" or "CONDITIONAL" or "UNKNOWN",
                  "reason": "explanation"
                }
              ],
              "status": "COMPATIBLE" or "INCOMPATIBLE" or "CONDITIONAL" or "UNKNOWN",
              "reason": "overall explanation"
            }

            Context chunks:
            {{string.Join("\n\n", allChunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Query: {{question}}
            """;

        var jsonResponse = await _llmService.GenerateCompletionAsync(prompt2, requireJson: true);
        CompatibilityResult? compatibilityResult = null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            compatibilityResult = JsonSerializer.Deserialize<CompatibilityResult>(jsonResponse, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Compatibility RAG output: {ex.Message}");
        }

        // Merge deterministic ebm domain checks into compatibility checks
        if (ebmEval != null)
        {
            var existingChecks = compatibilityResult?.Checks ?? new List<CompatibilityCheck>();

            foreach (var diff in ebmEval.Differences)
            {
                if (!existingChecks.Any(c => string.Equals(c.Dimension, diff.Dimension, StringComparison.OrdinalIgnoreCase)))
                {
                    existingChecks.Insert(0, new CompatibilityCheck(
                        Dimension: diff.Dimension,
                        SourceValue: diff.ValueA,
                        TargetRequirement: diff.ValueB,
                        Status: diff.IsCriticalIncompatibility ? "INCOMPATIBLE" : "CONDITIONAL",
                        Reason: diff.Explanation
                    ));
                }
            }

            var overallStatus = ebmEval.CriticalBlockers.Count > 0
                ? "INCOMPATIBLE"
                : (compatibilityResult?.Status ?? "COMPATIBLE");

            var overallReason = ebmEval.CriticalBlockers.Count > 0
                ? $"{ebmEval.Summary} {compatibilityResult?.Reason}".Trim()
                : (compatibilityResult?.Reason ?? ebmEval.Summary);

            compatibilityResult = new CompatibilityResult(existingChecks, overallStatus, overallReason, ebmEval);
        }

        var textResponse = "### Compatibility Analysis\n\n";
        if (compatibilityResult != null)
        {
            if (ebmEval?.CriticalBlockers.Count > 0)
            {
                textResponse += "### ⚠️ Kritisk Inkompatibilitet (ebm-papst Domæne)\n";
                foreach (var blocker in ebmEval.CriticalBlockers)
                {
                    textResponse += $"- ❌ **{blocker}**\n";
                }
                textResponse += "\n";
            }

            textResponse += $"**Overall Status: {compatibilityResult.Status}**\n";
            textResponse += $"**Reason**: {compatibilityResult.Reason}\n\n";
            textResponse += "#### Dimensions Checked:\n";
            foreach (var check in compatibilityResult.Checks)
            {
                textResponse += $"- **{check.Dimension}**: {check.Status}\n";
                textResponse += $"  - Source Value: {check.SourceValue}\n";
                textResponse += $"  - Target Requirement: {check.TargetRequirement}\n";
                textResponse += $"  - Reason: {check.Reason}\n";
            }
        }
        else
        {
            textResponse += jsonResponse;
        }

        return (textResponse, allChunks, compatibilityResult);
    }
}

public class DiagnosticWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public DiagnosticWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var allChunks = new List<DocumentChunk>();
        var entities = governorResult.Entities ?? new List<EntityInfo>();

        if (entities.Count > 0)
        {
            var searchTasks = entities.Select(entity =>
            {
                var query = $"{entity.Name} troubleshooting diagnostic error".Trim();
                return vectorStore.SearchAsync(query);
            }).ToList();

            var searchResults = await Task.WhenAll(searchTasks);
            foreach (var entityChunks in searchResults)
            {
                allChunks.AddRange(entityChunks);
            }
        }
        else
        {
            var fallbackChunks = initialSearchTask != null
                ? await initialSearchTask
                : await vectorStore.SearchAsync(question);
            allChunks.AddRange(fallbackChunks);
        }

        allChunks = allChunks.GroupBy(c => c.Id).Select(g => g.First()).ToList();

        var prompt = $$"""
            You are the Diagnostic RAG workflow executor for troubleshooting technical issues.
            Identify documented causes, diagnostic checks, expected results, and troubleshooting steps.
            Distinguish between:
            - documented cause
            - derived possibility
            - unsupported possibility

            Return a structured JSON object strictly matching this schema:
            {
              "causes": [
                {
                  "cause": "cause description",
                  "status": "SUPPORTED" or "DERIVED" or "UNSUPPORTED",
                  "evidence": ["source document references"],
                  "check": "how to verify",
                  "expectedResult": "expected value or state"
                }
              ],
              "troubleshootingSteps": [
                "step 1",
                "step 2"
              ]
            }

            Context chunks:
            {{string.Join("\n\n", allChunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Query: {{question}}
            """;

        var jsonResponse = await _llmService.GenerateCompletionAsync(prompt, requireJson: true);
        DiagnosticResult? diagnosticResult = null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            diagnosticResult = JsonSerializer.Deserialize<DiagnosticResult>(jsonResponse, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Diagnostic RAG output: {ex.Message}");
        }

        var textResponse = "### Diagnostic Analysis\n\n";
        if (diagnosticResult != null)
        {
            textResponse += "#### Potential Causes:\n";
            foreach (var cause in diagnosticResult.Causes)
            {
                textResponse += $"- **{cause.Cause}** ({cause.Status})\n";
                textResponse += $"  - Check: {cause.Check}\n";
                textResponse += $"  - Expected: {cause.ExpectedResult}\n";
                if (cause.Evidence != null && cause.Evidence.Count > 0)
                {
                    textResponse += $"  - Evidence: {string.Join(", ", cause.Evidence)}\n";
                }
            }

            textResponse += "\n#### Troubleshooting Steps:\n";
            foreach (var step in diagnosticResult.TroubleshootingSteps)
            {
                textResponse += $"- {step}\n";
            }
        }
        else
        {
            textResponse += jsonResponse;
        }

        return (textResponse, allChunks, diagnosticResult);
    }
}

public class CalculationWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public CalculationWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        // For calculations, search for formulas and parameters
        var chunks = (initialSearchTask != null ? await initialSearchTask : await vectorStore.SearchAsync(question)).ToList();

        var prompt = $$"""
            You are the Calculation RAG workflow executor.
            Your job is to identify the parameters and formulas required from technical documents, and perform the calculation.
            Only perform calculations based on documented parameters.

            Return a JSON object or clear markdown representing the calculation, including:
            - Formula
            - Inputs (with source document references)
            - Step-by-step math
            - Final result
            - CRITICAL LANGUAGE REQUIREMENT: You MUST ALWAYS write the response and explanations in the EXACT same language as the user's question. If the language cannot be detected (e.g. only numbers, formulas, or product codes), write in Danish (Dansk).

            Context chunks:
            {{string.Join("\n\n", chunks.Select(c => $"[Source: {c.FileName ?? c.DocumentId}] {c.Text}"))}}

            Query: {{question}}
            """;

        var response = await _llmService.GenerateCompletionAsync(prompt, requireJson: false);
        return (response, chunks, null);
    }
}

public class DesignWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public DesignWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var chunks = (initialSearchTask != null ? await initialSearchTask : await vectorStore.SearchAsync(question)).ToList();
        var answer = await _llmService.GenerateResponseAsync(question, chunks);
        return (answer, chunks, null);
    }
}

public class ClarificationWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;

    public ClarificationWorkflowExecutor(ILlmService llmService)
    {
        _llmService = llmService;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var prompt = $"The user asked: '{question}'. This is incomplete or unclear. Clarification reason: '{governorResult.ClarificationReason}'. Please ask the user to clarify or provide the missing details. CRITICAL LANGUAGE REQUIREMENT: You MUST formulate your response in the EXACT same language as the user's question (or in Danish if the language cannot be detected).";
        var answer = await _llmService.GenerateCompletionAsync(prompt, requireJson: false);
        return (answer, new List<DocumentChunk>(), null);
    }
}

public class OverviewWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService _llmService;
    private readonly IEbmProductCodeParser _ebmParser;
    private readonly IDocumentRepository? _documentRepository;

    public OverviewWorkflowExecutor(
        ILlmService llmService,
        IEbmProductCodeParser? ebmParser = null,
        IDocumentRepository? documentRepository = null)
    {
        _llmService = llmService;
        _ebmParser = ebmParser ?? new EbmProductCodeParser();
        _documentRepository = documentRepository;
    }

    public async Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var (allDocsDict, _) = await CatalogCacheHelper.GetCatalogDocumentsAndProductsAsync(vectorStore, _documentRepository, _ebmParser);

        // Search for relevant introductory / summary chunks
        var sampleChunks = (await vectorStore.SearchAsync(question, limit: 10)).ToList();

        var catalogItems = new List<CatalogItem>();
        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in allDocsDict.Values)
        {
            var docName = !string.IsNullOrWhiteSpace(doc.FileName) ? doc.FileName : doc.Id;
            var extracted = _ebmParser.ExtractProductsFromText(docName);

            if (extracted.Count > 0)
            {
                foreach (var prod in extracted)
                {
                    if (seenModels.Add(prod.CleanCode))
                    {
                        var category = prod.FanFamily switch
                        {
                            FanFamilyType.Axial => "Aksialventilatorer (Axial Fans)",
                            FanFamilyType.Centrifugal => "Centrifugalventilatorer & RadiPac (Centrifugal Fans)",
                            FanFamilyType.CompactOrSpecial => "Kompaktblæsere & DC-ventilatorer (Compact Fans)",
                            _ => "Øvrige ventilatorer og moduler"
                        };

                        catalogItems.Add(new CatalogItem(
                            ModelName: prod.RawCode,
                            Category: category,
                            Technology: prod.MotorDescription,
                            DiameterMm: prod.ImpellerDiameterMm,
                            Airflow: prod.AirflowDescription,
                            DocumentSource: docName,
                            ParsedInfo: prod,
                            Phase: doc.Phase,
                            NominalVoltage: doc.NominalVoltage,
                            VoltageRange: doc.VoltageRange
                        ));
                    }
                }
            }
            else
            {
                var cleanName = System.IO.Path.GetFileNameWithoutExtension(docName);
                if (seenModels.Add(cleanName))
                {
                    catalogItems.Add(new CatalogItem(
                        ModelName: cleanName,
                        Category: "Dokumenterede modeller & datablade",
                        Technology: "Se datablad for motorspecifikationer",
                        DiameterMm: null,
                        Airflow: "Se datablad",
                        DocumentSource: docName,
                        ParsedInfo: null,
                        Phase: doc.Phase,
                        NominalVoltage: doc.NominalVoltage,
                        VoltageRange: doc.VoltageRange
                    ));
                }
            }
        }

        // Also extract from chunks
        foreach (var chunk in sampleChunks)
        {
            var chunkProducts = _ebmParser.ExtractProductsFromText(chunk.Text);
            foreach (var prod in chunkProducts)
            {
                if (seenModels.Add(prod.CleanCode))
                {
                    var category = prod.FanFamily switch
                    {
                        FanFamilyType.Axial => "Aksialventilatorer (Axial Fans)",
                        FanFamilyType.Centrifugal => "Centrifugalventilatorer & RadiPac (Centrifugal Fans)",
                        FanFamilyType.CompactOrSpecial => "Kompaktblæsere & DC-ventilatorer (Compact Fans)",
                        _ => "Øvrige ventilatorer og moduler"
                    };

                    catalogItems.Add(new CatalogItem(
                        ModelName: prod.RawCode,
                        Category: category,
                        Technology: prod.MotorDescription,
                        DiameterMm: prod.ImpellerDiameterMm,
                        Airflow: prod.AirflowDescription,
                        DocumentSource: chunk.FileName ?? chunk.DocumentId,
                        ParsedInfo: prod
                    ));
                }
            }
        }

        var categorizedGroups = catalogItems
            .GroupBy(i => i.Category)
            .ToDictionary(g => g.Key, g => g.ToList());

        var catalogResult = new CatalogOverviewResult(
            TotalDocuments: allDocsDict.Count,
            TotalModels: catalogItems.Count,
            Categories: categorizedGroups
        );

        var lang = ResolveLanguage(question);
        var voltageFilter = ElectricalQueryExpander.ExtractVoltageFilter(question);
        List<CatalogItem>? filteredItems = null;
        if (voltageFilter != null)
        {
            filteredItems = catalogItems.Where(i => voltageFilter.Matches(i.Phase, i.NominalVoltage, i.VoltageRange)).ToList();
        }

        // Generic Diameter Filter (e.g. "Ø280", "280 mm", "diameter 280", "størrelse 280" - never bare "på <number>" as that matches "på 230 V")
        var diamMatch = Regex.Match(question, @"(?:Ø\s*(\d{3})\b|\b(\d{3})\s*mm\b|\b(?:størrelse|diameter|hjul)\s*(?:Ø)?\s*(\d{3})\b)", RegexOptions.IgnoreCase);
        int? targetDiameter = null;
        if (diamMatch.Success)
        {
            var val = !string.IsNullOrWhiteSpace(diamMatch.Groups[1].Value) ? diamMatch.Groups[1].Value
                    : (!string.IsNullOrWhiteSpace(diamMatch.Groups[2].Value) ? diamMatch.Groups[2].Value : diamMatch.Groups[3].Value);
            if (int.TryParse(val, out var d)) targetDiameter = d;
        }

        if (targetDiameter.HasValue)
        {
            var candidatesToFilter = filteredItems ?? catalogItems;
            filteredItems = candidatesToFilter.Where(i => i.DiameterMm == targetDiameter.Value).ToList();
        }

        bool hasFilter = voltageFilter != null || targetDiameter.HasValue;
        string filterHeadingDa = "";
        string filterHeadingDe = "";
        string filterHeadingEn = "";

        if (voltageFilter != null && targetDiameter.HasValue)
        {
            filterHeadingDa = $"{voltageFilter.RawMatch} og Ø{targetDiameter.Value} mm";
            filterHeadingDe = $"{voltageFilter.RawMatch} und Ø{targetDiameter.Value} mm";
            filterHeadingEn = $"{voltageFilter.RawMatch} and Ø{targetDiameter.Value} mm";
        }
        else if (voltageFilter != null)
        {
            filterHeadingDa = voltageFilter.RawMatch;
            filterHeadingDe = voltageFilter.RawMatch;
            filterHeadingEn = voltageFilter.RawMatch;
        }
        else if (targetDiameter.HasValue)
        {
            filterHeadingDa = $"Ø{targetDiameter.Value} mm";
            filterHeadingDe = $"Ø{targetDiameter.Value} mm";
            filterHeadingEn = $"Ø{targetDiameter.Value} mm";
        }

        var draftSb = new StringBuilder();
        if (lang == ContentLanguage.Danish)
        {
            if (hasFilter)
            {
                draftSb.AppendLine($"### ebm-papst modeller til {filterHeadingDa}\n");
                if (filteredItems != null && filteredItems.Count > 0)
                {
                    draftSb.AppendLine($"Følgende **{filteredItems.Count} model(ler)** i kataloget matcher kriterierne:\n");
                    var displayFiltered = filteredItems.Take(20).ToList();
                    foreach (var item in displayFiltered)
                    {
                        var diamStr = item.DiameterMm.HasValue ? $", Ø{item.DiameterMm} mm" : "";
                        var voltStr = !string.IsNullOrWhiteSpace(item.NominalVoltage) ? $", {item.Phase ?? ""} {item.NominalVoltage}" : "";
                        var rangeStr = !string.IsNullOrWhiteSpace(item.VoltageRange) ? $" (område: {item.VoltageRange})" : "";
                        draftSb.AppendLine($"- **{item.ModelName}**: {item.Technology}{diamStr}{voltStr}{rangeStr} *(Kilde: {item.DocumentSource})*");
                    }
                    if (filteredItems.Count > 20)
                    {
                        draftSb.AppendLine($"- *... og {filteredItems.Count - 20} yderligere modeller i kataloget, der matcher kriterierne.*");
                        draftSb.AppendLine("\n> **Tip**: Du kan indsnævre listen ved at angive en specifik diameter (f.eks. Ø280, Ø400, Ø560 mm) eller ønsket luftmængde (m³/t).");
                    }
                    draftSb.AppendLine();
                }
                else
                {
                    draftSb.AppendLine($"Der findes p.t. ingen dokumenterede modeller i den lokale database, der matcher kriterierne for {filterHeadingDa}.\n");
                }
            }
            else
            {
                draftSb.AppendLine("### ebm-papst Ventilator- og Produktoversigt\n");
                draftSb.AppendLine($"Systemet indeholder **{catalogResult.TotalModels} modeller** fordelt over **{catalogResult.TotalDocuments} datablade/dokumenter**:\n");

                foreach (var (category, items) in catalogResult.Categories)
                {
                    draftSb.AppendLine($"#### {category} ({items.Count} modeller)");
                    var displayItems = items.Take(15).ToList();
                    foreach (var item in displayItems)
                    {
                        var diamStr = item.DiameterMm.HasValue ? $", Ø{item.DiameterMm} mm" : "";
                        var airflowStr = !string.IsNullOrWhiteSpace(item.Airflow) ? $", {item.Airflow}" : "";
                        var voltStr = !string.IsNullOrWhiteSpace(item.NominalVoltage) ? $", {item.Phase ?? ""} {item.NominalVoltage}" : "";
                        draftSb.AppendLine($"- **{item.ModelName}**: {item.Technology}{diamStr}{airflowStr}{voltStr} *(Kilde: {item.DocumentSource})*");
                    }
                    if (items.Count > 15)
                    {
                        draftSb.AppendLine($"- *... og {items.Count - 15} yderligere modeller i denne kategori.*");
                    }
                    draftSb.AppendLine();
                }

                draftSb.AppendLine("#### Typenøgle og Parametre");
                draftSb.AppendLine("- **Ventilatortype**: 1. bogstav (A = Aksial grundmodel, S = med beskyttelsesgitter, W = i vægring, R = Løst centrifugalhjul, K = RadiPac i ramme, G/D = Sneglehus).");
                draftSb.AppendLine("- **Motorteknologi**: 2.-3. tegn (3G = EC energieffektiv motor; AC angiver poltal og 1~ 230V / 3~ 400V).");
                draftSb.AppendLine("- **Diameter**: 4.-6. ciffer angiver impellerdiameter i mm.");
                draftSb.AppendLine("- **Luftretning (Aksial)**: 12. ciffer (lige = Luftretning A, ulige = Luftretning V).");
            }
        }
        else if (lang == ContentLanguage.German)
        {
            if (hasFilter)
            {
                draftSb.AppendLine($"### ebm-papst Modelle für {filterHeadingDe}\n");
                if (filteredItems != null && filteredItems.Count > 0)
                {
                    draftSb.AppendLine($"Folgende **{filteredItems.Count} Modell(e)** im Katalog entsprechen den Kriterien:\n");
                    var displayFiltered = filteredItems.Take(20).ToList();
                    foreach (var item in displayFiltered)
                    {
                        var diamStr = item.DiameterMm.HasValue ? $", Ø{item.DiameterMm} mm" : "";
                        var voltStr = !string.IsNullOrWhiteSpace(item.NominalVoltage) ? $", {item.Phase ?? ""} {item.NominalVoltage}" : "";
                        var rangeStr = !string.IsNullOrWhiteSpace(item.VoltageRange) ? $" (Bereich: {item.VoltageRange})" : "";
                        draftSb.AppendLine($"- **{item.ModelName}**: {item.Technology}{diamStr}{voltStr}{rangeStr} *(Quelle: {item.DocumentSource})*");
                    }
                    if (filteredItems.Count > 20)
                    {
                        draftSb.AppendLine($"- *... und {filteredItems.Count - 20} weitere Modelle im Katalog, die den Kriterien entsprechen.*");
                        draftSb.AppendLine("\n> **Tipp**: Sie können die Liste eingrenzen, indem Sie einen bestimmten Durchmesser (z.B. Ø280, Ø400, Ø560 mm) oder den gewünschten Luftvolumenstrom (m³/h) angeben.");
                    }
                    draftSb.AppendLine();
                }
                else
                {
                    draftSb.AppendLine($"In der lokalen Datenbank sind derzeit keine dokumentierten Modelle für {filterHeadingDe} registriert.\n");
                }
            }
            else
            {
                draftSb.AppendLine("### ebm-papst Ventilator- und Produktübersicht\n");
                draftSb.AppendLine($"Das System indexiert derzeit **{catalogResult.TotalModels} Ventilatormodelle** in **{catalogResult.TotalDocuments} technischen Datenblättern/Dokumenten**:\n");

                foreach (var (category, items) in catalogResult.Categories)
                {
                    var deCategory = FormatCategoryInGerman(category);
                    draftSb.AppendLine($"#### {deCategory} ({items.Count} Modelle)");
                    var displayItems = items.Take(15).ToList();
                    foreach (var item in displayItems)
                    {
                        var diamStr = item.DiameterMm.HasValue ? $", Ø{item.DiameterMm} mm" : "";
                        var technology = item.ParsedInfo != null
                            ? item.ParsedInfo.GetMotorDescription(ContentLanguage.German)
                            : FormatNonParsedTechnologyInGerman(item.Technology);
                        var airflow = item.ParsedInfo != null
                            ? item.ParsedInfo.GetAirflowDescription(ContentLanguage.German)
                            : FormatNonParsedAirflowInGerman(item.Airflow);
                        var airflowStr = !string.IsNullOrWhiteSpace(airflow) ? $", {airflow}" : "";
                        var voltStr = !string.IsNullOrWhiteSpace(item.NominalVoltage) ? $", {item.Phase ?? ""} {item.NominalVoltage}" : "";
                        draftSb.AppendLine($"- **{item.ModelName}**: {technology}{diamStr}{airflowStr}{voltStr} *(Quelle: {item.DocumentSource})*");
                    }
                    if (items.Count > 15)
                    {
                        draftSb.AppendLine($"- *... und {items.Count - 15} weitere Modelle in dieser Kategorie.*");
                    }
                    draftSb.AppendLine();
                }

                draftSb.AppendLine("#### Typenschlüssel und Parameter");
                draftSb.AppendLine("- **Ventilatortype**: 1. Buchstabe (A = Axial-Grundmodell, S = mit Schutzgitter, W = im Wandring, R = Motorisiertes Laufrad, K = RadiPac im Tragrahmen, G/D = Spiralgehäuse).");
                draftSb.AppendLine("- **Motortechnologie**: 2.-3. Zeichen (3G = EC energiesparender Motor; AC gibt Polzahl und 1~ 230V / 3~ 400V an).");
                draftSb.AppendLine("- **Durchmesser**: 4.-6. Ziffer gibt den Laufraddurchmesser in mm an.");
                draftSb.AppendLine("- **Luftförderrichtung (Axial)**: 12. Ziffer (gerade = Förderrichtung A, ungerade = Förderrichtung V).");
            }
        }
        else
        {
            if (hasFilter)
            {
                draftSb.AppendLine($"### ebm-papst Models for {filterHeadingEn}\n");
                if (filteredItems != null && filteredItems.Count > 0)
                {
                    draftSb.AppendLine($"The following **{filteredItems.Count} model(s)** in the catalog match the criteria:\n");
                    var displayFiltered = filteredItems.Take(20).ToList();
                    foreach (var item in displayFiltered)
                    {
                        var diamStr = item.DiameterMm.HasValue ? $", Ø{item.DiameterMm} mm" : "";
                        var voltStr = !string.IsNullOrWhiteSpace(item.NominalVoltage) ? $", {item.Phase ?? ""} {item.NominalVoltage}" : "";
                        var rangeStr = !string.IsNullOrWhiteSpace(item.VoltageRange) ? $" (range: {item.VoltageRange})" : "";
                        draftSb.AppendLine($"- **{item.ModelName}**: {item.Technology}{diamStr}{voltStr}{rangeStr} *(Source: {item.DocumentSource})*");
                    }
                    if (filteredItems.Count > 20)
                    {
                        draftSb.AppendLine($"- *... and {filteredItems.Count - 20} additional models in the catalog matching the criteria.*");
                        draftSb.AppendLine("\n> **Tip**: You can narrow down the list by specifying a diameter (e.g. Ø280, Ø400, Ø560 mm) or required airflow (m³/h).");
                    }
                    draftSb.AppendLine();
                }
                else
                {
                    draftSb.AppendLine($"Currently no documented models in the local database match the criteria for {filterHeadingEn}.\n");
                }
            }
            else
            {
                draftSb.AppendLine("### ebm-papst Ventilator and Product Overview\n");
                draftSb.AppendLine($"The system currently indexes **{catalogResult.TotalModels} ventilator models** across **{catalogResult.TotalDocuments} technical datasheets/documents**:\n");

                foreach (var (category, items) in catalogResult.Categories)
                {
                    var enCategory = FormatCategoryInEnglish(category);
                    draftSb.AppendLine($"#### {enCategory} ({items.Count} models)");
                    var displayItems = items.Take(15).ToList();
                    foreach (var item in displayItems)
                    {
                        var diamStr = item.DiameterMm.HasValue ? $", Ø{item.DiameterMm} mm" : "";
                        var technology = item.ParsedInfo != null
                            ? item.ParsedInfo.GetMotorDescription(ContentLanguage.English)
                            : FormatNonParsedTechnologyInEnglish(item.Technology);
                        var airflow = item.ParsedInfo != null
                            ? item.ParsedInfo.GetAirflowDescription(ContentLanguage.English)
                            : FormatNonParsedAirflowInEnglish(item.Airflow);
                        var airflowStr = !string.IsNullOrWhiteSpace(airflow) ? $", {airflow}" : "";
                        var voltStr = !string.IsNullOrWhiteSpace(item.NominalVoltage) ? $", {item.Phase ?? ""} {item.NominalVoltage}" : "";
                        draftSb.AppendLine($"- **{item.ModelName}**: {technology}{diamStr}{airflowStr}{voltStr} *(Source: {item.DocumentSource})*");
                    }
                    if (items.Count > 15)
                    {
                        draftSb.AppendLine($"- *... and {items.Count - 15} additional models in this category.*");
                    }
                    draftSb.AppendLine();
                }

                draftSb.AppendLine("#### Product Designation Key & Parameters");
                draftSb.AppendLine("- **Fan Type**: 1st char (A = Axial base, S = with guard grille, W = in wall ring, R = Motorized impeller, K = RadiPac in bracket, G/D = Scroll housing).");
                draftSb.AppendLine("- **Motor Technology**: 2nd-3rd chars (3G = EC energy-efficient motor; AC indicates pole count and 1~ 230V / 3~ 400V).");
                draftSb.AppendLine("- **Diameter**: 4th-6th chars specify impeller diameter in mm.");
                draftSb.AppendLine("- **Airflow Direction (Axial)**: 12th char (even = Airflow A, odd = Airflow V).");
            }
        }

        if (filteredItems != null && filteredItems.Count > 0)
        {
            var matchingChunks = new List<DocumentChunk>();
            foreach (var item in filteredItems.Take(5))
            {
                var doc = allDocsDict.Values.FirstOrDefault(d =>
                    (!string.IsNullOrWhiteSpace(d.FileName) && d.FileName.Contains(item.ModelName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(d.ArticleId) && d.ArticleId.Contains(item.ModelName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(d.ProductCode) && d.ProductCode.Contains(item.ModelName, StringComparison.OrdinalIgnoreCase)));
                if (doc != null)
                {
                    try
                    {
                        var docChunks = (await vectorStore.GetChunksByDocumentAndIndicesAsync(doc.Id, new[] { 0, 1 })).ToList();
                        if (docChunks.Count == 0)
                        {
                            docChunks = (await vectorStore.GetAllChunksByDocumentIdAsync(doc.Id)).Take(2).ToList();
                        }
                        var enriched = docChunks.Select(c => c with
                        {
                            FileName = !string.IsNullOrWhiteSpace(c.FileName) ? c.FileName : doc.FileName,
                            FilePath = !string.IsNullOrWhiteSpace(c.FilePath) ? c.FilePath : doc.FilePath,
                            ArticleId = !string.IsNullOrWhiteSpace(c.ArticleId) ? c.ArticleId : doc.ArticleId
                        }).ToList();
                        matchingChunks.AddRange(enriched);
                    }
                    catch { }
                }
            }
            if (matchingChunks.Count > 0)
            {
                sampleChunks = matchingChunks.Take(8).ToList();
            }
        }

        var structuredDraft = draftSb.ToString().Trim();
        return (structuredDraft, sampleChunks, catalogResult);
    }

    private static ContentLanguage ResolveLanguage(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return ContentLanguage.Danish;

        var q = question.Trim();

        // 1. Definite German indicators (genuine German-only phrases and words)
        if (Regex.IsMatch(q, @"\b(welche(s|r|n)?|gibt\s+es|übersicht|uebersicht|lüfter|luefter|datenbl[äa]tter|zeige|guten\s+tag|danke|bitte|f[üu]r|k[öo]nnen\s+sie|kannst\s+du|deutsch(e|en)?)\b", RegexOptions.IgnoreCase))
        {
            return ContentLanguage.German;
        }

        // 2. Definite Danish indicators (prioritized for ebm-papst Danmark portal)
        if (Regex.IsMatch(q, @"\b(kan\s+du|finde?|hvilke?|hvad|hvor|findes|blæser(e)?|kompaktblæser(e)?|ventilatorer|kompaktventilatorer|datablade|oversigt|hjælp|tak|med|til|på|for|er\s+der|har\s+I|kan\s+man)\b", RegexOptions.IgnoreCase))
        {
            return ContentLanguage.Danish;
        }

        // 3. Definite English indicators
        if (Regex.IsMatch(q, @"\b(can\s+you|which|what|where|fans?|models?|overview|catalog(ue)?|list|show|available|any|datasheets?|find|link|help|please)\b", RegexOptions.IgnoreCase))
        {
            return ContentLanguage.English;
        }

        // 4. Statistical heuristic from DefaultLanguageDetector
        var detection = DefaultLanguageDetector.DetectLanguageHeuristic(q, null);
        if (string.Equals(detection.Language, "de", StringComparison.OrdinalIgnoreCase))
            return ContentLanguage.German;
        if (string.Equals(detection.Language, "da", StringComparison.OrdinalIgnoreCase))
            return ContentLanguage.Danish;
        if (string.Equals(detection.Language, "en", StringComparison.OrdinalIgnoreCase))
            return ContentLanguage.English;

        return detection.IsEnglish ? ContentLanguage.English : ContentLanguage.Danish;
    }

    private static string FormatCategoryInEnglish(string category)
    {
        return category switch
        {
            "Aksialventilatorer (Axial Fans)" => "Axial Fans",
            "Centrifugalventilatorer & RadiPac (Centrifugal Fans)" => "Centrifugal Fans & RadiPac",
            "Kompaktblæsere & DC-ventilatorer (Compact Fans)" => "Compact & DC Fans",
            "Øvrige ventilatorer og moduler" => "Other Fans and Modules",
            "Dokumenterede modeller & datablade" => "Documented Models & Datasheets",
            _ => category
        };
    }

    private static string FormatCategoryInGerman(string category)
    {
        return category switch
        {
            "Aksialventilatorer (Axial Fans)" => "Axialventilatoren",
            "Centrifugalventilatorer & RadiPac (Centrifugal Fans)" => "Radialventilatoren & RadiPac",
            "Kompaktblæsere & DC-ventilatorer (Compact Fans)" => "Kompaktlüfter & DC-Ventilatoren",
            "Øvrige ventilatorer og moduler" => "Sonstige Ventilatoren und Module",
            "Dokumenterede modeller & datablade" => "Dokumentierte Modelle & Datenblätter",
            _ => category
        };
    }

    private static string FormatNonParsedTechnologyInEnglish(string danishTech)
    {
        if (string.IsNullOrWhiteSpace(danishTech)) return "See datasheet for motor specifications";
        if (danishTech.Contains("datablad", StringComparison.OrdinalIgnoreCase)) return "See datasheet for motor specifications";
        if (danishTech.Contains("Kompaktmotor", StringComparison.OrdinalIgnoreCase) || danishTech.Contains("Elektronisk DC", StringComparison.OrdinalIgnoreCase)) return "Electronic DC / Compact Motor";
        if (danishTech.Contains("EC-motor", StringComparison.OrdinalIgnoreCase)) return "EC motor";
        if (danishTech.Contains("AC-motor", StringComparison.OrdinalIgnoreCase)) return "AC motor";
        return danishTech;
    }

    private static string FormatNonParsedTechnologyInGerman(string danishTech)
    {
        if (string.IsNullOrWhiteSpace(danishTech)) return "Siehe Datenblatt für Motorspezifikationen";
        if (danishTech.Contains("datablad", StringComparison.OrdinalIgnoreCase)) return "Siehe Datenblatt für Motorspezifikationen";
        if (danishTech.Contains("Kompaktmotor", StringComparison.OrdinalIgnoreCase) || danishTech.Contains("Elektronisk DC", StringComparison.OrdinalIgnoreCase)) return "Elektronischer DC- / Kompaktmotor";
        if (danishTech.Contains("EC-motor", StringComparison.OrdinalIgnoreCase)) return "EC-Motor";
        if (danishTech.Contains("AC-motor", StringComparison.OrdinalIgnoreCase)) return "AC-Motor";
        return danishTech;
    }

    private static string FormatNonParsedAirflowInEnglish(string danishAirflow)
    {
        if (string.IsNullOrWhiteSpace(danishAirflow)) return string.Empty;
        if (danishAirflow.Contains("datablad", StringComparison.OrdinalIgnoreCase)) return "See datasheet";
        if (danishAirflow.Contains("Se specifik montage", StringComparison.OrdinalIgnoreCase)) return "See datasheet for specific mounting (over struts / ribs)";
        if (danishAirflow.Contains("over ribber / struts", StringComparison.OrdinalIgnoreCase)) return "Airflow over struts / ribs";
        if (danishAirflow.Contains("Aksial indsugning, radial udblæsning", StringComparison.OrdinalIgnoreCase)) return "Axial intake, radial discharge";
        if (danishAirflow.Contains("Radial luftafgang", StringComparison.OrdinalIgnoreCase)) return "Radial air discharge";
        return danishAirflow;
    }

    private static string FormatNonParsedAirflowInGerman(string danishAirflow)
    {
        if (string.IsNullOrWhiteSpace(danishAirflow)) return string.Empty;
        if (danishAirflow.Contains("datablad", StringComparison.OrdinalIgnoreCase)) return "Siehe Datenblatt";
        if (danishAirflow.Contains("Se specifik montage", StringComparison.OrdinalIgnoreCase)) return "Spezifische Montage siehe Datenblatt (über Stege)";
        if (danishAirflow.Contains("over ribber / struts", StringComparison.OrdinalIgnoreCase)) return "Luftförderung über Stege";
        if (danishAirflow.Contains("Aksial indsugning, radial udblæsning", StringComparison.OrdinalIgnoreCase)) return "Axial saugend, radial ausblasend";
        if (danishAirflow.Contains("Radial luftafgang", StringComparison.OrdinalIgnoreCase)) return "Radiale Luftausblasung";
        return danishAirflow;
    }
}

public class GreetingWorkflowExecutor : IWorkflowExecutor
{
    private readonly ILlmService? _llmService;

    public GreetingWorkflowExecutor(ILlmService? llmService = null)
    {
        _llmService = llmService;
    }

    public Task<(string DraftResponse, List<DocumentChunk> ContextChunks, object? WorkflowData)> ExecuteAsync(
        string question,
        InputGovernorResult governorResult,
        IVectorStore vectorStore,
        Task<IEnumerable<DocumentChunk>>? initialSearchTask = null
    )
    {
        var q = question.Trim().ToLowerInvariant();
        var lang = ResolveGreetingLanguage(q);

        string response;
        if (lang == ContentLanguage.German)
        {
            if (q.Contains("dank") || q.Contains("vielen dank"))
            {
                response = "Gern geschehen! Geben Sie einfach Bescheid, wenn Sie weitere Fragen zu ebm-papst Ventilatoren oder Datenblättern haben.";
            }
            else if (q.Contains("wer bist du") || q.Contains("was kannst du") || q.Contains("hilfe") || q.Contains("helfen"))
            {
                response = "Ich bin Ihr technischer Assistent für ebm-papst Ventilatoren und Datenblätter. Ich kann Ihnen helfen, technische Spezifikationen (Spannung, Luftmenge, Abmessungen) zu finden, Modelle zu vergleichen, Ersatzprodukte zu ermitteln oder technische Berechnungen durchzuführen. Wie kann ich Ihnen helfen?";
            }
            else
            {
                response = "Hallo! Ich bin bereit, Ihre Fragen auf Basis der verfügbaren ebm-papst Datenblätter zu beantworten. Sie können nach bestimmten Modellen fragen, Ventilatoren vergleichen, Ersatzprodukte suchen oder Berechnungen durchführen. Wie kann ich Ihnen heute helfen?";
            }
        }
        else if (lang == ContentLanguage.Danish)
        {
            if (q.Contains("tak"))
            {
                response = "Selv tak! Sig endelig til hvis du har flere spørgsmål til ebm-papst ventilatorer eller datablade.";
            }
            else if (q.Contains("hvem er du") || q.Contains("hvad kan du") || q.Contains("hjælp"))
            {
                response = "Jeg er din intelligente tekniske assistent for ebm-papst ventilatorer og datablade. Jeg kan hjælpe dig med at finde tekniske specifikationer (spænding, luftmængde, dimensioner), sammenligne to modeller, finde erstatningsventilatorer eller udføre tekniske beregninger. Hvad vil du gerne have hjælp til?";
            }
            else
            {
                response = "Hej! Jeg er klar til at besvare dine spørgsmål baseret på de tilgængelige ebm-papst datablade. Du kan spørge om specifikke modeller, sammenligne ventilatorer, finde erstatningsprodukter eller lave beregninger. Hvad kan jeg hjælpe dig med i dag?";
            }
        }
        else
        {
            if (q.Contains("thank"))
            {
                response = "You're welcome! Let me know if you need any more help with ebm-papst fans, models, or datasheets.";
            }
            else if (q.Contains("who are you") || q.Contains("what can you do") || q.Contains("help"))
            {
                response = "I am your technical assistant for ebm-papst datasheets and ventilation systems. I can help you look up technical specifications (voltage, airflow, dimensions), compare fan models, find replacements, or perform engineering calculations. How can I help you today?";
            }
            else
            {
                response = "Hello! I am ready to answer your questions based on the available ebm-papst datasheets. You can ask about specific models, compare fans, look for replacement products, or run calculations. What can I help you with today?";
            }
        }

        return Task.FromResult((response, new List<DocumentChunk>(), (object?)null));
    }

    private static ContentLanguage ResolveGreetingLanguage(string q)
    {
        if (q.Contains("hallo") || q.Contains("guten") || q.Contains("dank") || q.Contains("servus") || q.Contains("grüß") || q.Contains("gruess") || q.Contains("moin") || q.Contains("wer bist") || q.Contains("was kannst"))
        {
            return ContentLanguage.German;
        }

        if (q.Contains("hej") || q.Contains("dav") || q.Contains("god") || q.Contains("hva") || q.Contains("tak") || q.Contains("hvem") || q.Contains("hjælp") || q.Contains("mojn"))
        {
            return ContentLanguage.Danish;
        }

        if (q.Contains("hello") || q.Contains("hi") || q.Contains("thank") || q.Contains("who are") || q.Contains("what can") || q.Contains("help"))
        {
            return ContentLanguage.English;
        }

        var detection = DefaultLanguageDetector.DetectLanguageHeuristic(q, null);
        if (string.Equals(detection.Language, "de", StringComparison.OrdinalIgnoreCase)) return ContentLanguage.German;
        if (string.Equals(detection.Language, "da", StringComparison.OrdinalIgnoreCase)) return ContentLanguage.Danish;
        return ContentLanguage.English;
    }
}
