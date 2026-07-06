using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using UglyToad.PdfPig;

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
                var pageText = page.Text;
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
}
