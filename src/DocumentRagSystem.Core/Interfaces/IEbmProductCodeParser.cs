using System.Diagnostics.CodeAnalysis;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IEbmProductCodeParser
{
    /// <summary>
    /// Attempts to parse an ebm-papst product string into structured domain information.
    /// </summary>
    bool TryParse(string? rawInput, [NotNullWhen(true)] out EbmProductInfo? productInfo);

    /// <summary>
    /// Parses an ebm-papst product string. Returns product info with details or unknown fallback.
    /// </summary>
    EbmProductInfo Parse(string rawInput);

    /// <summary>
    /// Extracts all potential ebm-papst product numbers from a text block or question.
    /// </summary>
    List<EbmProductInfo> ExtractProductsFromText(string text);

    /// <summary>
    /// Evaluates technical comparison and replacement viability between two ebm-papst products based on domain rules.
    /// </summary>
    EbmComparisonEvaluation Compare(EbmProductInfo productA, EbmProductInfo productB);
}
