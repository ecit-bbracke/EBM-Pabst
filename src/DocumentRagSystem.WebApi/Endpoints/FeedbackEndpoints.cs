using System;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace DocumentRagSystem.WebApi.Endpoints;

public record SubmitFeedbackRequest(
    string Comment,
    string? ContactEmail = null,
    string? SearchPrompts = null,
    string? AssistantAnswer = null,
    string? ConversationId = null
);

public static class FeedbackEndpoints
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/feedback", async (
            SubmitFeedbackRequest request,
            ClaimsPrincipal user,
            IFeedbackRepository feedbackRepo,
            UserManager<IdentityUser> userManager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("FeedbackEndpoints");

            // 1. Mandatory Comment Validation
            if (string.IsNullOrWhiteSpace(request.Comment) || request.Comment.Trim().Length == 0)
            {
                return Results.BadRequest(new { message = "Din kommentar skal udfyldes." });
            }

            if (request.Comment.Length > 5000)
            {
                return Results.BadRequest(new { message = "Kommentaren må højst indeholde 5000 tegn." });
            }

            // 2. Optional Email Address Validation
            string? validContactEmail = null;
            if (!string.IsNullOrWhiteSpace(request.ContactEmail))
            {
                var trimmedEmail = request.ContactEmail.Trim();
                if (!EmailRegex.IsMatch(trimmedEmail) || !new EmailAddressAttribute().IsValid(trimmedEmail))
                {
                    return Results.BadRequest(new { message = "Ugyldig e-mailadresse." });
                }
                validContactEmail = trimmedEmail;
            }

            // 3. Extract User Identity from Authenticated Session
            var identityUser = await userManager.GetUserAsync(user);
            var userId = identityUser?.Id ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? "authenticated_user";
            var userEmail = identityUser?.Email ?? user.FindFirstValue(ClaimTypes.Email) ?? (user.Identity?.Name?.Contains('@') == true ? user.Identity.Name : null);

            var feedback = new UserFeedback(
                Id: Guid.NewGuid().ToString(),
                UserId: userId,
                UserEmail: userEmail,
                Comment: request.Comment.Trim(),
                ContactEmail: validContactEmail,
                SearchPrompts: request.SearchPrompts?.Trim(),
                AssistantAnswer: request.AssistantAnswer?.Trim(),
                ConversationId: request.ConversationId?.Trim(),
                CreatedAt: DateTime.UtcNow
            );

            var saved = await feedbackRepo.SaveFeedbackAsync(feedback);

            logger.LogInformation("Feedback {FeedbackId} saved from user {UserId} (Conversation: {ConvId}).",
                saved.Id, userId, request.ConversationId);

            return Results.Ok(new
            {
                message = "Tak for din feedback!",
                feedbackId = saved.Id
            });
        })
        .RequireAuthorization("QueryPolicy")
        .WithName("SubmitFeedback");

        app.MapGet("/api/feedback", async (
            IFeedbackRepository feedbackRepo,
            ClaimsPrincipal user) =>
        {
            // Only administrators may retrieve feedback entries
            if (!UserSecurityHelper.IsUserAdmin(user))
            {
                return Results.Forbid();
            }

            var feedbacks = await feedbackRepo.GetAllFeedbackAsync(limit: 200);
            return Results.Ok(feedbacks);
        })
        .RequireAuthorization()
        .WithName("GetAllFeedback");

        return app;
    }
}
