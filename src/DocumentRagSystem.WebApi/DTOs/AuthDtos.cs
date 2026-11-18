namespace DocumentRagSystem.WebApi.DTOs;

public record LoginRequest(string Email, string Password, bool RememberMe = true);

public record UserDto(string Email, string? UserName = null);

public record CreateUserRequest(string Email, string Password);

public record UserSummaryDto(
    string Id,
    string Email,
    string? UserName,
    bool IsLockedOut,
    string? CreatedBy = null,
    DateTime? CreatedAt = null,
    DateTime? LastLoginAt = null,
    string? LastResetBy = null
);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record AdminResetPasswordRequest(string NewPassword);
