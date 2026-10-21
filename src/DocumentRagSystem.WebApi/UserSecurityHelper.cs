using System;
using System.Security.Claims;

namespace DocumentRagSystem.WebApi;

public static class UserSecurityHelper
{
    public static bool IsUserAdmin(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        return user.IsInRole("Admin");
    }
}
