using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocumentRagSystem.Infrastructure.TextExtractors;

public class PdfTextExtractor : ITextExtractor
{
    public Task<string> ExtractTextAsync(Stream pdfStream)
    {
        if (pdfStream == null)
            throw new ArgumentNullException(nameof(pdfStream));

        try
        {
            var textBuilder = new StringBuilder();

            // Open the PDF document from stream using PdfPig
            using var document = PdfDocument.Open(pdfStream);
            
            bool firstPage = true;
            foreach (var page in document.GetPages())
            {
                var pageText = ExtractPageAsMarkdownTable(page);
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    if (!firstPage)
                    {
                        textBuilder.Append('\f');
                    }
                    textBuilder.Append(pageText);
                    firstPage = false;
                }
            }

            return Task.FromResult(textBuilder.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PdfPig extraction failed: {ex.Message}. Running direct token-salvage fallback...");

            try
            {
                // Seek back to start of stream if seekable
                if (pdfStream.CanSeek)
                {
                    pdfStream.Position = 0;
                }

                using var reader = new StreamReader(pdfStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                var rawPdfText = reader.ReadToEnd();

                // Extract all printable contents of parenthesis '(' and ')' which represent text strings in PDF streams
                var sb = new StringBuilder();
                var matches = Regex.Matches(rawPdfText, @"\(([^)]+)\)");

                foreach (Match match in matches)
                {
                    var textLiteral = match.Groups[1].Value;
                    // Filter out short noise or commands
                    if (textLiteral.Length >= 2 && !textLiteral.StartsWith("/"))
                    {
                        sb.Append(textLiteral).Append(" ");
                    }
                }

                if (sb.Length > 0)
                {
                    return Task.FromResult(sb.ToString().Trim());
                }
            }
            catch (Exception fallbackEx)
            {
                Console.WriteLine($"Token salvage fallback also failed: {fallbackEx.Message}");
            }

            throw new InvalidOperationException($"Failed to extract text from PDF stream: {ex.Message}", ex);
        }
    }

    private string ExtractPageAsMarkdownTable(Page page, double rowTolerancePoints = 3.5)
    {
        var allWords = page.GetWords().ToList();
        if (!allWords.Any()) return string.Empty;

        // Separate headers and footers from main content based on page height proportions
        double pageHeight = page.Height;
        double headerThreshold = pageHeight - 75.0; // Top 75 points
        double footerThreshold = 55.0;            // Bottom 55 points

        var headerWords = allWords.Where(w => w.BoundingBox.Bottom > headerThreshold)
                                  .OrderByDescending(w => w.BoundingBox.Top)
                                  .ThenBy(w => w.BoundingBox.Left)
                                  .ToList();

        var footerWords = allWords.Where(w => w.BoundingBox.Top < footerThreshold)
                                  .OrderByDescending(w => w.BoundingBox.Top)
                                  .ThenBy(w => w.BoundingBox.Left)
                                  .ToList();

        var contentWords = allWords.Where(w => w.BoundingBox.Bottom <= headerThreshold && w.BoundingBox.Top >= footerThreshold)
                                   .ToList();

        // 1. Format Header and Footer lines simply
        var headerLines = GroupWordsIntoLines(headerWords, rowTolerancePoints);
        var footerLines = GroupWordsIntoLines(footerWords, rowTolerancePoints);

        var sb = new StringBuilder();

        // Append Header
        foreach (var line in headerLines)
        {
            sb.AppendLine(line);
        }
        if (headerLines.Any()) sb.AppendLine();

        // 2. Process Content Zone as structured table/content
        if (contentWords.Any())
        {
            // Group content words vertically into rows (Top-to-Bottom, descending Y)
            var rows = new List<List<Word>>();
            foreach (var word in contentWords.OrderByDescending(w => w.BoundingBox.Top))
            {
                bool placed = false;
                foreach (var row in rows)
                {
                    var rowAverageTop = row.Average(w => w.BoundingBox.Top);
                    var rowAverageBottom = row.Average(w => w.BoundingBox.Bottom);

                    // Group words if they are on a similar horizontal line (within tolerance)
                    if (Math.Abs(word.BoundingBox.Top - rowAverageTop) <= rowTolerancePoints ||
                        Math.Abs(word.BoundingBox.Bottom - rowAverageBottom) <= rowTolerancePoints)
                    {
                        row.Add(word);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    rows.Add(new List<Word> { word });
                }
            }

            // Sort rows descending vertically (Top-to-Bottom)
            rows = rows.OrderByDescending(r => r.Average(w => w.BoundingBox.Top)).ToList();

            // Identify horizontal clusters (Columns) globally in content zone
            var leftCoordinates = contentWords.Select(w => w.BoundingBox.Left).OrderBy(x => x).ToList();
            var columnStarts = new List<double>();
            double colMergeTolerance = 18.0; // Distance in points to merge column boundaries

            foreach (var left in leftCoordinates)
            {
                if (!columnStarts.Any() || Math.Abs(left - columnStarts.Last()) > colMergeTolerance)
                {
                    columnStarts.Add(left);
                }
            }
            columnStarts = columnStarts.OrderBy(x => x).ToList();
            int columnCount = columnStarts.Count;

            // Map cell elements into columns for each row
            var markdownRows = new List<string[]>();
            foreach (var row in rows)
            {
                var rowCells = Enumerable.Repeat(string.Empty, columnCount).ToArray();
                
                // Group adjacent words horizontally inside the same cell
                var sortedRowWords = row.OrderBy(w => w.BoundingBox.Left).ToList();
                var mergedCells = new List<(double Left, string Text)>();
                var currentCellWords = new List<Word>();

                foreach (var word in sortedRowWords)
                {
                    if (!currentCellWords.Any())
                    {
                        currentCellWords.Add(word);
                        continue;
                    }

                    double spaceThreshold = word.BoundingBox.Height * 1.5;
                    double gap = word.BoundingBox.Left - currentCellWords.Last().BoundingBox.Right;

                    if (gap <= spaceThreshold)
                    {
                        currentCellWords.Add(word);
                    }
                    else
                    {
                        mergedCells.Add((currentCellWords.First().BoundingBox.Left, string.Join(" ", currentCellWords.Select(w => w.Text))));
                        currentCellWords.Clear();
                        currentCellWords.Add(word);
                    }
                }

                if (currentCellWords.Any())
                {
                    mergedCells.Add((currentCellWords.First().BoundingBox.Left, string.Join(" ", currentCellWords.Select(w => w.Text))));
                }

                // Assign each cell to its nearest global column index
                foreach (var cell in mergedCells)
                {
                    int colIndex = columnStarts.BinarySearch(cell.Left);
                    if (colIndex < 0)
                    {
                        colIndex = ~colIndex;
                        if (colIndex >= columnCount) colIndex = columnCount - 1;
                        if (colIndex > 0 && Math.Abs(cell.Left - columnStarts[colIndex - 1]) < Math.Abs(cell.Left - columnStarts[colIndex]))
                        {
                            colIndex--;
                        }
                    }
                    
                    if (string.IsNullOrEmpty(rowCells[colIndex]))
                        rowCells[colIndex] = cell.Text;
                    else
                        rowCells[colIndex] += " " + cell.Text;
                }

                markdownRows.Add(rowCells);
            }

            // 4. Segment lines dynamically to split actual tables from titles and single paragraphs
            var rowSegments = markdownRows.Select(r => new {
                Cells = r,
                NonEmptyCount = r.Count(c => !string.IsNullOrWhiteSpace(c))
            }).ToList();

            int rowIndex = 0;
            while (rowIndex < rowSegments.Count)
            {
                // Find contiguous non-table rows (rows with 1 or 2 filled cells - likely titles/notes)
                var nonTableBlock = new List<string[]>();
                while (rowIndex < rowSegments.Count && rowSegments[rowIndex].NonEmptyCount <= 2)
                {
                    nonTableBlock.Add(rowSegments[rowIndex].Cells);
                    rowIndex++;
                }

                foreach (var rowCells in nonTableBlock)
                {
                    var lineText = string.Join(" ", rowCells.Where(c => !string.IsNullOrEmpty(c))).Trim();
                    if (!string.IsNullOrEmpty(lineText))
                    {
                        sb.AppendLine(lineText);
                    }
                }

                // Find contiguous table rows (rows with 3 or more filled cells - likely active tabular content)
                var tableBlock = new List<string[]>();
                while (rowIndex < rowSegments.Count && rowSegments[rowIndex].NonEmptyCount > 2)
                {
                    tableBlock.Add(rowSegments[rowIndex].Cells);
                    rowIndex++;
                }

                if (tableBlock.Any())
                {
                    // For just this specific table block, prune columns that are completely empty *within this block*
                    var blockEmptyCols = new List<int>();
                    for (int col = 0; col < columnCount; col++)
                    {
                        bool hasData = tableBlock.Any(r => !string.IsNullOrWhiteSpace(r[col]));
                        if (!hasData)
                        {
                            blockEmptyCols.Add(col);
                        }
                    }

                    var prunedBlockRows = new List<string[]>();
                    foreach (var r in tableBlock)
                    {
                        var pruned = r.Where((val, idx) => !blockEmptyCols.Contains(idx)).ToArray();
                        prunedBlockRows.Add(pruned);
                    }

                    int blockColCount = columnCount - blockEmptyCols.Count;

                    if (blockColCount <= 2)
                    {
                        // Fall back to plain lines if block is too narrow
                        foreach (var rowCells in prunedBlockRows)
                        {
                            var lineText = string.Join(" ", rowCells.Where(c => !string.IsNullOrEmpty(c))).Trim();
                            if (!string.IsNullOrEmpty(lineText))
                            {
                                sb.AppendLine(lineText);
                            }
                        }
                    }
                    else
                    {
                        sb.AppendLine();
                        // Build structured markdown table for this contiguous block
                        sb.Append("| ").Append(string.Join(" | ", prunedBlockRows[0])).AppendLine(" |");
                        
                        var separators = Enumerable.Repeat("---", blockColCount);
                        sb.Append("| ").Append(string.Join(" | ", separators)).AppendLine(" |");
                        
                        for (int i = 1; i < prunedBlockRows.Count; i++)
                        {
                            sb.Append("| ").Append(string.Join(" | ", prunedBlockRows[i])).AppendLine(" |");
                        }
                        sb.AppendLine();
                    }
                }
            }
        }

        // Append Footer
        if (footerLines.Any()) sb.AppendLine();
        foreach (var line in footerLines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }

    private List<string> GroupWordsIntoLines(List<Word> words, double rowTolerancePoints)
    {
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Top))
        {
            bool placed = false;
            foreach (var line in lines)
            {
                var lineAverageTop = line.Average(w => w.BoundingBox.Top);
                var lineAverageBottom = line.Average(w => w.BoundingBox.Bottom);

                if (Math.Abs(word.BoundingBox.Top - lineAverageTop) <= rowTolerancePoints ||
                    Math.Abs(word.BoundingBox.Bottom - lineAverageBottom) <= rowTolerancePoints)
                {
                    line.Add(word);
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                lines.Add(new List<Word> { word });
            }
        }

        return lines.OrderByDescending(l => l.Average(w => w.BoundingBox.Top))
                    .Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
                    .ToList();
    }
}
