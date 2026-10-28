using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.WebApi.DTOs;
using FluentValidation;
using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.WebApi.Endpoints;

public static class QueryEndpoints
{
    public static string ConvertToPlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = Program.CleanLatexMath(text);

        // Pre-process HTML tags for line breaks and lists
        cleaned = Regex.Replace(cleaned, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"</p>", "\n\n", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"</li>", "\n", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<li>", "* ", RegexOptions.IgnoreCase);

        // Strip HTML tags (e.g. <p>, <strong>, <table>, <a href="...">, etc.)
        cleaned = Regex.Replace(cleaned, @"</?[a-zA-Z][^>]*>", string.Empty);

        // Render markdown to plain text via Markdig
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var plain = Markdown.ToPlainText(cleaned, pipeline);

        // Decode HTML entities (e.g. &amp;, &quot;, &lt;, &gt;, &#39;)
        plain = System.Net.WebUtility.HtmlDecode(plain);

        // Normalize excess empty lines
        plain = Regex.Replace(plain, @"\n{3,}", "\n\n");
        return plain.Trim();
    }
    public static IEndpointRouteBuilder MapQueryEndpoints(this IEndpointRouteBuilder app, IEnumerable<string> servedUploadDirectories)
    {
        // QUERY ENDPOINT WITH CITATIONS
        app.MapPost("/api/query", async (
            QueryRequest request,
            IValidator<QueryRequest> validator,
            IVectorStore vectorStore,
            ITechnicalRagOrchestrator orchestrator,
            IDanishDocumentResolver danishResolver,
            IConversationStateStore stateStore,
            ClaimsPrincipal user,
            HttpContext httpContext,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("QueryEndpoints");
            var requestStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var validationResult = await validator.ValidateAsync(request);
            if (!validationResult.IsValid)
            {
                return Results.BadRequest(validationResult.Errors.Select(e => e.ErrorMessage));
            }

            var ownerId = user.Identity?.Name;
            var targetConversationId = string.IsNullOrWhiteSpace(request.ConversationId)
                ? Guid.NewGuid().ToString()
                : request.ConversationId;

            ConversationState? initialConversationState = null;
            var existingState = await stateStore.GetStateAsync(targetConversationId);
            if (existingState != null)
            {
                bool isApiKey = user.HasClaim("auth_method", "api_key") ||
                                string.Equals(user.Identity?.Name, "ApiKeyClient", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(existingState.OwnerUserId) &&
                    !string.Equals(existingState.OwnerUserId, ownerId, StringComparison.OrdinalIgnoreCase) &&
                    !UserSecurityHelper.IsUserAdmin(user) &&
                    !isApiKey)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                initialConversationState = existingState;
            }
            else
            {
                initialConversationState = ConversationState.CreateNew(targetConversationId, ownerUserId: ownerId);
            }

            logger.LogInformation("Query received: {Question} (ConversationId: {ConversationId})", request.Question, targetConversationId);

            // Process through the Multi-Stage Technical RAG Orchestrator
            var (answer, chunks, trace) = await orchestrator.ProcessQueryAsync(request.Question, vectorStore, targetConversationId, initialConversationState);

            logger.LogInformation("Execution Trace: {TraceJson}", JsonSerializer.Serialize(trace));

            // Convert markdown answer to HTML with advanced extensions (for tables, bold, lists, etc.)
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            var htmlAnswer = Markdown.ToHtml(Program.CleanLatexMath(answer), pipeline);
            var citations = new List<CitationDto>();

            foreach (var item in chunks)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.DocumentId))
                {
                    continue;
                }

                var fileName = DocumentUrlResolver.FirstNonEmpty(item.FileName, item.DocumentId);
                var filePath = item.FilePath;
                var url = DocumentUrlResolver.ToDocumentUrl(filePath, fileName, servedUploadDirectories);

                if (!string.IsNullOrWhiteSpace(url) && url.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var searchKeyword = item.ArticleId;
                    if (string.IsNullOrWhiteSpace(searchKeyword) && !string.IsNullOrWhiteSpace(fileName))
                    {
                        var (parsedArt, _) = ArticleDocumentNameParser.Parse(fileName);
                        searchKeyword = parsedArt;
                    }
                    var searchParam = !string.IsNullOrWhiteSpace(searchKeyword)
                        ? $"&search={Uri.EscapeDataString(searchKeyword)}"
                        : string.Empty;
                    var targetPage = item.PageNumber ?? (item.Index + 1);
                    url += $"#page={targetPage}{searchParam}";
                }

                citations.Add(new CitationDto(
                    item.Id,
                    item.DocumentId,
                    string.Empty,
                    item.Index,
                    fileName,
                    url
                ));
            }

