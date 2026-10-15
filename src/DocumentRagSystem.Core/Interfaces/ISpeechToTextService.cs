using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

public interface ISpeechToTextService
{
    Task<string> TranscribeAudioAsync(Stream audioStream, string mimeType, string? languageHint = null, CancellationToken cancellationToken = default);
}
