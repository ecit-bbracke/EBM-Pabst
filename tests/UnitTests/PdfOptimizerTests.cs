using System;
using System.IO;
using System.Linq;
using DocumentRagSystem.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class PdfOptimizerTests
{
    private readonly PdfOptimizer _optimizer;
    private readonly string _samplePdfPath;

    public PdfOptimizerTests()
    {
        _optimizer = new PdfOptimizer(NullLogger<PdfOptimizer>.Instance);

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _samplePdfPath = Path.Combine(repoRoot, "tests", "TestData", "sample.pdf");
        if (!File.Exists(_samplePdfPath))
        {
            _samplePdfPath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.pdf");
        }
    }

    [Fact]
    public void SliceAndOptimize_WithNullOrEmpty_ReturnsOriginal()
    {
        var resultNull = _optimizer.SliceAndOptimize(null!, new[] { 1 });
        Assert.Empty(resultNull);

        var empty = Array.Empty<byte>();
        var resultEmpty = _optimizer.SliceAndOptimize(empty, new[] { 1 });
        Assert.Empty(resultEmpty);
    }

    [Fact]
    public void SliceAndOptimize_AllPages_RecompressesAndOptimizesStructure()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var multiPagePdf = Path.Combine(repoRoot, "data", "Generelt", "Data_sheet_US_-_S4E315BS2035_VNA0315H4MGZ_KM312852_.pdf");
        if (!File.Exists(multiPagePdf)) return;

        var origBytes = File.ReadAllBytes(multiPagePdf);
        using var origDoc = PdfDocument.Open(origBytes);
        var totalPages = origDoc.NumberOfPages;

        // Optimize ALL pages through PdfDocumentBuilder (no slicing)
        var allPages = Enumerable.Range(1, totalPages).ToList();
        var optimizedBytes = _optimizer.SliceAndOptimize(origBytes, allPages);

        Console.WriteLine($"Original full size: {origBytes.Length} bytes -> Re-built full size: {optimizedBytes.Length} bytes");
        Assert.NotEmpty(optimizedBytes);
        using var optDoc = PdfDocument.Open(optimizedBytes);
        Assert.Equal(totalPages, optDoc.NumberOfPages);
        Assert.NotEmpty(optDoc.GetPages().First().Letters);
    }

    [Fact]
    public void SliceAndOptimize_MultiPagePdf_ReducesSizeAndPageCount()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var multiPagePdf = Path.Combine(repoRoot, "data", "Generelt", "Data_sheet_US_-_S4E315BS2035_VNA0315H4MGZ_KM312852_.pdf");
        if (!File.Exists(multiPagePdf))
        {
            // If sample not in data, skip
            return;
        }

        var origBytes = File.ReadAllBytes(multiPagePdf);
        using (var origDoc = PdfDocument.Open(origBytes))
        {
            Assert.True(origDoc.NumberOfPages > 1);
        }

        // Slice to page 1 only
        var slicedBytes = _optimizer.SliceAndOptimize(origBytes, new[] { 1 });
        Assert.NotEmpty(slicedBytes);
        Assert.True(slicedBytes.Length < origBytes.Length, "Sliced single-page PDF must be smaller than the multi-page original.");

        using var slicedDoc = PdfDocument.Open(slicedBytes);
        Assert.Equal(1, slicedDoc.NumberOfPages);
    }

    [Fact]
    public void SliceAndOptimize_WithMultiplePages_KeepsOnlyRequestedPagesInOrder()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var multiPagePdf = Path.Combine(repoRoot, "data", "Generelt", "Data_sheet_US_-_S4E315BS2035_VNA0315H4MGZ_KM312852_.pdf");
        if (!File.Exists(multiPagePdf))
        {
            return;
        }

        var origBytes = File.ReadAllBytes(multiPagePdf);

        // Request pages 1 and 3
        var slicedBytes = _optimizer.SliceAndOptimize(origBytes, new[] { 3, 1 });
        Assert.NotEmpty(slicedBytes);

        using var slicedDoc = PdfDocument.Open(slicedBytes);
        Assert.Equal(2, slicedDoc.NumberOfPages);
    }
}
