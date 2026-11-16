using System.Collections.Generic;

namespace DocumentRagSystem.Core.Interfaces;

public interface IPdfOptimizer
{
    /// <summary>
    /// Slices a PDF to only include the specified 1-based page numbers and compresses the result.
    /// If targetPageNumbers is empty or contains all pages, returns the optimized/compacted document.
    /// </summary>
    byte[] SliceAndOptimize(byte[] sourcePdfBytes, IEnumerable<int>? targetPageNumbers = null);
}
