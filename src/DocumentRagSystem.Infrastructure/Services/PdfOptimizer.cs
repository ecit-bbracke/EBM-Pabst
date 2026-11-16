using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentRagSystem.Core.Interfaces;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace DocumentRagSystem.Infrastructure.Services;

public class PdfOptimizer : IPdfOptimizer
{
    private readonly ILogger<PdfOptimizer> _logger;

    public PdfOptimizer(ILogger<PdfOptimizer> logger)
    {
        _logger = logger;
    }

    public byte[] SliceAndOptimize(byte[] sourcePdfBytes, IEnumerable<int>? targetPageNumbers = null)
    {
        if (sourcePdfBytes == null || sourcePdfBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        try
        {
            using var doc = PdfDocument.Open(sourcePdfBytes);
            var totalPages = doc.NumberOfPages;
            if (totalPages <= 1 && (targetPageNumbers == null || !targetPageNumbers.Any()))
            {
                return sourcePdfBytes;
            }

            var requestedPages = targetPageNumbers != null
                ? targetPageNumbers.Where(p => p >= 1 && p <= totalPages).Distinct().OrderBy(p => p).ToList()
                : new List<int>();

            // If no specific valid pages requested, include all pages
            if (requestedPages.Count == 0)
            {
                requestedPages = Enumerable.Range(1, totalPages).ToList();
            }

            var builder = new PdfDocumentBuilder
            {
                ArchiveStandard = PdfAStandard.None,
                IncludeDocumentInformation = false
            };

            foreach (var pageNum in requestedPages)
            {
                builder.AddPage(doc, pageNum);
            }

            var result = builder.Build();
            if (result != null && result.Length > 0)
            {
                _logger.LogInformation("[PdfOptimizer] Sliced/Optimized PDF from {OriginalSize} bytes to {ReducedSize} bytes (Pages: {PagesCount}/{TotalPages})",
                    sourcePdfBytes.Length, result.Length, requestedPages.Count, totalPages);
                return result;
            }

            return sourcePdfBytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PdfOptimizer] Failed to slice/optimize PDF. Returning original bytes.");
            return sourcePdfBytes;
        }
    }
}
