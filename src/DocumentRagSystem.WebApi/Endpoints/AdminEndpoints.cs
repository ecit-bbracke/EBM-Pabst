using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.WebApi.DTOs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.WebApi.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app, IEnumerable<string> servedUploadDirectories)
    {
        // USER MANAGEMENT ENDPOINTS
        app.MapGet("/api/admin/users", async (
            ClaimsPrincipal currentUser,
            UserManager<IdentityUser> userManager) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var users = await userManager.Users.ToListAsync();
            var dtos = users.Select(u => new UserSummaryDto(
                Id: u.Id,
                Email: u.Email ?? u.UserName ?? string.Empty,
                UserName: u.UserName,
                IsLockedOut: u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow
            )).ToList();

            return Results.Ok(dtos);
        })
        .RequireAuthorization()
        .WithName("GetAdminUsers");

        app.MapPost("/api/admin/users", async (
            CreateUserRequest model,
            ClaimsPrincipal currentUser,
            UserManager<IdentityUser> userManager) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.Password))
            {
                return Results.BadRequest(new { message = "Email og adgangskode skal udfyldes." });
            }

            var existing = await userManager.FindByEmailAsync(model.Email);
            if (existing != null)
            {
                return Results.BadRequest(new { message = "En bruger med denne email findes allerede." });
            }

            var newUser = new IdentityUser
            {
                UserName = model.Email.Trim(),
                Email = model.Email.Trim(),
                EmailConfirmed = true
            };

            var result = await userManager.CreateAsync(newUser, model.Password);
            if (result.Succeeded)
            {
                return Results.Ok(new UserSummaryDto(
                    Id: newUser.Id,
                    Email: newUser.Email,
                    UserName: newUser.UserName,
                    IsLockedOut: false
                ));
            }

            return Results.BadRequest(new { message = string.Join(", ", result.Errors.Select(e => e.Description)) });
        })
        .RequireAuthorization()
        .WithName("CreateAdminUser");

        app.MapDelete("/api/admin/users/{id}", async (
            string id,
            ClaimsPrincipal currentUser,
            UserManager<IdentityUser> userManager) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = await userManager.FindByIdAsync(id);
            if (user == null)
            {
                return Results.NotFound(new { message = "Bruger ikke fundet." });
            }

            if (string.Equals(user.Email, "admin@ebmpabst.dk", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Standard administratoren kan ikke slettes." });
            }

            if (string.Equals(user.Email, currentUser.Identity?.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Du kan ikke slette din egen bruger." });
            }

            var result = await userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                return Results.Ok(new { message = "Bruger slettet." });
            }

            return Results.BadRequest(new { message = string.Join(", ", result.Errors.Select(e => e.Description)) });
        })
        .RequireAuthorization()
        .WithName("DeleteAdminUser");

        app.MapPost("/api/admin/users/{id}/unlock", async (
            string id,
            ClaimsPrincipal currentUser,
            UserManager<IdentityUser> userManager) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = await userManager.FindByIdAsync(id);
            if (user == null)
            {
                return Results.NotFound(new { message = "Bruger ikke fundet." });
            }

            await userManager.SetLockoutEndDateAsync(user, null);
            await userManager.ResetAccessFailedCountAsync(user);

            return Results.Ok(new { message = "Bruger låst op." });
        })
        .RequireAuthorization()
        .WithName("UnlockAdminUser");

        // METADATA BACKFILL ENDPOINT
        app.MapPost("/api/admin/backfill-metadata", async (
            ClaimsPrincipal currentUser,
            IDocumentRepository repository) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var codeParser = new EbmProductCodeParser();
            var allDocs = await repository.GetAllDocumentsAsync();
            var docsToUpdate = allDocs.Where(d => string.IsNullOrWhiteSpace(d.ProductCode) || string.IsNullOrWhiteSpace(d.ProductCategory)).ToList();

            var updates = new List<(string Id, string? ProductCode, string? ProductCategory)>();
            foreach (var doc in docsToUpdate)
            {
                if (string.IsNullOrWhiteSpace(doc.FileName)) continue;

                var extractedProducts = codeParser.ExtractProductsFromText(doc.FileName);
                var bestProduct = extractedProducts.FirstOrDefault();
                if (bestProduct != null)
                {
                    updates.Add((doc.Id, bestProduct.CleanCode, bestProduct.FanFamily.ToString()));
                }
            }

            if (updates.Count > 0)
            {
                await repository.BatchUpdateMetadataAsync(updates);
                TechnicalRagOrchestrator.InvalidateQueryCache();
            }

            return Results.Ok(new { message = $"Gennemførte lyn-backfill af metadata. Opdaterede {updates.Count} ud af {docsToUpdate.Count} dokumenter uden metadata i én samlet transaktion." });
        })
        .RequireAuthorization()
        .WithName("BackfillMetadata");

        // FTS5 FULL-TEXT SEARCH BACKFILL ENDPOINT (Admin only)
        app.MapPost("/api/admin/backfill-fts", async (
            ClaimsPrincipal currentUser,
            IDocumentRepository repository,
            ITextExtractor textExtractor,
            IChunkingService chunkingService,
            IFullTextSearchStore ftsStore,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var logger = loggerFactory.CreateLogger("AdminEndpoints");
            var allDocs = (await repository.GetAllDocumentsAsync()).ToList();
            var indexedDocIds = await ftsStore.GetIndexedDocumentIdsAsync(cancellationToken);

            var unindexedDocs = allDocs
                .Where(d => d.Status == DocumentStatus.Processed && !indexedDocIds.Contains(d.Id))
                .ToList();

            if (unindexedDocs.Count == 0)
            {
                var currentChunkCount = await ftsStore.GetIndexedChunkCountAsync(cancellationToken);
                return Results.Ok(new
                {
                    message = $"Alle {allDocs.Count} dokumenter er allerede indekseret i SQLite FTS5 ({currentChunkCount} chunks totalt).",
                    indexedDocuments = 0,
                    totalChunks = currentChunkCount
                });
            }

            int indexedCount = 0;
            int chunkCount = 0;
            var batch = new List<DocumentChunk>();

            foreach (var doc in unindexedDocs)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Resolve local file path
                string? resolvedPath = null;
                if (!string.IsNullOrWhiteSpace(doc.FilePath) && File.Exists(doc.FilePath))
                {
                    resolvedPath = doc.FilePath;
                }
                else
                {
                    var urlName = Path.GetFileName(Uri.UnescapeDataString(doc.FilePath ?? string.Empty));
                    var cleanName = Path.GetFileName(Uri.UnescapeDataString(doc.FileName ?? string.Empty));

                    foreach (var dir in servedUploadDirectories)
                    {
                        if (!Directory.Exists(dir)) continue;

                        if (!string.IsNullOrWhiteSpace(urlName))
                        {
                            var candidate = Path.Combine(dir, urlName);
                            if (File.Exists(candidate))
                            {
                                resolvedPath = candidate;
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(cleanName))
                        {
                            var candidate = Path.Combine(dir, cleanName);
                            if (File.Exists(candidate))
                            {
                                resolvedPath = candidate;
                                break;
                            }

                            var suffixMatch = DocumentUrlResolver.ResolveUploadSuffixMatch(dir, cleanName);
                            if (!string.IsNullOrWhiteSpace(suffixMatch))
                            {
                                var fullSuffix = Path.Combine(dir, suffixMatch);
                                if (File.Exists(fullSuffix))
                                {
                                    resolvedPath = fullSuffix;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(resolvedPath)) continue;

                try
                {
                    await using var stream = File.OpenRead(resolvedPath);
                    var text = await textExtractor.ExtractTextAsync(stream);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var chunks = chunkingService.ChunkText(text, doc.Id);
                        foreach (var chunk in chunks)
                        {
                            batch.Add(chunk with
                            {
                                FileName = doc.FileName,
                                OriginalFileName = doc.FileName,
                                FilePath = doc.FilePath,
                                UploadedAt = doc.UploadedAt,
                                ArticleId = doc.ArticleId,
                                SourceDocumentId = doc.SourceDocumentId
                            });
                        }

                        indexedCount++;
                        chunkCount += chunks.Count();

                        if (batch.Count >= 1000)
                        {
                            await ftsStore.AddChunksAsync(batch, cancellationToken);
                            batch.Clear();
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[BackfillFts] Kunne ikke udtrække tekst for {FileName}", doc.FileName);
                }
            }

            if (batch.Count > 0)
            {
                await ftsStore.AddChunksAsync(batch, cancellationToken);
            }

            TechnicalRagOrchestrator.InvalidateQueryCache();
            var totalChunksAfter = await ftsStore.GetIndexedChunkCountAsync(cancellationToken);
            return Results.Ok(new
            {
                message = $"FTS5 indeksering fuldført! Indekserede {indexedCount} dokumenter ({chunkCount} nye chunks). Total i FTS5: {totalChunksAfter} chunks.",
                indexedDocuments = indexedCount,
                totalChunks = totalChunksAfter
            });
        })
        .RequireAuthorization()
        .WithName("BackfillFts");

        // CLEAN ORPHANED DOCUMENTS ENDPOINT (Admin only - removes DB records whose files were deleted from disk and have no vector embeddings)
        app.MapPost("/api/admin/clean-orphans", async (
            ClaimsPrincipal currentUser,
            IDocumentRepository repository,
            IVectorStore vectorStore,
            ILoggerFactory loggerFactory) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var logger = loggerFactory.CreateLogger("AdminEndpoints");
            var allDocs = (await repository.GetAllDocumentsAsync()).ToList();
            List<Document> vectorDocs;
            try
            {
                vectorDocs = (await vectorStore.GetDocumentsAsync(100_000))?.ToList() ?? new List<Document>();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Oprydning af forældreløse dokumenter afbrudt: Vektordatabasen er utilgængelig.");
                return Results.Problem(
                    title: "Vektordatabasen er utilgængelig",
                    detail: $"Kunne ikke verificere eksisterende vektorer i søgeindekset: {ex.Message}. Oprydningen er afbrudt for at forhindre utilsigtet sletning af metadata.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var vectorDocIds = new HashSet<string>(vectorDocs.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

            var deletedCount = 0;

            foreach (var doc in allDocs)
            {
                var resolvedUrl = DocumentUrlResolver.ToDocumentUrl(doc.FilePath, doc.FileName, servedUploadDirectories);
                if (string.IsNullOrWhiteSpace(resolvedUrl) && !vectorDocIds.Contains(doc.Id))
                {
                    await repository.DeleteDocumentAsync(doc.Id);
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                TechnicalRagOrchestrator.InvalidateQueryCache();
            }

            return Results.Ok(new
            {
                message = $"Oprydning fuldført. Fjernede {deletedCount} forældreløse dokumenter uden fysisk fil eller vektorer.",
                deletedCount,
                remainingCount = allDocs.Count - deletedCount
            });
        })
        .RequireAuthorization()
        .WithName("CleanOrphanedDocuments");

        return app;
    }
}