            // Append Danish companion documents as file references if available
            var danishCompanions = await danishResolver.FindDanishCompanionDocumentsAsync(chunks);
            foreach (var danishDoc in danishCompanions)
            {
                var daFileName = danishDoc.FileName;
                var daUrl = DocumentUrlResolver.ToDocumentUrl(danishDoc.FilePath, daFileName, servedUploadDirectories);

                if (!citations.Any(c => string.Equals(c.filename, daFileName, StringComparison.OrdinalIgnoreCase)))
                {
                    citations.Add(new CitationDto(
                        ChunkId: $"{danishDoc.Id}_danish_companion",
                        DocumentId: danishDoc.Id,
                        Text: "Dansk version (reference)",
                        Index: 0,
                        filename: daFileName,
                        filepath: daUrl,
                        language: "da",
                        isCompanion: true
                    ));
                }
            }

            var effectiveConversationId = trace.ConversationStateAfter?.ConversationId ?? request.ConversationId;
            requestStopwatch.Stop();

            var effectiveMode = !string.IsNullOrWhiteSpace(request.Mode)
                ? request.Mode
                : (!string.IsNullOrWhiteSpace(httpContext.Request.Query["mode"])
                    ? httpContext.Request.Query["mode"].ToString()
                    : "html");

            bool isJsonMode = string.Equals(effectiveMode, "json", StringComparison.OrdinalIgnoreCase);

            if (isJsonMode)
            {
                var filesList = new List<JsonQueryFileDto>();
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var allDocReferences = new List<(string? FilePath, string? FileName)>();
                foreach (var chunk in chunks)
                {
                    if (chunk != null)
                    {
                        var fn = DocumentUrlResolver.FirstNonEmpty(chunk.FileName, chunk.DocumentId);
                        allDocReferences.Add((chunk.FilePath, fn));
                    }
                }
                foreach (var daDoc in danishCompanions)
                {
                    if (daDoc != null)
                    {
                        allDocReferences.Add((daDoc.FilePath, daDoc.FileName));
                    }
                }

                foreach (var docRef in allDocReferences)
                {
                    var cleanFileName = Path.GetFileName(docRef.FileName ?? docRef.FilePath ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(cleanFileName) || !seenFiles.Add(cleanFileName))
                    {
                        continue;
                    }

                    var physicalPath = DocumentUrlResolver.ResolvePhysicalFilePath(docRef.FilePath, docRef.FileName, servedUploadDirectories);
                    if (!string.IsNullOrWhiteSpace(physicalPath) && File.Exists(physicalPath))
                    {
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(physicalPath);
                            var base64 = Convert.ToBase64String(bytes);
                            filesList.Add(new JsonQueryFileDto(cleanFileName, base64));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to read physical PDF file for JSON mode: {PhysicalPath}", physicalPath);
                        }
                    }
                    else
                    {
                        logger.LogWarning("Physical PDF file not found for JSON mode: {FileName} ({FilePath})", docRef.FileName, docRef.FilePath);
                    }
                }

                var plainTextAnswer = ConvertToPlainText(answer);

                logger.LogInformation(
                    "HTTP POST /api/query [JSON mode] completed in {RequestDurationMs}ms (ConversationId: {ConversationId}, Files: {FileCount}).",
                    requestStopwatch.ElapsedMilliseconds, effectiveConversationId, filesList.Count);

