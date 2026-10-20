using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
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
                if (!string.IsNullOrWhiteSpace(existingState.OwnerUserId) &&
                    !string.Equals(existingState.OwnerUserId, ownerId, StringComparison.OrdinalIgnoreCase) &&
                    !UserSecurityHelper.IsUserAdmin(user))
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
                    url += $"#page={item.Index + 1}{searchParam}";
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
            logger.LogInformation(
                "HTTP POST /api/query completed in {RequestDurationMs}ms (ConversationId: {ConversationId}, Citations: {CitationCount}).",
                requestStopwatch.ElapsedMilliseconds, effectiveConversationId, citations.Count);

            return Results.Ok(new QueryResponse(htmlAnswer, citations, effectiveConversationId));
        })
        .RequireAuthorization()
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
                if (!string.IsNullOrWhiteSpace(existingState.OwnerUserId) &&
                    !string.Equals(existingState.OwnerUserId, ownerId, StringComparison.OrdinalIgnoreCase) &&
                    !UserSecurityHelper.IsUserAdmin(httpContext.User))
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
                                    url += $"#page={item.Index + 1}{searchParam}";
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
        .RequireAuthorization()
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
