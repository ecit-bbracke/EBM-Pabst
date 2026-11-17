using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
            UserManager<IdentityUser> userManager,
            IEmailSender emailSender,
            ILoggerFactory loggerFactory,
            HttpContext httpContext) =>
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
                var logger = loggerFactory.CreateLogger("AdminEndpoints");
                logger.LogInformation("New user created: {Email} by admin {Admin}.", newUser.Email, currentUser.Identity?.Name);

                bool emailSent = false;
                try
                {
                    var loginUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/login.html";
                    var welcomeHtml = BuildWelcomeEmailHtml(newUser.Email, model.Password, loginUrl);
                    await emailSender.SendEmailAsync(newUser.Email, "Velkommen til ebm-papst Portalen", welcomeHtml, isHtml: true);
                    emailSent = true;
                    logger.LogInformation("Welcome email successfully dispatched to {Email}.", newUser.Email);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not send welcome email to {Email}: {Error}", newUser.Email, ex.Message);
                }

                return Results.Ok(new
                {
                    id = newUser.Id,
                    email = newUser.Email,
                    userName = newUser.UserName,
                    isLockedOut = false,
                    emailSent = emailSent,
                    message = emailSent
                        ? $"Bruger '{newUser.Email}' oprettet! Velkomstmail er sendt til brugeren."
                        : $"Bruger '{newUser.Email}' oprettet! (Bemærk: Kunne ikke afsende velkomstmail automatisk)."
                });
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

        app.MapPost("/api/admin/users/{id}/reset-password", async (
            string id,
            AdminResetPasswordRequest model,
            ClaimsPrincipal currentUser,
            UserManager<IdentityUser> userManager,
            IEmailSender emailSender,
            ILoggerFactory loggerFactory,
            HttpContext httpContext) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(model.NewPassword) || model.NewPassword.Length < 6)
            {
                return Results.BadRequest(new { message = "Ny adgangskode skal være på mindst 6 tegn." });
            }

            var user = await userManager.FindByIdAsync(id);
            if (user == null)
            {
                return Results.NotFound(new { message = "Bruger ikke fundet." });
            }

            var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
            var result = await userManager.ResetPasswordAsync(user, resetToken, model.NewPassword);

            if (result.Succeeded)
            {
                await userManager.SetLockoutEndDateAsync(user, null);
                await userManager.ResetAccessFailedCountAsync(user);

                var logger = loggerFactory.CreateLogger("AdminEndpoints");
                logger.LogInformation("Password reset for user: {Email} by admin {Admin}.", user.Email, currentUser.Identity?.Name);

                bool emailSent = false;
                try
                {
                    var loginUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/login.html";
                    var resetHtml = BuildPasswordResetEmailHtml(user.Email ?? string.Empty, model.NewPassword, loginUrl);
                    await emailSender.SendEmailAsync(user.Email ?? string.Empty, "Din adgangskode til ebm-papst Portalen er blevet nulstillet", resetHtml, isHtml: true);
                    emailSent = true;
                    logger.LogInformation("Password reset notification email sent to {Email}.", user.Email);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not send password reset notification email to {Email}: {Error}", user.Email, ex.Message);
                }

                return Results.Ok(new
                {
                    emailSent = emailSent,
                    message = emailSent
                        ? $"Adgangskoden for '{user.Email}' er blevet nulstillet, og email er sendt til brugeren."
                        : $"Adgangskoden for '{user.Email}' er blevet nulstillet."
                });
            }

            return Results.BadRequest(new { message = string.Join(", ", result.Errors.Select(e => e.Description)) });
        })
        .RequireAuthorization()
        .WithName("AdminResetPassword");

        // CLEAR QUERY CACHE ENDPOINT
        app.MapPost("/api/admin/clear-cache", () =>
        {
            TechnicalRagOrchestrator.InvalidateQueryCache();
            return Results.Ok(new { message = "Query cache invalidated." });
        })
        .RequireAuthorization("QueryPolicy")
        .WithName("ClearQueryCache");

        // METADATA BACKFILL ENDPOINT
        app.MapPost("/api/admin/backfill-metadata", async (
            ClaimsPrincipal currentUser,
            IDocumentRepository repository,
            ITextExtractor textExtractor,
            IVectorStore? vectorStore,
            bool? forceAll) =>
        {
            if (!UserSecurityHelper.IsUserAdmin(currentUser) && !currentUser.HasClaim("auth_method", "api_key"))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var codeParser = new EbmProductCodeParser();
            var allDocs = (await repository.GetAllDocumentsAsync()).ToList();
            var shouldForce = forceAll.GetValueOrDefault(false);
            var docsToUpdate = shouldForce
                ? allDocs
                : allDocs.Where(d => string.IsNullOrWhiteSpace(d.ProductCode) ||
                                     string.IsNullOrWhiteSpace(d.ProductCategory) ||
                                     string.IsNullOrWhiteSpace(d.Phase) ||
                                     string.IsNullOrWhiteSpace(d.NominalVoltage) ||
                                     !d.DiameterMm.HasValue ||
                                     !d.MinVoltage.HasValue ||
                                     !d.MaxVoltage.HasValue ||
                                     (d.Phase == "3~" && d.FileName != null && (d.FileName.Contains("8300100757") || d.FileName.Contains("VBH0280CSPFS")))).ToList();

            var updates = new List<DocumentMetadataUpdate>();
            int electricalCount = 0;

            foreach (var doc in docsToUpdate)
            {
                if (string.IsNullOrWhiteSpace(doc.FileName)) continue;

                string? extractedText = null;

                // Attempt to resolve file on disk to extract rich electrical specifications from tables
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
                            var p1 = Path.Combine(dir, urlName);
                            if (File.Exists(p1)) { resolvedPath = p1; break; }
                        }
                        if (!string.IsNullOrWhiteSpace(cleanName))
                        {
                            var p2 = Path.Combine(dir, cleanName);
                            if (File.Exists(p2)) { resolvedPath = p2; break; }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
                {
                    try
                    {
                        using var fileStream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        extractedText = await textExtractor.ExtractTextAsync(fileStream);
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(extractedText) && vectorStore != null)
                {
                    try
                    {
                        var chunks = (await vectorStore.GetAllChunksByDocumentIdAsync(doc.Id)).ToList();
                        if (chunks.Count > 0)
                        {
                            extractedText = string.Join("\n", chunks.Take(4).Select(c => c.Text));
                        }
                    }
                    catch { }
                }

                var extractedProducts = codeParser.ExtractProductsFromText(doc.FileName!);
                if (extractedProducts.Count == 0 && !string.IsNullOrWhiteSpace(extractedText))
                {
                    extractedProducts = codeParser.ExtractProductsFromText(extractedText!.Substring(0, Math.Min(extractedText!.Length, 3000)));
                }

                var bestProduct = extractedProducts.FirstOrDefault();
                var techMeta = TechnicalMetadataExtractor.Extract(extractedText, doc.FileName, bestProduct);

                var productCode = techMeta.ProductCode ?? bestProduct?.CleanCode ?? doc.ProductCode;
                var productCategory = techMeta.ProductCategory ?? bestProduct?.FanFamily.ToString() ?? doc.ProductCategory;
                var phase = techMeta.Phase ?? doc.Phase;
                var nominalVoltage = techMeta.NominalVoltage ?? doc.NominalVoltage;
                var voltageRange = techMeta.VoltageRange ?? doc.VoltageRange;
                var powerWatts = techMeta.PowerWatts ?? doc.PowerWatts;
                var diameterMm = techMeta.DiameterMm ?? doc.DiameterMm ?? bestProduct?.ImpellerDiameterMm;

                if (!string.IsNullOrWhiteSpace(phase) || !string.IsNullOrWhiteSpace(nominalVoltage) || powerWatts.HasValue)
                {
                    electricalCount++;
                }

                updates.Add(new DocumentMetadataUpdate(
                    Id: doc.Id,
                    ProductCode: productCode,
                    ProductCategory: productCategory,
                    Phase: phase,
                    NominalVoltage: nominalVoltage,
                    VoltageRange: voltageRange,
                    PowerWatts: powerWatts,
                    DiameterMm: diameterMm,
                    MinVoltage: techMeta.MinVoltage ?? doc.MinVoltage,
                    MaxVoltage: techMeta.MaxVoltage ?? doc.MaxVoltage
                ));
            }

            if (updates.Count > 0)
            {
                await repository.BatchUpdateMetadataAsync(updates);
                TechnicalRagOrchestrator.InvalidateQueryCache();
            }

            return Results.Ok(new
            {
                message = $"Gennemførte backfill af tekniske metadata for {updates.Count} dokumenter ({electricalCount} med registrerede elektriske specifikationer).",
                totalExamined = allDocs.Count,
                updatedDocuments = updates.Count,
                electricalSpecsExtracted = electricalCount
            });
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

    private static string BuildWelcomeEmailHtml(string email, string password, string loginUrl)
    {
        var encodedEmail = WebUtility.HtmlEncode(email);
        var encodedPassword = WebUtility.HtmlEncode(password);
        var encodedLoginUrl = WebUtility.HtmlEncode(loginUrl);

        return $$"""
            <!DOCTYPE html>
            <html lang="da">
            <head>
              <meta charset="utf-8" />
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; color: #1e293b; background-color: #f8fafc; margin: 0; padding: 20px; }
                .card { max-width: 600px; margin: 0 auto; background: #ffffff; border: 1px solid #e2e8f0; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.05); }
                .header { background: #0c4a6e; color: #ffffff; padding: 24px; text-align: left; }
                .header h2 { margin: 0; font-size: 20px; font-weight: 700; }
                .header p { margin: 6px 0 0; font-size: 13px; opacity: 0.9; }
                .content { padding: 24px; font-size: 14px; line-height: 1.6; color: #334155; }
                .credentials-box { background-color: #f8fafc; border: 1px solid #e2e8f0; border-left: 4px solid #0284c7; padding: 16px; border-radius: 6px; margin: 20px 0; }
                .credentials-box table { width: 100%; border-collapse: collapse; font-size: 13px; }
                .credentials-box th, .credentials-box td { padding: 6px 8px; text-align: left; }
                .credentials-box th { width: 35%; color: #64748b; font-weight: 600; }
                .credentials-box td { font-family: Consolas, Monaco, monospace; font-weight: 600; color: #0f172a; }
                .btn { display: inline-block; background-color: #0284c7; color: #ffffff !important; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 600; font-size: 14px; margin: 16px 0; }
                .footer { background: #f8fafc; padding: 14px 24px; border-top: 1px solid #e2e8f0; font-size: 11px; color: #94a3b8; text-align: center; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="header">
                  <h2>ebm-papst Portal &bull; Velkommen som bruger</h2>
                  <p>Din adgang er nu oprettet</p>
                </div>
                <div class="content">
                  <p>Hej,</p>
                  <p>Der er oprettet en brugerkonto til dig på ebm-papst Portalen med adgang til AI Chat Assistenten, tekniske datablade og beregningsmoduler.</p>
                  
                  <div class="credentials-box">
                    <table>
                      <tr><th>Brugernavn:</th><td>{{encodedEmail}}</td></tr>
                      <tr><th>Midlertidig adgangskode:</th><td>{{encodedPassword}}</td></tr>
                      <tr><th>Login URL:</th><td><a href="{{encodedLoginUrl}}" style="color: #0284c7; font-family: sans-serif;">{{encodedLoginUrl}}</a></td></tr>
                    </table>
                  </div>

                  <div style="text-align: center;">
                    <a href="{{encodedLoginUrl}}" class="btn" target="_blank">Log ind på ebm-papst Portalen</a>
                  </div>

                  <p style="font-size: 12px; color: #64748b; margin-top: 20px;">
                    <strong>Sikkerhedstip:</strong> Vi anbefaler, at du efter første login skifter din adgangskode via <em>"Skift kode"</em>-knappen i portalens topmenu.
                  </p>
                </div>
                <div class="footer">
                  ebm-papst Document RAG Portal &bull; Denne besked er sendt automatisk ved oprettelse.
                </div>
              </div>
            </body>
            </html>
            """;
    }

    private static string BuildPasswordResetEmailHtml(string email, string newPassword, string loginUrl)
    {
        var encodedEmail = WebUtility.HtmlEncode(email);
        var encodedPassword = WebUtility.HtmlEncode(newPassword);
        var encodedLoginUrl = WebUtility.HtmlEncode(loginUrl);

        return $$"""
            <!DOCTYPE html>
            <html lang="da">
            <head>
              <meta charset="utf-8" />
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; color: #1e293b; background-color: #f8fafc; margin: 0; padding: 20px; }
                .card { max-width: 600px; margin: 0 auto; background: #ffffff; border: 1px solid #e2e8f0; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.05); }
                .header { background: #0c4a6e; color: #ffffff; padding: 24px; text-align: left; }
                .header h2 { margin: 0; font-size: 20px; font-weight: 700; }
                .header p { margin: 6px 0 0; font-size: 13px; opacity: 0.9; }
                .content { padding: 24px; font-size: 14px; line-height: 1.6; color: #334155; }
                .credentials-box { background-color: #f8fafc; border: 1px solid #e2e8f0; border-left: 4px solid #f59e0b; padding: 16px; border-radius: 6px; margin: 20px 0; }
                .credentials-box table { width: 100%; border-collapse: collapse; font-size: 13px; }
                .credentials-box th, .credentials-box td { padding: 6px 8px; text-align: left; }
                .credentials-box th { width: 35%; color: #64748b; font-weight: 600; }
                .credentials-box td { font-family: Consolas, Monaco, monospace; font-weight: 600; color: #0f172a; }
                .btn { display: inline-block; background-color: #0284c7; color: #ffffff !important; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 600; font-size: 14px; margin: 16px 0; }
                .footer { background: #f8fafc; padding: 14px 24px; border-top: 1px solid #e2e8f0; font-size: 11px; color: #94a3b8; text-align: center; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="header">
                  <h2>ebm-papst Portal &bull; Adgangskode Nulstillet</h2>
                  <p>En administrator har nulstillet din adgangskode</p>
                </div>
                <div class="content">
                  <p>Hej,</p>
                  <p>Din adgangskode til ebm-papst Portalen er blevet nulstillet af en administrator.</p>
                  
                  <div class="credentials-box">
                    <table>
                      <tr><th>Brugernavn:</th><td>{{encodedEmail}}</td></tr>
                      <tr><th>Ny adgangskode:</th><td>{{encodedPassword}}</td></tr>
                      <tr><th>Login URL:</th><td><a href="{{encodedLoginUrl}}" style="color: #0284c7; font-family: sans-serif;">{{encodedLoginUrl}}</a></td></tr>
                    </table>
                  </div>

                  <div style="text-align: center;">
                    <a href="{{encodedLoginUrl}}" class="btn" target="_blank">Log ind på ebm-papst Portalen</a>
                  </div>

                  <p style="font-size: 12px; color: #64748b; margin-top: 20px;">
                    <strong>Sikkerhedstip:</strong> Vi anbefaler, at du efter login ændrer denne midlertidige adgangskode via <em>"Skift kode"</em> i menuen.
                  </p>
                </div>
                <div class="footer">
                  ebm-papst Document RAG Portal &bull; Denne besked er sendt automatisk ved nulstilling.
                </div>
              </div>
            </body>
            </html>
            """;
    }
}
