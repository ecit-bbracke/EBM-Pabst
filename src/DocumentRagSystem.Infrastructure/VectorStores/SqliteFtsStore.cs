using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.Infrastructure.VectorStores;

public class SqliteFtsStore : IFullTextSearchStore
{
    private static readonly ConcurrentDictionary<string, bool> _initializedDatabases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _initLock = new();

    private readonly IDbContextFactory<DocumentDbContext> _contextFactory;
    private readonly ILogger<SqliteFtsStore>? _logger;

    public SqliteFtsStore(
        IDbContextFactory<DocumentDbContext> contextFactory,
        ILogger<SqliteFtsStore>? logger = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger;
        EnsureFtsInitialized();
    }

    private void EnsureFtsInitialized()
    {
        string connStr;
        try
        {
            using var probe = _contextFactory.CreateDbContext();
            connStr = probe.Database.GetConnectionString() ?? "default";
        }
        catch
        {
            connStr = "default";
        }

        if (_initializedDatabases.ContainsKey(connStr)) return;

        lock (_initLock)
        {
            if (_initializedDatabases.ContainsKey(connStr)) return;

            try
            {
                using var context = _contextFactory.CreateDbContext();
                var conn = context.Database.GetDbConnection();
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE VIRTUAL TABLE IF NOT EXISTS ChunkFts USING fts5(
                        ChunkId UNINDEXED, 
                        DocumentId UNINDEXED, 
                        IndexNumber UNINDEXED,
                        FileName, 
                        FilePath UNINDEXED,
                        Text, 
                        ArticleId, 
                        SourceDocumentId,
                        tokenize='unicode61 remove_diacritics 2'
                    );";
                cmd.ExecuteNonQuery();

                _initializedDatabases[connStr] = true;
                _logger?.LogInformation("[SqliteFtsStore] SQLite FTS5 ChunkFts virtual table initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[SqliteFtsStore] Failed to initialize SQLite FTS5 table.");
            }
        }
    }

    public async Task AddChunksAsync(IEnumerable<DocumentChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks == null) return;
        var list = new List<DocumentChunk>(chunks);
        if (list.Count == 0) return;

