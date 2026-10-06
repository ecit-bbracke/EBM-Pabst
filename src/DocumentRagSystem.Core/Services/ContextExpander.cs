using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Core.Services;

public class ContextExpander : IContextExpander
{
    private readonly ILogger<ContextExpander>? _logger;

    public ContextExpander(ILogger<ContextExpander>? logger = null)
    {
        _logger = logger;
    }

    public async Task<List<DocumentChunk>> ExpandContextAsync(
        IEnumerable<DocumentChunk> initialChunks,
        IVectorStore vectorStore,
        int windowSize = 1,
        CancellationToken cancellationToken = default)
    {
        if (initialChunks == null) return new List<DocumentChunk>();
        var chunkList = initialChunks.ToList();
        if (chunkList.Count == 0 || windowSize <= 0) return chunkList;

        var result = new Dictionary<string, DocumentChunk>();
        foreach (var c in chunkList)
        {
            result[c.Id] = c;
        }

        // Group chunks by DocumentId
        var byDoc = chunkList
            .Where(c => !string.IsNullOrWhiteSpace(c.DocumentId))
            .GroupBy(c => c.DocumentId);

        foreach (var docGroup in byDoc)
        {
            var docId = docGroup.Key;
            var existingIndices = docGroup.Select(c => c.Index).ToHashSet();
            var neededIndices = new HashSet<int>();

            foreach (var idx in existingIndices)
            {
                for (int w = 1; w <= windowSize; w++)
                {
                    if (idx - w >= 0 && !existingIndices.Contains(idx - w))
                    {
                        neededIndices.Add(idx - w);
                    }
                    if (!existingIndices.Contains(idx + w))
                    {
                        neededIndices.Add(idx + w);
                    }
                }
            }

            if (neededIndices.Count > 0)
            {
                try
                {
                    var neighborChunks = await vectorStore.GetChunksByDocumentAndIndicesAsync(docId, neededIndices);
                    if (neighborChunks != null)
                    {
                        int addedCount = 0;
                        foreach (var nc in neighborChunks)
                        {
                            if (!result.ContainsKey(nc.Id))
                            {
                                result[nc.Id] = nc;
                                addedCount++;
                            }
                        }

                        if (addedCount > 0)
                        {
                            _logger?.LogInformation(
                                "[ContextExpander] Expanded {AddedCount} neighbor chunks for document {DocId} (Indices: {Indices}).",
                                addedCount, docId, string.Join(", ", neededIndices));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[ContextExpander] Failed to expand neighbor chunks for document {DocId}.", docId);
                }
            }
        }

        return result.Values
            .OrderBy(c => c.DocumentId)
            .ThenBy(c => c.Index)
            .ToList();
    }
}
