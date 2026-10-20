using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentRagSystem.WebApi.Middleware;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public string ApiKey { get; set; } = string.Empty;
}

public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // If no API key has been configured on the server, do not allow API key authentication
        if (string.IsNullOrWhiteSpace(Options.ApiKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? providedKey = null;

        // 1. Check "X-Api-Key" header
        if (Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var headerValues))
        {
            providedKey = headerValues.FirstOrDefault();
        }
        // 2. Check "Authorization: Bearer <key>" or "Authorization: ApiKey <key>"
        else if (Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            var authHeader = authHeaderValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    providedKey = authHeader.Substring(7).Trim();
                }
                else if (authHeader.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
                {
                    providedKey = authHeader.Substring(7).Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(providedKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!string.Equals(providedKey, Options.ApiKey, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key provided."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "ApiKeyClient"),
            new Claim(ClaimTypes.Role, "ApiUploadUser"),
            new Claim("scope", "documents:upload"),
            new Claim("auth_method", "api_key")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
