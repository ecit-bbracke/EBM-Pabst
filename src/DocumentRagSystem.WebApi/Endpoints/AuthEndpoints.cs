using System;
using System.Security.Claims;
using DocumentRagSystem.WebApi.DTOs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;

namespace DocumentRagSystem.WebApi.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (
            LoginRequest model,
            SignInManager<IdentityUser> signInManager,
            UserManager<IdentityUser> userManager) =>
        {
            if (string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.Password))
            {
                return Results.BadRequest(new { message = "Email og adgangskode skal udfyldes." });
            }

            var user = await userManager.FindByEmailAsync(model.Email) ?? await userManager.FindByNameAsync(model.Email);
            if (user == null)
            {
                return Results.Unauthorized();
            }

            var result = await signInManager.PasswordSignInAsync(user, model.Password, isPersistent: model.RememberMe, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                return Results.Ok(new { message = "Logget ind", email = user.Email });
            }

            if (result.IsLockedOut)
            {
                return Results.BadRequest(new { message = "Kontoen er midlertidigt låst pga. for mange mislykkede loginforsøg. Prøv igen om 15 minutter." });
            }

            return Results.Unauthorized();
        })
        .AllowAnonymous()
        .WithName("Login");

        app.MapPost("/api/auth/logout", async (SignInManager<IdentityUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Ok(new { message = "Logget ud" });
        })
        .RequireAuthorization()
        .WithName("Logout");

        app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
        {
            if (user.Identity?.IsAuthenticated == true)
            {
                return Results.Ok(new { isAuthenticated = true, email = user.Identity.Name, isAdmin = UserSecurityHelper.IsUserAdmin(user) });
            }
            return Results.Unauthorized();
        })
        .RequireAuthorization()
        .WithName("GetCurrentUser");

        app.MapPost("/api/auth/change-password", async (
            ChangePasswordRequest model,
            ClaimsPrincipal user,
            UserManager<IdentityUser> userManager,
            SignInManager<IdentityUser> signInManager) =>
        {
            if (string.IsNullOrWhiteSpace(model.CurrentPassword))
            {
                return Results.BadRequest(new { message = "Nuværende adgangskode skal udfyldes." });
            }

            if (string.IsNullOrWhiteSpace(model.NewPassword) || model.NewPassword.Length < 6)
            {
                return Results.BadRequest(new { message = "Ny adgangskode skal være på mindst 6 tegn." });
            }

            var identityUser = await userManager.GetUserAsync(user);
            if (identityUser == null)
            {
                return Results.Unauthorized();
            }

            var result = await userManager.ChangePasswordAsync(identityUser, model.CurrentPassword, model.NewPassword);
            if (result.Succeeded)
            {
                // Refresh sign-in cookie so security stamp is updated without logging the user out
                await signInManager.RefreshSignInAsync(identityUser);
                return Results.Ok(new { message = "Adgangskoden er blevet opdateret." });
            }

            var errorDescriptions = string.Join(" ", result.Errors.Select(e => e.Description));
            if (errorDescriptions.Contains("Incorrect password", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Den nuværende adgangskode er forkert." });
            }

            return Results.BadRequest(new { message = errorDescriptions });
        })
        .RequireAuthorization()
        .WithName("ChangePassword");

        return app;
    }
}