        EnsureFtsInitialized();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Explicitly delete any prior chunks for these documents/chunks to prevent FTS5 duplication
        var docIds = list.Select(c => c.DocumentId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (docIds.Count > 0)
        {
            using var delDocCmd = connection.CreateCommand();
            delDocCmd.Transaction = transaction as System.Data.Common.DbTransaction;
            delDocCmd.CommandText = "DELETE FROM ChunkFts WHERE DocumentId = $delDocId;";
            var delDocParam = delDocCmd.CreateParameter();
            delDocParam.ParameterName = "$delDocId";
            delDocCmd.Parameters.Add(delDocParam);

            foreach (var docId in docIds)
            {
                delDocParam.Value = docId;
                await delDocCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            using var delCmd = connection.CreateCommand();
            delCmd.Transaction = transaction as System.Data.Common.DbTransaction;
            delCmd.CommandText = "DELETE FROM ChunkFts WHERE ChunkId = $delChunkId;";
            var delParam = delCmd.CreateParameter();
            delParam.ParameterName = "$delChunkId";
            delCmd.Parameters.Add(delParam);

            foreach (var chunk in list)
            {
                delParam.Value = chunk.Id;
                await delCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction as System.Data.Common.DbTransaction;
        command.CommandText = @"
            INSERT INTO ChunkFts(
                ChunkId, DocumentId, IndexNumber, FileName, FilePath, Text, ArticleId, SourceDocumentId
            ) VALUES (
                $chunkId, $docId, $indexNum, $fileName, $filePath, $text, $articleId, $sourceDocId
            );";

        var chunkIdParam = command.CreateParameter();
        chunkIdParam.ParameterName = "$chunkId";
        command.Parameters.Add(chunkIdParam);

        var docIdParam = command.CreateParameter();
        docIdParam.ParameterName = "$docId";
        command.Parameters.Add(docIdParam);

        var indexNumParam = command.CreateParameter();
        indexNumParam.ParameterName = "$indexNum";
        command.Parameters.Add(indexNumParam);

        var fileNameParam = command.CreateParameter();
        fileNameParam.ParameterName = "$fileName";
        command.Parameters.Add(fileNameParam);

        var filePathParam = command.CreateParameter();
        filePathParam.ParameterName = "$filePath";
        command.Parameters.Add(filePathParam);

        var textParam = command.CreateParameter();
        textParam.ParameterName = "$text";
        command.Parameters.Add(textParam);

        var articleIdParam = command.CreateParameter();
        articleIdParam.ParameterName = "$articleId";
        command.Parameters.Add(articleIdParam);

        var sourceDocIdParam = command.CreateParameter();
        sourceDocIdParam.ParameterName = "$sourceDocId";
        command.Parameters.Add(sourceDocIdParam);

        foreach (var chunk in list)
        {
            chunkIdParam.Value = chunk.Id;
            docIdParam.Value = chunk.DocumentId;
            indexNumParam.Value = chunk.Index;
            fileNameParam.Value = (object?)chunk.FileName ?? DBNull.Value;
            filePathParam.Value = (object?)chunk.FilePath ?? DBNull.Value;
            textParam.Value = chunk.Text;
            articleIdParam.Value = (object?)chunk.ArticleId ?? DBNull.Value;
            sourceDocIdParam.Value = (object?)chunk.SourceDocumentId ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _logger?.LogDebug("[SqliteFtsStore] Successfully indexed {Count} chunks into SQLite FTS5.", list.Count);
    }

    public async Task<IEnumerable<DocumentChunk>> SearchAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<DocumentChunk>();

        var ftsQuery = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery)) return Array.Empty<DocumentChunk>();

        EnsureFtsInitialized();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT ChunkId, DocumentId, IndexNumber, FileName, FilePath, Text, ArticleId, SourceDocumentId, bm25(ChunkFts) as Score
            FROM ChunkFts
            WHERE ChunkFts MATCH $ftsQuery
            ORDER BY Score ASC
            LIMIT $limit;";

        var queryParam = command.CreateParameter();
        queryParam.ParameterName = "$ftsQuery";
        queryParam.Value = ftsQuery;
        command.Parameters.Add(queryParam);

        var limitParam = command.CreateParameter();
        limitParam.ParameterName = "$limit";
        limitParam.Value = Math.Clamp(limit, 1, 50);
        command.Parameters.Add(limitParam);

        var results = new List<DocumentChunk>();

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var chunkId = reader.GetString(0);
                var docId = reader.GetString(1);
                var indexNum = reader.GetInt32(2);
                var fileName = reader.IsDBNull(3) ? null : reader.GetString(3);
                var filePath = reader.IsDBNull(4) ? null : reader.GetString(4);
                var text = reader.GetString(5);
                var articleId = reader.IsDBNull(6) ? null : reader.GetString(6);
                var sourceDocId = reader.IsDBNull(7) ? null : reader.GetString(7);

                results.Add(new DocumentChunk(
                    Id: chunkId,
                    DocumentId: docId,
                    Text: text,
                    Index: indexNum,
                    FileName: fileName,
                    FilePath: filePath,
                    UploadedAt: DateTime.UtcNow,
                    OriginalFileName: fileName,
                    ArticleId: articleId,
                    SourceDocumentId: sourceDocId
                ));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SqliteFtsStore] BM25 FTS5 query failed for: {Query}", ftsQuery);
        }

        return results;
    }

    public async Task DeleteChunksByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return;

        EnsureFtsInitialized();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ChunkFts WHERE DocumentId = $docId;";

        var docIdParam = command.CreateParameter();
        docIdParam.ParameterName = "$docId";
        docIdParam.Value = documentId;
        command.Parameters.Add(docIdParam);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetIndexedChunkCountAsync(CancellationToken cancellationToken = default)
    {
        EnsureFtsInitialized();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM ChunkFts;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<HashSet<string>> GetIndexedDocumentIdsAsync(CancellationToken cancellationToken = default)
    {
        EnsureFtsInitialized();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT DocumentId FROM ChunkFts;";

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                set.Add(reader.GetString(0));
            }
        }
        return set;
    }

    private static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var tokens = query.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        var cleanTerms = new List<string>();
        foreach (var t in tokens)
        {
            var clean = Regex.Replace(t, @"[^\w\.\-\/]", "").Trim();
            if (clean.Length > 0 && !clean.Equals("AND", StringComparison.OrdinalIgnoreCase) && !clean.Equals("OR", StringComparison.OrdinalIgnoreCase) && !clean.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                cleanTerms.Add($"\"{clean.Replace("\"", "\"\"")}\"");
            }
        }
        return cleanTerms.Count > 0 ? string.Join(" OR ", cleanTerms) : string.Empty;
    }
}
