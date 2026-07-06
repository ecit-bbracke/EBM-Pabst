using System.IO;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IDocumentProcessor
{
    Task<Document> ProcessPdfAsync(Stream pdfStream, string fileName);
}
