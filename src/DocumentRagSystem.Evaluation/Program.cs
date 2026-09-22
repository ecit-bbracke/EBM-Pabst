using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DocumentRagSystem.Evaluation;

public static class Program
{
    private static string _apiBase = "https://localhost:7251";
    private static string _dataDir = "data/Generelt";
    private static string _docsDir = "docs";
    private static string _outputPdf = "test.pdf";

    public static async Task<int> Main(string[] args)
    {
        // Parse command-line arguments if provided
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--api" || args[i] == "-a" || args[i] == "--api-base") && i + 1 < args.Length)
            {
                _apiBase = args[++i].TrimEnd('/');
            }
            else if ((args[i] == "--data" || args[i] == "-d" || args[i] == "--data-dir") && i + 1 < args.Length)
            {
                _dataDir = args[++i];
            }
            else if ((args[i] == "--docs" || args[i] == "--docs-dir") && i + 1 < args.Length)
            {
                _docsDir = args[++i];
            }
            else if ((args[i] == "--output" || args[i] == "-o" || args[i] == "--output-pdf") && i + 1 < args.Length)
            {
                _outputPdf = args[++i];
            }
            else if (args[i] == "--help" || args[i] == "-h")
            {
                PrintHelp();
                return 0;
            }
        }

        Console.WriteLine("=================================================");
        Console.WriteLine("     Document RAG System - Evaluation Runner     ");
        Console.WriteLine("=================================================");
        Console.WriteLine($"API Base URL : {_apiBase}");
        Console.WriteLine($"Data Dir     : {_dataDir}");
        Console.WriteLine($"Docs Dir     : {_docsDir}");
        Console.WriteLine($"Output PDF   : {_outputPdf}");
        Console.WriteLine();

        // Configure QuestPDF Community license
        QuestPDF.Settings.License = LicenseType.Community;

        // Resolve workspace paths
        var resolvedDataDir = ResolveDirectory(_dataDir);
        var resolvedDocsDir = ResolveDirectory(_docsDir);
        var resolvedOutputPath = Path.GetFullPath(_outputPdf);

