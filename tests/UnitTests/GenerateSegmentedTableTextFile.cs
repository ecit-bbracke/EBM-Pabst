using System;
using System.IO;
using System.Threading.Tasks;
using DocumentRagSystem.Infrastructure.TextExtractors;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class GenerateSegmentedTableTextFile
{
    [Fact]
    public async Task ExportFullExtraction()
    {
        var pdfPath = @"c:\Users\bbracke\Desktop\EBM\DotNet\data\Generelt\Data_sheet_DA_-_K3G560PC0401_KM260717_ (1).pdf";
        if (!File.Exists(pdfPath))
        {
            throw new Exception("File not found at: " + pdfPath);
        }

        var extractor = new PdfTextExtractor();
        using var stream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read);
        var text = await extractor.ExtractTextAsync(stream);

        // Save it directly to the workspace root
        var outputPath = @"c:\Users\bbracke\Desktop\EBM\DotNet\extracted_table_segmented_text.txt";
        await File.WriteAllTextAsync(outputPath, text);
    }
}