                return Results.Ok(new JsonQueryResponse(plainTextAnswer, filesList, effectiveConversationId));
            }

            logger.LogInformation(
                "HTTP POST /api/query [HTML mode] completed in {RequestDurationMs}ms (ConversationId: {ConversationId}, Citations: {CitationCount}).",
                requestStopwatch.ElapsedMilliseconds, effectiveConversationId, citations.Count);

            return Results.Ok(new QueryResponse(htmlAnswer, citations, effectiveConversationId));
        })
        .RequireAuthorization("QueryPolicy")
        .WithName("QueryDocuments");

        // STREAMING QUERY ENDPOINT (SSE)
        app.MapPost("/api/query/stream", async (
            QueryRequest request,
            IValidator<QueryRequest> validator,
            IVectorStore vectorStore,
            ITechnicalRagOrchestrator orchestrator,
            IDanishDocumentResolver danishResolver,
            IConversationStateStore stateStore,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("QueryEndpoints");
            var requestStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var validationResult = await validator.ValidateAsync(request, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Results.BadRequest(validationResult.Errors.Select(e => e.ErrorMessage));
            }

            var ownerId = httpContext.User.Identity?.Name;
            var targetConversationId = string.IsNullOrWhiteSpace(request.ConversationId)
                ? Guid.NewGuid().ToString()
                : request.ConversationId;

            ConversationState? initialConversationState = null;
            var existingState = await stateStore.GetStateAsync(targetConversationId);
            if (existingState != null)
            {
                bool isApiKey = httpContext.User.HasClaim("auth_method", "api_key") ||
                                string.Equals(httpContext.User.Identity?.Name, "ApiKeyClient", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(existingState.OwnerUserId) &&
                    !string.Equals(existingState.OwnerUserId, ownerId, StringComparison.OrdinalIgnoreCase) &&
                    !UserSecurityHelper.IsUserAdmin(httpContext.User) &&
                    !isApiKey)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                initialConversationState = existingState;
            }
            else
            {
                initialConversationState = ConversationState.CreateNew(targetConversationId, ownerUserId: ownerId);
            }

            logger.LogInformation("Streaming query received: {Question} (ConversationId: {ConversationId})", request.Question, targetConversationId);

            httpContext.Response.Headers.Append("Content-Type", "text/event-stream");
            httpContext.Response.Headers.Append("Cache-Control", "no-cache");
            httpContext.Response.Headers.Append("Connection", "keep-alive");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, httpContext.RequestAborted);
            var streamToken = linkedCts.Token;

            var stream = orchestrator.ProcessQueryStreamAsync(
                request.Question,
                vectorStore,
                targetConversationId,
                initialConversationState,
                streamToken);

            int chunkIndex = 0;
            try
            {
                await foreach (var evt in stream.WithCancellation(streamToken))
                {
                    if (streamToken.IsCancellationRequested) break;

                    if (evt.EventType == "citations")
                    {
                        var citations = new List<CitationDto>();
                        if (evt.Chunks != null && evt.Chunks.Count > 0)
                        {
                            foreach (var item in evt.Chunks)
                            {
                                if (item == null || string.IsNullOrWhiteSpace(item.DocumentId))
                                    continue;

                                var fileName = DocumentUrlResolver.FirstNonEmpty(item.FileName, item.DocumentId);
                                var filePath = item.FilePath;
                                var url = DocumentUrlResolver.ToDocumentUrl(filePath, fileName, servedUploadDirectories);

                                if (!string.IsNullOrWhiteSpace(url) && url.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
                                {
                                    var searchKeyword = item.ArticleId;
                                    if (string.IsNullOrWhiteSpace(searchKeyword) && !string.IsNullOrWhiteSpace(fileName))
                                    {
                                        var (parsedArt, _) = ArticleDocumentNameParser.Parse(fileName);
                                        searchKeyword = parsedArt;
                                    }
                                    var searchParam = !string.IsNullOrWhiteSpace(searchKeyword)
                                        ? $"&search={Uri.EscapeDataString(searchKeyword)}"
                                        : string.Empty;
                                    var targetPage = item.PageNumber ?? (item.Index + 1);
                                    url += $"#page={targetPage}{searchParam}";
                                }

                                citations.Add(new CitationDto(
                                    item.Id,
                                    item.DocumentId,
                                    string.Empty,
                                    item.Index,
                                    fileName,
                                    url
                                ));
                            }

                            var danishCompanions = await danishResolver.FindDanishCompanionDocumentsAsync(evt.Chunks, streamToken);
                            foreach (var danishDoc in danishCompanions)
                            {
                                var daFileName = danishDoc.FileName;
                                var daUrl = DocumentUrlResolver.ToDocumentUrl(danishDoc.FilePath, daFileName, servedUploadDirectories);

                                if (!citations.Any(c => string.Equals(c.filename, daFileName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    citations.Add(new CitationDto(
                                        ChunkId: $"{danishDoc.Id}_danish_companion",
                                        DocumentId: danishDoc.Id,
                                        Text: "Dansk version (reference)",
                                        Index: 0,
                                        filename: daFileName,
                                        filepath: daUrl,
                                        language: "da",
                                        isCompanion: true
                                    ));
                                }
                            }
                        }

                        var payload = JsonSerializer.Serialize(new { citations, conversationId = evt.ConversationId }, Program.JsonCamelCase);
                        await httpContext.Response.WriteAsync($"event: citations\ndata: {payload}\n\n", streamToken);
                        await httpContext.Response.Body.FlushAsync(streamToken);
                    }
                    else if (evt.EventType == "chunk")
                    {
                        chunkIndex++;
                        var payload = JsonSerializer.Serialize(new { text = evt.Text }, Program.JsonCamelCase);
                        await httpContext.Response.WriteAsync($"event: chunk\ndata: {payload}\n\n", streamToken);
                        await httpContext.Response.Body.FlushAsync(streamToken);
                    }
                    else if (evt.EventType == "replace")
                    {
                        var payload = JsonSerializer.Serialize(new { text = evt.Text }, Program.JsonCamelCase);
                        await httpContext.Response.WriteAsync($"event: replace\ndata: {payload}\n\n", streamToken);
                        await httpContext.Response.Body.FlushAsync(streamToken);
                    }
                    else if (evt.EventType == "done")
                    {
                        requestStopwatch.Stop();
                        logger.LogInformation(
                            "HTTP POST /api/query/stream completed in {RequestDurationMs}ms (ConversationId: {ConversationId}, TotalChunksStreamed: {ChunkCount}).",
                            requestStopwatch.ElapsedMilliseconds, evt.ConversationId, chunkIndex);

                        var payload = JsonSerializer.Serialize(new { conversationId = evt.ConversationId, trace = evt.Trace });
                        await httpContext.Response.WriteAsync($"event: done\ndata: {payload}\n\n", streamToken);
                        await httpContext.Response.Body.FlushAsync(streamToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                requestStopwatch.Stop();
                logger.LogInformation(
                    "HTTP POST /api/query/stream aborted by client disconnect after {DurationMs}ms (ConversationId: {ConversationId}, ChunksStreamed: {ChunkCount}).",
                    requestStopwatch.ElapsedMilliseconds, request.ConversationId, chunkIndex);
            }

            return Results.Empty;
        })
        .RequireAuthorization("QueryPolicy")
        .WithName("QueryDocumentsStream");

        // SPEECH-TO-TEXT AUDIO TRANSCRIPTION ENDPOINT (Cross-browser fallback for Firefox etc.)
        app.MapPost("/api/stt/transcribe", async (
            HttpRequest request,
            ISpeechToTextService sttService,
            CancellationToken cancellationToken) =>
        {
            string? lang = request.Query["lang"];
            string contentType = request.ContentType ?? "audio/webm";

            Stream audioStream;
            string resolvedMime = contentType;

            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                {
                    return Results.BadRequest(new { error = "Ingen lydfil modtaget." });
                }
                audioStream = file.OpenReadStream();
                resolvedMime = file.ContentType ?? "audio/webm";
            }
            else
            {
                audioStream = request.Body;
            }

            var text = await sttService.TranscribeAudioAsync(audioStream, resolvedMime, lang, cancellationToken);
            return Results.Ok(new { text });
        })
        .RequireAuthorization()
        .WithName("TranscribeAudio");

        return app;
    }
}
