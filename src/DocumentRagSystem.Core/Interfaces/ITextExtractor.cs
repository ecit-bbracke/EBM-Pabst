using System.IO;
using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

public interface ITextExtractor
{
    Task<string> ExtractTextAsync(Stream pdfStream);
}
