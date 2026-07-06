using System;
using System.Collections.Generic;
using System.Text;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public class ChunkingService : IChunkingService
{
    private readonly int _maxChunkSize;
    private readonly int _chunkOverlap;

    public ChunkingService(int maxChunkSize = 1000, int chunkOverlap = 200)
    {
        _maxChunkSize = maxChunkSize;
        _chunkOverlap = chunkOverlap;
    }

    public IEnumerable<DocumentChunk> ChunkText(string text, string documentId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        // If the text contains page break character (\f), split by page breaks
        if (text.Contains('\f'))
        {
            var pages = text.Split('\f', StringSplitOptions.RemoveEmptyEntries);
            var chunks = new List<string>();

            foreach (var page in pages)
            {
                var normalizedPage = NormalizeText(page);
                if (!string.IsNullOrWhiteSpace(normalizedPage))
                {
                    chunks.Add(normalizedPage);
                }
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                yield return new DocumentChunk(
                    Id: $"{documentId}_chunk_{i}",
                    DocumentId: documentId,
                    Text: chunks[i],
                    Index: i
                );
            }
        }
        else
        {
            // Fall back to original fixed character / sentence split logic if no page breaks exist
            text = NormalizeText(text);

            var chunks = new List<string>();
            int index = 0;
            int length = text.Length;

            while (index < length)
            {
                int size = Math.Min(_maxChunkSize, length - index);
                string chunkText = text.Substring(index, size);

                if (index + size < length)
                {
                    int lastSpace = chunkText.LastIndexOf(' ');
                    int lastPeriod = chunkText.LastIndexOf('.');
                    int lastNewline = chunkText.LastIndexOf('\n');

                    int splitIndex = -1;
                    if (lastPeriod > _maxChunkSize / 2) splitIndex = lastPeriod + 1;
                    else if (lastNewline > _maxChunkSize / 2) splitIndex = lastNewline + 1;
                    else if (lastSpace > _maxChunkSize / 2) splitIndex = lastSpace + 1;

                    if (splitIndex > 0)
                    {
                        chunkText = chunkText.Substring(0, splitIndex);
                        size = splitIndex;
                    }
                }

                chunks.Add(chunkText.Trim());

                index += size - _chunkOverlap;
                if (index < 0 || size <= _chunkOverlap)
                {
                    index += _chunkOverlap;
                }
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                yield return new DocumentChunk(
                    Id: $"{documentId}_chunk_{i}",
                    DocumentId: documentId,
                    Text: chunks[i],
                    Index: i
                );
            }
        }
    }

    private string NormalizeText(string text)
    {
        // Remove duplicate newlines and spaces to normalize layout
        var sb = new StringBuilder();
        string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (!string.IsNullOrEmpty(trimmedLine))
            {
                sb.Append(trimmedLine).Append(" ");
            }
        }

        return sb.ToString().Trim();
    }
}
