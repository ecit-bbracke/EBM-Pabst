using System;
using System.Collections.Generic;
using System.Linq;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Services;

public static class ReciprocalRankFusion
{
    /// <summary>
    /// Combines multiple ranked lists using Reciprocal Rank Fusion (RRF).
    /// Score = Sum(1.0 / (k + rank))
    /// </summary>
    public static List<DocumentChunk> Fuse(
        IEnumerable<DocumentChunk> vectorResults,
        IEnumerable<DocumentChunk> bm25Results,
        double k = 60.0,
        int maxResults = 15)
    {
        var scores = new Dictionary<string, (DocumentChunk Chunk, double Score)>(StringComparer.OrdinalIgnoreCase);

        // 1. Vector Search scoring
        int rank = 1;
        foreach (var chunk in vectorResults ?? Enumerable.Empty<DocumentChunk>())
        {
            if (string.IsNullOrWhiteSpace(chunk.Id)) continue;
            var rrfScore = 1.0 / (k + rank);
            scores[chunk.Id] = (chunk, rrfScore);
            rank++;
        }

        // 2. BM25 Search scoring
        rank = 1;
        foreach (var chunk in bm25Results ?? Enumerable.Empty<DocumentChunk>())
        {
            if (string.IsNullOrWhiteSpace(chunk.Id)) continue;
            var rrfScore = 1.0 / (k + rank);
            if (scores.TryGetValue(chunk.Id, out var existing))
            {
                scores[chunk.Id] = (existing.Chunk, existing.Score + rrfScore);
            }
            else
            {
                scores[chunk.Id] = (chunk, rrfScore);
            }
            rank++;
        }

        return scores.Values
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x => x.Chunk)
            .ToList();
    }
}
