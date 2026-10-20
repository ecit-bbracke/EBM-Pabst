using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.WebApi.DTOs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.WebApi.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app, IEnumerable<string> servedUploadDirectories)
    {
        // DIRECT DOCUMENT FILE SERVE FALLBACK
        app.MapGet("/uploads/{*filePath}", (string filePath) =>
        {
            if (string.IsNullOrWhiteSpace(filePath)) return Results.NotFound();

            var cleanPath = Uri.UnescapeDataString(filePath).TrimStart('/');
            if (cleanPath.Contains("..") || !cleanPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var fileName = Path.GetFileName(cleanPath);

            foreach (var dir in servedUploadDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                var fullDir = Path.GetFullPath(dir);

                var direct = Path.Combine(dir, fileName);
                if (File.Exists(direct))
                {
                    var fullDirect = Path.GetFullPath(direct);
                    if (fullDirect.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.File(fullDirect, "application/pdf", enableRangeProcessing: true);
                    }
                }

                var subpath = Path.Combine(dir, cleanPath);
                if (File.Exists(subpath))
                {
                    var fullSubpath = Path.GetFullPath(subpath);
                    if (fullSubpath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.File(fullSubpath, "application/pdf", enableRangeProcessing: true);
                    }
                }

                try
                {
                    var pattern = $"*_{fileName}";
                    var matches = Directory.GetFiles(dir, pattern);
                    if (matches.Length > 0)
                    {
                        return Results.File(matches[0], "application/pdf", enableRangeProcessing: true);
                    }
                }
                catch { }

                if (fileName.Length > 37 && fileName[36] == '_')
                {
                    var rawName = fileName.Substring(37);
                    var rawDirect = Path.Combine(dir, rawName);
                    if (File.Exists(rawDirect))
                    {
                        return Results.File(rawDirect, "application/pdf", enableRangeProcessing: true);
                    }
                }
            }

            return Results.NotFound();
        })
        .WithName("GetUploadedFile");

        // GET ALL DOCUMENTS ENDPOINT (Supports pagination and filtering)
        app.MapGet("/api/documents", async (
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] string? search,
            [FromQuery] string? category,
            [FromQuery] bool? onlyExisting,
            [FromQuery] bool? onlyMissing,
            IDocumentRepository repository,
            IVectorStore vectorStore) =>
        {
            var p = page ?? 1;
            var size = Math.Clamp(pageSize ?? 20, 1, 100);

            var (repoDocs, totalCount) = await repository.GetPagedDocumentsAsync(p, size, search, category);

            // Auto-reconcile fallback: if repository is completely empty, check if vector store has indexed documents
            if (totalCount == 0 && string.IsNullOrWhiteSpace(search) && string.IsNullOrWhiteSpace(category))
            {
                try
                {
                    var vectorDocs = await vectorStore.GetDocumentsAsync(100);
                    if (vectorDocs != null && vectorDocs.Any())
                    {
                        foreach (var vDoc in vectorDocs)
                        {
                            await repository.AddDocumentAsync(vDoc);
                        }
                        (repoDocs, totalCount) = await repository.GetPagedDocumentsAsync(p, size, search, category);
                    }
                }
                catch { }
            }

            var items = repoDocs.Select(doc => doc with { FilePath = DocumentUrlResolver.ToDocumentUrl(doc.FilePath, doc.FileName, servedUploadDirectories) }).ToList();

            if (onlyExisting == true)
            {
                items = items.Where(d => !string.IsNullOrWhiteSpace(d.FilePath)).ToList();
                totalCount = items.Count < repoDocs.Count() ? items.Count : totalCount;
            }
            else if (onlyMissing == true)
            {
                items = items.Where(d => string.IsNullOrWhiteSpace(d.FilePath)).ToList();
                totalCount = items.Count < repoDocs.Count() ? items.Count : totalCount;
            }

            var totalPages = (int)Math.Ceiling(totalCount / (double)size);
            if (totalPages == 0) totalPages = 1;

            var paged = new PagedResult<Document>(
                Items: items,
                TotalCount: totalCount,
                Page: p,
                PageSize: size,
                TotalPages: totalPages,
                HasPreviousPage: p > 1,
                HasNextPage: p < totalPages
            );

            return Results.Ok(paged);
        })
        .RequireAuthorization()
        .WithName("GetAllDocuments");

        // DELETE DOCUMENT BY ID ENDPOINT
        app.MapDelete("/api/documents/{id}", async (
            string id,
            IDocumentRepository repository,
            IVectorStore vectorStore,
            IFullTextSearchStore? ftsStore,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DocumentEndpoints");
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.BadRequest(new { error = "Dokument-ID skal angives." });
            }

            var doc = await repository.GetDocumentByIdAsync(id);
            if (doc == null)
            {
                try
                {
                    var vectorDocs = await vectorStore.GetDocumentsAsync();
                    doc = vectorDocs.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    // Vector store fallback
                }
            }

            if (doc == null)
            {
                return Results.NotFound(new { message = "Dokumentet blev ikke fundet." });
            }

            // 1. Delete physical file from disk
            DeletePhysicalDocumentFile(doc.FilePath, doc.FileName, servedUploadDirectories);
            Program.DirFileCache.Clear();

            // 2. Delete chunks from vector database
            try
            {
                await vectorStore.DeleteDocumentChunksAsync(id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fejl under sletning af chunks i vektordatabasen for dokument ID: {DocumentId}", id);
            }

            // 3. Delete chunks from SQLite FTS5 database
            if (ftsStore != null)
            {
                try
                {
                    await ftsStore.DeleteChunksByDocumentIdAsync(id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Fejl under sletning af chunks i FTS5 for dokument ID: {DocumentId}", id);
                }
            }

            // 4. Delete document record from SQLite repository
            try
            {
                await repository.DeleteDocumentAsync(id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fejl under sletning af dokument fra repository for dokument ID: {DocumentId}", id);
            }

            logger.LogInformation("Dokument slettet succesfuldt: {FileName} (ID: {DocumentId})", doc.FileName, id);
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithName("DeleteDocument");

        // DOCUMENT STATUS ENDPOINTS
        app.MapGet("/api/documents/status", async (
            [FromQuery] string? fileName,
            [FromQuery] string? name,
            [FromQuery] string? file,
            IDocumentRepository repository,
            IVectorStore vectorStore) =>
        {
            var resolvedFileName = fileName ?? name ?? file;
            if (string.IsNullOrWhiteSpace(resolvedFileName))
            {
                return Results.BadRequest(new { error = "The 'fileName' parameter is required." });
            }

            return await GetDocumentStatusResultAsync(resolvedFileName, repository, vectorStore, servedUploadDirectories);
        })
        .RequireAuthorization("DocumentUploadPolicy")
        .WithName("GetDocumentStatusByQuery")
        .WithSummary("Get document upload status by filename (query parameter)")
        .WithDescription("Checks whether a document has already been uploaded by filename and returns its current status.")
        .Produces<DocumentStatusResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        app.MapGet("/api/documents/status/{*fileName}", async (
            string fileName,
            IDocumentRepository repository,
            IVectorStore vectorStore) =>
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return Results.BadRequest(new { error = "The 'fileName' parameter is required." });
            }

            return await GetDocumentStatusResultAsync(fileName, repository, vectorStore, servedUploadDirectories);
        })
        .RequireAuthorization("DocumentUploadPolicy")
        .WithName("GetDocumentStatusByPath")
        .WithSummary("Get document upload status by filename (route parameter)")
        .WithDescription("Checks whether a document has already been uploaded by filename and returns its current status.")
        .Produces<DocumentStatusResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        app.MapGet("/api/documents/{fileName}/status", async (
            string fileName,
            IDocumentRepository repository,
            IVectorStore vectorStore) =>
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return Results.BadRequest(new { error = "The 'fileName' parameter is required." });
            }

            return await GetDocumentStatusResultAsync(fileName, repository, vectorStore, servedUploadDirectories);
        })
        .RequireAuthorization("DocumentUploadPolicy")
        .WithName("GetDocumentStatusByName")
        .WithSummary("Get document upload status by filename (route parameter)")
        .WithDescription("Checks whether a document has already been uploaded by filename and returns its current status.")
        .Produces<DocumentStatusResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        // DOCUMENT UPLOAD ENDPOINT
        app.MapPost("/api/documents/upload", async (
            HttpRequest request,
            IDocumentProcessor processor,
            IDocumentQueue queue,
            IDocumentRepository repository,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("DocumentEndpoints");
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Invalid content type. Multipart form-data expected.");
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest("No file was uploaded under the field name 'file'.");
            }

            if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Only PDF documents are supported.");
            }

            bool background = request.Query.TryGetValue("background", out var bgStr) && bool.TryParse(bgStr, out var bg) && bg;

            if (background)
            {
                logger.LogInformation("Uploading and queuing file for background processing: {FileName}", file.FileName);

                var memoryStream = new MemoryStream();
                await file.CopyToAsync(memoryStream, cancellationToken);
                memoryStream.Position = 0;

                var (articleId, sourceDocId) = ArticleDocumentNameParser.Parse(file.FileName);

                var existingDoc = await repository.GetDocumentByFileNameAsync(file.FileName);
                if (existingDoc == null && !string.IsNullOrWhiteSpace(sourceDocId))
                {
                    existingDoc = await repository.GetDocumentBySourceDocIdAsync(sourceDocId);
                }

                var documentId = existingDoc?.Id
                    ?? (!string.IsNullOrWhiteSpace(sourceDocId) ? sourceDocId : Guid.NewGuid().ToString());

                var tempDocument = new Document(
                    Id: documentId,
                    FileName: file.FileName,
                    FilePath: existingDoc?.FilePath ?? "Queued/Background",
                    UploadedAt: DateTime.UtcNow,
                    Status: DocumentStatus.Pending,
                    ArticleId: articleId,
                    SourceDocumentId: sourceDocId
                );
                await repository.AddDocumentAsync(tempDocument);

                queue.QueueBackgroundWorkItem(async token =>
                {
                    try
                    {
                        await processor.ProcessPdfAsync(memoryStream, file.FileName, documentId, token);
                        Program.DirFileCache.Clear();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Background processing failed for file: {FileName}", file.FileName);
                    }
                    finally
                    {
                        await memoryStream.DisposeAsync();
                    }
                });

                return Results.Accepted($"/api/documents", tempDocument);
            }
            else
            {
                logger.LogInformation("Uploading and processing file synchronously: {FileName}", file.FileName);

                using var stream = file.OpenReadStream();
                var document = await processor.ProcessPdfAsync(stream, file.FileName, cancellationToken: cancellationToken);
                Program.DirFileCache.Clear();

                if (document.Status == DocumentStatus.Failed)
                {
                    return Results.BadRequest(new
                    {
                        error = document.ErrorMessage ?? "Behandlingen af dokumentet mislykkedes.",
                        status = "Failed",
                        id = document.Id,
                        fileName = document.FileName
                    });
                }

                return Results.Ok(document);
            }
        })
        .RequireAuthorization("DocumentUploadPolicy")
        .WithName("UploadDocument");

        return app;
    }

    private static void DeletePhysicalDocumentFile(string? filePath, string? fileName, IEnumerable<string> uploadDirectories)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var rawName = Path.GetFileName(Uri.UnescapeDataString(filePath));
            if (!string.IsNullOrWhiteSpace(rawName))
            {
                candidates.Add(rawName);
                if (rawName.Length > 37 && rawName[36] == '_')
                {
                    candidates.Add(rawName.Substring(37));
                }
            }

            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { }
            }
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var cleanFileName = Path.GetFileName(fileName);
            if (!string.IsNullOrWhiteSpace(cleanFileName))
            {
                candidates.Add(cleanFileName);
            }
        }

        foreach (var dir in uploadDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var c in candidates)
            {
                var direct = Path.Combine(dir, c);
                if (File.Exists(direct))
                {
                    try { File.Delete(direct); } catch { }
                }

                try
                {
                    var pattern = $"*_{c}";
                    var matches = Directory.GetFiles(dir, pattern);
                    foreach (var m in matches)
                    {
                        try { File.Delete(m); } catch { }
                    }
                }
                catch { }
            }
        }
    }

    private static async Task<IResult> GetDocumentStatusResultAsync(
        string fileName,
        IDocumentRepository repository,
        IVectorStore vectorStore,
        IEnumerable<string> uploadDirectories)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Results.BadRequest(new { error = "The 'fileName' parameter cannot be empty." });
        }

        var cleanName = fileName.Trim();
        var targetFileName = Path.GetFileName(cleanName);
        if (string.IsNullOrWhiteSpace(targetFileName))
        {
            targetFileName = cleanName;
        }

        // 1. Fetch documents from repository and vector store using fast indexed queries
        var matches = new List<Document>();

        var exactMatch = await repository.GetDocumentByFileNameAsync(targetFileName);
        if (exactMatch != null)
        {
            matches.Add(exactMatch);
        }
        else
        {
            var idMatch = await repository.GetDocumentByIdAsync(cleanName);
            if (idMatch != null)
            {
                matches.Add(idMatch);
            }
            else
            {
                var (pagedDocs, _) = await repository.GetPagedDocumentsAsync(1, 10, targetFileName);
                matches.AddRange(pagedDocs);
            }
        }

        try
        {
            var vectorDocs = await vectorStore.GetDocumentsAsync(25);
            foreach (var vDoc in vectorDocs)
            {
                if (string.Equals(vDoc.FileName, cleanName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(vDoc.FileName), targetFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(vDoc.Id, cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!matches.Any(m => m.Id == vDoc.Id))
                    {
                        matches.Add(vDoc);
                    }
                }
            }
        }
        catch
        {
            // Vector store fallback
        }

        if (matches.Count > 0)
        {
            int MatchScore(Document d)
            {
                if (string.Equals(d.FileName, cleanName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(d.FileName), targetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
                return 1;
            }

            int StatusPriority(DocumentStatus status) => status switch
            {
                DocumentStatus.Processed => 1,
                DocumentStatus.Skipped => 2,
                DocumentStatus.Processing => 3,
                DocumentStatus.Pending => 4,
                DocumentStatus.Failed => 5,
                _ => 6
            };

            var bestMatch = matches
                .OrderBy(MatchScore)
                .ThenBy(m => StatusPriority(m.Status))
                .ThenByDescending(m => m.UploadedAt)
                .First();

            var resolvedFilePath = DocumentUrlResolver.ToDocumentUrl(bestMatch.FilePath, bestMatch.FileName, uploadDirectories);

            return Results.Ok(new DocumentStatusResponse(
                HasBeenUploaded: true,
                Status: bestMatch.Status.ToString(),
                DocumentId: bestMatch.Id,
                FileName: bestMatch.FileName,
                FilePath: resolvedFilePath,
                UploadedAt: bestMatch.UploadedAt,
                ErrorMessage: bestMatch.ErrorMessage,
                Language: bestMatch.Language,
                ArticleId: bestMatch.ArticleId,
                SourceDocumentId: bestMatch.SourceDocumentId
            ));
        }

        // 3. Fallback: check if the file exists directly in served upload directories
        var existingUrl = DocumentUrlResolver.ToExistingUploadUrl(targetFileName, uploadDirectories);
        if (!string.IsNullOrWhiteSpace(existingUrl))
        {
            return Results.Ok(new DocumentStatusResponse(
                HasBeenUploaded: true,
                Status: "Uploaded",
                DocumentId: null,
                FileName: targetFileName,
                FilePath: existingUrl,
                UploadedAt: null,
                ErrorMessage: null
            ));
        }

        // 4. Not uploaded / not found
        return Results.Ok(new DocumentStatusResponse(
            HasBeenUploaded: false,
            Status: "NotUploaded",
            DocumentId: null,
            FileName: targetFileName,
            FilePath: null,
            UploadedAt: null,
            ErrorMessage: null
        ));
    }
}