        // Setup HTTP client that ignores SSL certificate errors (for local dev self-signed certs)
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_apiBase),
            Timeout = TimeSpan.FromMinutes(3)
        };

        // 1. Fetch currently uploaded documents to avoid redundant work
        Console.WriteLine("Fetching already uploaded documents from API...");
        var uploadedFilenames = await GetUploadedDocumentFilenamesAsync(httpClient);
        Console.WriteLine($"Already uploaded ({uploadedFilenames.Count}): [{string.Join(", ", uploadedFilenames)}]");

        // 2. Find all 'Spørgsmål_*.md' files in docs/
        if (!Directory.Exists(resolvedDocsDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Docs directory '{resolvedDocsDir}' not found!");
            Console.ResetColor();
            return 1;
        }

        var mdFiles = Directory.GetFiles(resolvedDocsDir, "Spørgsmål_*.md")
            .OrderBy(f => f)
            .ToList();

        if (mdFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"No Spørgsmål files found in '{resolvedDocsDir}'!");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"Found {mdFiles.Count} Spørgsmål files to process.");
        var results = new List<DocumentEvaluationResult>();

        // 3. Process each Spørgsmål document
        foreach (var mdFile in mdFiles)
        {
            var docName = Path.GetFileName(mdFile);
            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine($"Processing Spørgsmål document: {docName}");

            // Match with PDF in data/Generelt
            var pdfPath = FindMatchingPdf(mdFile, resolvedDataDir);
            if (pdfPath == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: No matching PDF found for {docName} in '{resolvedDataDir}'");
                Console.ResetColor();
                continue;
            }

            var pdfName = Path.GetFileName(pdfPath);
            Console.WriteLine($"Matching PDF: {pdfName}");

            // Check if PDF is already uploaded, if not upload it
            if (!uploadedFilenames.Contains(pdfName))
            {
                Console.WriteLine($"PDF '{pdfName}' not in system. Uploading now...");
                var uploadSuccess = await UploadPdfAsync(httpClient, pdfPath);
                if (!uploadSuccess)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Skipping queries for {docName} due to upload failure.");
                    Console.ResetColor();
                    continue;
                }
                uploadedFilenames.Add(pdfName);
            }
            else
            {
                Console.WriteLine($"PDF '{pdfName}' already uploaded and indexed.");
            }

            // Read JSON Q&As from the markdown file
            List<QuestionItem>? questionsData = null;
            try
            {
                var jsonText = await File.ReadAllTextAsync(mdFile);
                questionsData = JsonSerializer.Deserialize<List<QuestionItem>>(jsonText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to parse JSON from {docName}: {ex.Message}");
                Console.ResetColor();
                continue;
            }

            if (questionsData == null || questionsData.Count == 0)
            {
                Console.WriteLine($"No questions found in {docName}.");
                continue;
            }

            var qaPairs = new List<QaPairResult>();

            // 4. Query each question and record response
            foreach (var qItem in questionsData)
            {
                var qId = qItem.Id.ValueKind switch
                {
                    JsonValueKind.Number => qItem.Id.GetInt64().ToString(CultureInfo.InvariantCulture),
                    JsonValueKind.String => qItem.Id.GetString() ?? "",
                    _ => qItem.Id.ToString()
                };

                var questionText = qItem.Question ?? string.Empty;
                var expectedAnswer = qItem.Answer ?? string.Empty;
                var difficulty = string.IsNullOrWhiteSpace(qItem.Difficulty) ? "N/A" : qItem.Difficulty;
                var topic = string.IsNullOrWhiteSpace(qItem.Topic) ? "N/A" : qItem.Topic;

                var preview = questionText.Length > 60 ? questionText.Substring(0, 60) + "..." : questionText;
                Console.WriteLine($"Querying Q{qId}: {preview}");

                var apiAnswerRaw = await QueryApiAsync(httpClient, questionText);
                string plainAnswer;

                if (apiAnswerRaw != null)
                {
                    plainAnswer = HtmlToPlainText(apiAnswerRaw);
                }
                else
                {
                    plainAnswer = "ERROR: Failed to retrieve answer from local Web API.";
                }

                qaPairs.Add(new QaPairResult
                {
                    Id = qId,
                    Question = questionText,
                    ExpectedAnswer = expectedAnswer,
                    ApiAnswer = plainAnswer,
                    Difficulty = difficulty,
                    Topic = topic
                });
            }

            results.Add(new DocumentEvaluationResult
            {
                DocumentName = docName,
                PdfName = pdfName,
                QaPairs = qaPairs
            });
        }

        // 5. Generate PDF report
        Console.WriteLine();
        Console.WriteLine($"Generating evaluation PDF report '{resolvedOutputPath}'...");
        try
        {
            var reportDoc = new EvaluationReportDocument(results, DateTime.Now);
            reportDoc.GeneratePdf(resolvedOutputPath);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PDF successfully generated at: {resolvedOutputPath}");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error generating PDF: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run --project src/DocumentRagSystem.Evaluation [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -a, --api-base <url>    Base URL of the Web API (default: https://localhost:7251)");
        Console.WriteLine("  -d, --data-dir <path>   Path to the PDF data directory (default: data/Generelt)");
        Console.WriteLine("  --docs-dir <path>       Path to the Spørgsmål markdown files (default: docs)");
        Console.WriteLine("  -o, --output <file>     Output path for the generated PDF report (default: test.pdf)");
        Console.WriteLine("  -h, --help              Show this help message");
    }

    private static string ResolveDirectory(string relativePath)
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, relativePath);
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent == current || string.IsNullOrEmpty(parent))
                break;
            current = parent;
        }
        return Path.GetFullPath(relativePath);
    }

    private static async Task<HashSet<string>> GetUploadedDocumentFilenamesAsync(HttpClient client)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var response = await client.GetAsync("/api/documents");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.TryGetProperty("fileName", out var fileNameProp) && fileNameProp.ValueKind == JsonValueKind.String)
                        {
                            var fn = fileNameProp.GetString();
                            if (!string.IsNullOrEmpty(fn))
                                result.Add(fn);
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"Warning: Failed to fetch documents (HTTP {response.StatusCode})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching documents: {ex.Message}");
        }
        return result;
    }

    private static async Task<bool> UploadPdfAsync(HttpClient client, string pdfPath)
    {
        var filename = Path.GetFileName(pdfPath);
        Console.WriteLine($"Uploading and processing {filename}...");
        try
        {
            using var form = new MultipartFormDataContent();
            var fileBytes = await File.ReadAllBytesAsync(pdfPath);
            var byteContent = new ByteArrayContent(fileBytes);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(byteContent, "file", filename);

            var response = await client.PostAsync("/api/documents/upload", form);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Successfully processed {filename}");
                return true;
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Failed to process {filename}: {response.StatusCode} - {err}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception uploading {filename}: {ex.Message}");
            return false;
        }
    }

    private static string? FindMatchingPdf(string mdFilename, string dataDir)
    {
        var baseName = Path.GetFileName(mdFilename);
        if (baseName.StartsWith("Spørgsmål_", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName.Substring("Spørgsmål_".Length);
        }
        var nameWithoutExt = Path.GetFileNameWithoutExtension(baseName);

        if (!Directory.Exists(dataDir))
        {
            Console.WriteLine($"Data directory '{dataDir}' does not exist!");
            return null;
        }

        foreach (var file in Directory.GetFiles(dataDir))
        {
            var fName = Path.GetFileNameWithoutExtension(file);
            var fExt = Path.GetExtension(file);
            if (string.Equals(fName, nameWithoutExt, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fExt, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }
        return null;
    }

    private static async Task<string?> QueryApiAsync(HttpClient client, string question)
    {
        try
        {
            var payload = new { Question = question };
            var response = await client.PostAsJsonAsync("/api/query", payload);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(content);
                if (jsonDoc.RootElement.TryGetProperty("answer", out var answerProp) && answerProp.ValueKind == JsonValueKind.String)
                {
                    return answerProp.GetString();
                }
                return content;
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Query failed: {response.StatusCode} - {err}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Query exception: {ex.Message}");
            return null;
        }
    }

    public static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        // Pre-process list items
        html = Regex.Replace(html, @"<li>\s*<strong>(.*?)</strong>", "  * $1", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<li>\s*<b>(.*?)</b>", "  * $1", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<li>", "  * ", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</li>", "\n", RegexOptions.IgnoreCase);

        // Paragraphs and line breaks
        html = Regex.Replace(html, @"<p>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</p>", "\n\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

        // Bold / Strong
        html = Regex.Replace(html, @"<strong>(.*?)</strong>", "$1", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<b>(.*?)</b>", "$1", RegexOptions.IgnoreCase);

        // Strip any other tags
        html = Regex.Replace(html, @"<[^>]+>", "", RegexOptions.IgnoreCase);

        // Clean up multiple newlines
        html = Regex.Replace(html, @"\n{3,}", "\n\n");

        return html.Trim();
    }
}

public class QuestionItem
{
    [JsonPropertyName("id")]
    public JsonElement Id { get; set; }

    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("answer")]
    public string? Answer { get; set; }

    [JsonPropertyName("difficulty")]
    public string? Difficulty { get; set; }

    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    [JsonPropertyName("source_section")]
    public string? SourceSection { get; set; }
}

public class DocumentEvaluationResult
{
    public string DocumentName { get; set; } = string.Empty;
    public string PdfName { get; set; } = string.Empty;
    public List<QaPairResult> QaPairs { get; set; } = new();
}

public class QaPairResult
{
    public string Id { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string ExpectedAnswer { get; set; } = string.Empty;
    public string ApiAnswer { get; set; } = string.Empty;
    public string Difficulty { get; set; } = "N/A";
    public string Topic { get; set; } = "N/A";
}

public class EvaluationReportDocument : IDocument
{
    private readonly List<DocumentEvaluationResult> _results;
    private readonly DateTime _reportDate;

    public EvaluationReportDocument(List<DocumentEvaluationResult> results, DateTime reportDate)
    {
        _results = results;
        _reportDate = reportDate;
    }

    public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    public void Compose(IDocumentContainer container)
    {
        // 1. Cover Page
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(0);
            page.PageColor(Color.FromHex("#1A365D")); // Deep Blue

            page.Content().Padding(40).Column(column =>
            {
                column.Spacing(15);
                column.Item().PaddingTop(120);

                column.Item().AlignCenter().Text("Document RAG System")
                    .FontSize(26).Bold().FontColor(Colors.White);

                column.Item().AlignCenter().Text("Evaluation & Quality Report")
                    .FontSize(18).Bold().FontColor(Colors.White);

                column.Item().PaddingTop(25);
                column.Item().AlignCenter().Text("A comparative analysis of expected document answers\nagainst local Web API RAG responses.")
                    .FontSize(12).FontColor(Color.FromHex("#DCDCDC")).AlignCenter();

                column.Item().PaddingTop(160);
                column.Item().AlignCenter().Text($"Date: {_reportDate:MMMM yyyy}")
                    .FontSize(10).Italic().FontColor(Color.FromHex("#B4B4B4"));

                column.Item().AlignCenter().Text($"Total Documents: {_results.Count}")
                    .FontSize(10).Italic().FontColor(Color.FromHex("#B4B4B4"));

                var totalQuestions = _results.Sum(d => d.QaPairs.Count);
                column.Item().AlignCenter().Text($"Total Questions Evaluated: {totalQuestions}")
                    .FontSize(10).Italic().FontColor(Color.FromHex("#B4B4B4"));
            });
        });

        // 2. Content Pages for each document
        foreach (var doc in _results)
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(Color.FromHex("#282828")));

                // Header
                page.Header().Column(headerCol =>
                {
                    headerCol.Item().Row(row =>
                    {
                        row.RelativeItem().AlignLeft().Text("Document RAG System - Evaluation Report")
                            .FontSize(8).Bold().FontColor(Color.FromHex("#646E78"));

                        row.RelativeItem().AlignRight().Text(text =>
                        {
                            text.DefaultTextStyle(x => x.FontSize(8).Italic().FontColor(Color.FromHex("#646E78")));
                            text.Span("Page ");
                            text.CurrentPageNumber();
                            text.Span(" of ");
                            text.TotalPages();
                        });
                    });

                    headerCol.Item().PaddingTop(4).LineHorizontal(1).LineColor(Color.FromHex("#C8C8C8"));
                    headerCol.Item().PaddingBottom(10);
                });

                // Footer
                page.Footer().AlignCenter().Text("Confidential - For Internal Evaluation Only")
                    .FontSize(8).Italic().FontColor(Color.FromHex("#808080"));

                // Content
                page.Content().Column(col =>
                {
                    col.Spacing(14);

                    // Document Heading
                    col.Item().Text($"Document: {doc.DocumentName}")
                        .FontSize(14).Bold().FontColor(Color.FromHex("#1A365D"));

                    foreach (var qa in doc.QaPairs)
                    {
                        col.Item().Column(qaCol =>
                        {
                            qaCol.Spacing(4);

                            // Question Header
                            qaCol.Item().Text($"Q{qa.Id} - {qa.Topic} ({qa.Difficulty})")
                                .FontSize(10).Bold().FontColor(Color.FromHex("#646E78"));

                            // Question Text
                            qaCol.Item().Text($"Q: {qa.Question}")
                                .FontSize(11).Bold().FontColor(Color.FromHex("#1E1E1E"));

                            // Expected Answer
                            qaCol.Item().PaddingTop(2).Text("Expected Answer (Document):")
                                .FontSize(10).Bold().FontColor(Color.FromHex("#646E78"));

                            qaCol.Item().Background(Color.FromHex("#F5F7FA"))
                                .Padding(8)
                                .Text(qa.ExpectedAnswer)
                                .FontSize(10).FontColor(Color.FromHex("#282828"));

                            // API Answer
                            qaCol.Item().PaddingTop(2).Text("Web API RAG Response:")
                                .FontSize(10).Bold().FontColor(Color.FromHex("#646E78"));

                            qaCol.Item().Background(Color.FromHex("#F5F7FA"))
                                .Padding(8)
                                .Text(qa.ApiAnswer)
                                .FontSize(10).FontColor(Color.FromHex("#282828"));

                            // Separator
                            qaCol.Item().PaddingTop(8).LineHorizontal(1).LineColor(Color.FromHex("#E6E6E6"));
                        });
                    }
                });
            });
        }
    }
}


