using System;
using System.Collections.Generic;
using System.Security.Claims;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DocumentRagSystem.WebApi.Endpoints;

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        // GET CONVERSATION STATE
        app.MapGet("/api/conversation/{id}", async (string id, ClaimsPrincipal user, IConversationStateStore stateStore) =>
        {
            var state = await stateStore.GetStateAsync(id);
            if (state == null) return Results.NotFound();
            if (!IsAuthorizedForConversation(state, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(state);
        })
        .RequireAuthorization("QueryPolicy")
        .WithName("GetConversationState");

        // CLEAR CONVERSATION STATE
        app.MapDelete("/api/conversation/{id}", async (string id, ClaimsPrincipal user, IConversationStateStore stateStore) =>
        {
            var state = await stateStore.GetStateAsync(id);
            if (state != null && !IsAuthorizedForConversation(state, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            await stateStore.ClearStateAsync(id);
            return Results.NoContent();
        })
        .RequireAuthorization("QueryPolicy")
        .WithName("ClearConversationState");

        // REMOVE SPECIFIC CONSTRAINT FROM CONVERSATION
        app.MapDelete("/api/conversation/{id}/constraints/{attribute}", async (string id, string attribute, ClaimsPrincipal user, IConversationStateStore stateStore) =>
        {
            var state = await stateStore.GetStateAsync(id);
            if (state == null) return Results.NotFound();
            if (!IsAuthorizedForConversation(state, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            await stateStore.RemoveConstraintAsync(id, attribute);
            var updated = await stateStore.GetStateAsync(id);
            return updated != null ? Results.Ok(updated) : Results.NotFound();
        })
        .RequireAuthorization("QueryPolicy")
        .WithName("RemoveConversationConstraint");

        // RESET CONVERSATION CONSTRAINTS
        app.MapPost("/api/conversation/{id}/reset", async (string id, ClaimsPrincipal user, IConversationStateStore stateStore) =>
        {
            var state = await stateStore.GetStateAsync(id);
            if (state == null) return Results.NotFound();
            if (!IsAuthorizedForConversation(state, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var resetState = state with
            {
                ActiveConstraints = new List<ConversationConstraint>(),
                CandidateSet = null,
                TurnCount = state.TurnCount + 1,
                TopicVersion = state.TopicVersion + 1
            };
            await stateStore.SaveStateAsync(id, resetState);
            return Results.Ok(resetState);
        })
        .RequireAuthorization("QueryPolicy")
        .WithName("ResetConversationConstraints");

        return app;
    }

    private static bool IsAuthorizedForConversation(ConversationState state, ClaimsPrincipal user)
    {
        if (state == null) return false;
        if (string.IsNullOrWhiteSpace(state.OwnerUserId)) return true; // Legacy or system-created
        if (UserSecurityHelper.IsUserAdmin(user)) return true;
        bool isApiKey = user.HasClaim("auth_method", "api_key") ||
                        string.Equals(user.Identity?.Name, "ApiKeyClient", StringComparison.OrdinalIgnoreCase);
        if (isApiKey) return true;
        return string.Equals(state.OwnerUserId, user.Identity?.Name, StringComparison.OrdinalIgnoreCase);
    }
}
