using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

public record LanguageDetectionResult(
    string Language,
    bool IsEnglish,
    double Confidence,
    string? Explanation = null
);

public interface ILanguageDetector
{
    Task<LanguageDetectionResult> DetectLanguageAsync(string text, string? fileName = null);
}
