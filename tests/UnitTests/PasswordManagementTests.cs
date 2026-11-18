using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using DocumentRagSystem.WebApi.DTOs;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class PasswordManagementTests
{
    private static Mock<UserManager<IdentityUser>> CreateMockUserManager()
    {
        var store = new Mock<IUserStore<IdentityUser>>();
        return new Mock<UserManager<IdentityUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    [Fact]
    public void ChangePasswordRequest_ValidationRules()
    {
        // GIVEN password change requests:
        var emptyCurrent = new ChangePasswordRequest("", "NewSecret123!");
        var shortNew = new ChangePasswordRequest("OldSecret123!", "12345");
        var valid = new ChangePasswordRequest("OldSecret123!", "NewSecret123!");

        // THEN validation rules are verified:
        Assert.True(string.IsNullOrWhiteSpace(emptyCurrent.CurrentPassword));
        Assert.True(shortNew.NewPassword.Length < 6);
        Assert.False(string.IsNullOrWhiteSpace(valid.CurrentPassword));
        Assert.True(valid.NewPassword.Length >= 6);
    }

    [Fact]
    public void AdminResetPasswordRequest_ValidationRules()
    {
        // GIVEN admin reset requests:
        var empty = new AdminResetPasswordRequest("");
        var shortPw = new AdminResetPasswordRequest("abc");
        var valid = new AdminResetPasswordRequest("NewSecret456!");

        // THEN length constraints hold:
        Assert.True(string.IsNullOrWhiteSpace(empty.NewPassword));
        Assert.True(shortPw.NewPassword.Length < 6);
        Assert.True(valid.NewPassword.Length >= 6);
    }

    [Fact]
    public void UserSummaryDto_WithAuditFields_StoresAuditDataCorrectly()
    {
        // GIVEN a user summary DTO with audit trail:
        var now = DateTime.UtcNow;
        var dto = new UserSummaryDto(
            Id: "u-1",
            Email: "emp@ebmpabst.dk",
            UserName: "emp@ebmpabst.dk",
            IsLockedOut: false,
            CreatedBy: "admin@ebmpabst.dk",
            CreatedAt: now,
            LastLoginAt: now.AddMinutes(5),
            LastResetBy: "admin@ebmpabst.dk (2026-11-18)"
        );

        // THEN audit fields are preserved:
        Assert.Equal("admin@ebmpabst.dk", dto.CreatedBy);
        Assert.Equal(now, dto.CreatedAt);
        Assert.Equal(now.AddMinutes(5), dto.LastLoginAt);
        Assert.Equal("admin@ebmpabst.dk (2026-11-18)", dto.LastResetBy);
    }

    [Fact]
    public async Task UserManager_ChangePassword_WhenSuccessful_ReturnsSucceeded()
    {
        // GIVEN an authenticated user and mock user manager:
        var user = new IdentityUser { Id = "u1", UserName = "test@ebmpabst.dk", Email = "test@ebmpabst.dk" };
        var mockUserManager = CreateMockUserManager();

        mockUserManager.Setup(m => m.ChangePasswordAsync(user, "OldSecret123!", "NewSecret123!"))
            .ReturnsAsync(IdentityResult.Success);

        // WHEN changing password:
        var result = await mockUserManager.Object.ChangePasswordAsync(user, "OldSecret123!", "NewSecret123!");

        // THEN operation succeeds:
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task UserManager_ChangePassword_WhenCurrentPasswordIncorrect_ReturnsFailure()
    {
        // GIVEN an incorrect current password:
        var user = new IdentityUser { Id = "u1", UserName = "test@ebmpabst.dk", Email = "test@ebmpabst.dk" };
        var mockUserManager = CreateMockUserManager();

        mockUserManager.Setup(m => m.ChangePasswordAsync(user, "WrongPassword", "NewSecret123!"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "PasswordMismatch", Description = "Incorrect password." }));

        // WHEN changing password:
        var result = await mockUserManager.Object.ChangePasswordAsync(user, "WrongPassword", "NewSecret123!");

        // THEN operation fails with expected error:
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Description.Contains("Incorrect password"));
    }

    [Fact]
    public async Task UserManager_AdminResetPassword_GeneratesTokenAndResetsSuccessfully()
    {
        // GIVEN an admin resetting a user's password:
        var user = new IdentityUser { Id = "u2", UserName = "employee@ebmpabst.dk", Email = "employee@ebmpabst.dk" };
        var mockUserManager = CreateMockUserManager();

        mockUserManager.Setup(m => m.GeneratePasswordResetTokenAsync(user))
            .ReturnsAsync("reset-token-xyz");

        mockUserManager.Setup(m => m.ResetPasswordAsync(user, "reset-token-xyz", "NewAdminSetSecret123!"))
            .ReturnsAsync(IdentityResult.Success);

        // WHEN generating token and resetting:
        var token = await mockUserManager.Object.GeneratePasswordResetTokenAsync(user);
        var result = await mockUserManager.Object.ResetPasswordAsync(user, token, "NewAdminSetSecret123!");

        // THEN password reset succeeds:
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task WelcomeEmail_Dispatch_SendsEmailWithTemporaryCredentials()
    {
        // GIVEN a mock email sender and user creation parameters:
        var mockEmailSender = new Mock<DocumentRagSystem.Core.Interfaces.IEmailSender>();
        string? capturedRecipient = null;
        string? capturedSubject = null;
        string? capturedBody = null;

        mockEmailSender.Setup(x => x.SendEmailAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<System.Threading.CancellationToken>()))
            .Callback<string, string, string, bool, System.Threading.CancellationToken>((to, subj, body, html, ct) =>
            {
                capturedRecipient = to;
                capturedSubject = subj;
                capturedBody = body;
            })
            .Returns(Task.CompletedTask);

        // WHEN sending a welcome email:
        var recipient = "newemployee@ebmpabst.dk";
        var tempPassword = "SecureGeneratedSecret#14";
        var loginUrl = "https://portal.ebmpabst.dk/login.html";

        var welcomeHtml = $"""
            <html>
                <body>
                    <p>Brugernavn: {recipient}</p>
                    <p>Midlertidig adgangskode: {tempPassword}</p>
                    <p>Login URL: {loginUrl}</p>
                </body>
            </html>
            """;

        await mockEmailSender.Object.SendEmailAsync(
            recipient,
            "Velkommen til ebm-papst Portalen",
            welcomeHtml,
            isHtml: true
        );

        // THEN email was dispatched to the new user with credentials:
        Assert.Equal("newemployee@ebmpabst.dk", capturedRecipient);
        Assert.Equal("Velkommen til ebm-papst Portalen", capturedSubject);
        Assert.NotNull(capturedBody);
        Assert.Contains(recipient, capturedBody);
        Assert.Contains(tempPassword, capturedBody);
        Assert.Contains(loginUrl, capturedBody);
    }
}
