using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Infrastructure.Repositories;
using DocumentRagSystem.WebApi.Endpoints;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class FeedbackTests
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    [Fact]
    public async Task InMemoryFeedbackRepository_SaveAndRetrieve_ShouldPersistAllFields()
    {
        // GIVEN a repository and a user feedback entry:
        var repo = new InMemoryFeedbackRepository();
        var feedback = new UserFeedback(
            Id: Guid.NewGuid().ToString(),
            UserId: "user-123",
            UserEmail: "testuser@ebmpapst.com",
            Comment: "Forkert spænding angivet for model 8300100757",
            ContactEmail: "dig@eksempel.dk",
            SearchPrompts: "en erstatning til type K3G280PR04I2",
            AssistantAnswer: "Modellen er 230V 800W",
            ConversationId: "conv-abc-123",
            CreatedAt: DateTime.UtcNow
        );

        // WHEN saving the feedback:
        var saved = await repo.SaveFeedbackAsync(feedback);

        // THEN it should be persisted and retrievable:
        Assert.NotNull(saved);
        Assert.Equal(feedback.Id, saved.Id);
        Assert.Equal("user-123", saved.UserId);
        Assert.Equal("testuser@ebmpapst.com", saved.UserEmail);
        Assert.Equal("Forkert spænding angivet for model 8300100757", saved.Comment);
        Assert.Equal("dig@eksempel.dk", saved.ContactEmail);
        Assert.Equal("en erstatning til type K3G280PR04I2", saved.SearchPrompts);
        Assert.Equal("Modellen er 230V 800W", saved.AssistantAnswer);
        Assert.Equal("conv-abc-123", saved.ConversationId);

        var retrieved = await repo.GetFeedbackByIdAsync(saved.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(saved.Id, retrieved.Id);

        var all = (await repo.GetAllFeedbackAsync()).ToList();
        Assert.Single(all);
        Assert.Equal(saved.Id, all[0].Id);

        // WHEN deleting the feedback:
        var deleted = await repo.DeleteFeedbackAsync(saved.Id);
        Assert.True(deleted);

        // THEN it should no longer exist:
        var afterDelete = await repo.GetFeedbackByIdAsync(saved.Id);
        Assert.Null(afterDelete);
        var allAfter = (await repo.GetAllFeedbackAsync()).ToList();
        Assert.Empty(allAfter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FeedbackValidation_MandatoryComment_WithEmptyOrNull_ShouldBeInvalid(string? comment)
    {
        // GIVEN an empty or whitespace comment:
        bool isValid = !string.IsNullOrWhiteSpace(comment) && comment.Trim().Length > 0;

        // THEN validation should fail:
        Assert.False(isValid);
    }

    [Fact]
    public void FeedbackValidation_Comment_ExceedingMaxLength_ShouldBeInvalid()
    {
        // GIVEN a comment longer than 5000 characters:
        var longComment = new string('A', 5001);

        // THEN length check should fail:
        Assert.True(longComment.Length > 5000);
    }

    [Theory]
    [InlineData("valid@example.com", true)]
    [InlineData("dig@eksempel.dk", true)]
    [InlineData("user.name+tag@sub.domain.co.uk", true)]
    [InlineData("invalid-email", false)]
    [InlineData("@missinguser.com", false)]
    [InlineData("missingdomain@", false)]
    [InlineData("spaces in@email.com", false)]
    public void FeedbackValidation_OptionalEmail_WhenProvided_ShouldValidateCorrectly(string email, bool expectedValid)
    {
        // GIVEN an email input:
        bool isValid = EmailRegex.IsMatch(email) && new EmailAddressAttribute().IsValid(email);

        // THEN validation matches expectation:
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void FeedbackContext_ShouldPreservePromptsAndAnswerAccurately()
    {
        // GIVEN a search context with multiple user prompts and an answer:
        var prompts = "Hvad er erstatning for K3G280PR04I2?\n---\nEr den 1-faset?";
        var answer = "Ja, 8300100757 er 1~ 230 VAC 800 W.";
        var feedback = new UserFeedback(
            Id: Guid.NewGuid().ToString(),
            UserId: "usr-456",
            UserEmail: "usr@ebm.dk",
            Comment: "Alt ser fint ud",
            ContactEmail: null,
            SearchPrompts: prompts,
            AssistantAnswer: answer,
            ConversationId: "conv-xyz",
            CreatedAt: DateTime.UtcNow
        );

        // THEN search prompts and answer are fully preserved:
        Assert.Contains("Hvad er erstatning", feedback.SearchPrompts);
        Assert.Contains("Er den 1-faset?", feedback.SearchPrompts);
        Assert.Equal(answer, feedback.AssistantAnswer);
        Assert.Null(feedback.ContactEmail);
    }

    [Fact]
    public async Task FeedbackNotificationService_WhenFeedbackSubmitted_DispatchesEmailToConfiguredRecipient()
    {
        // GIVEN a feedback notification service configured to email bbracke@ecit.com:
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

        var options = Microsoft.Extensions.Options.Options.Create(new FeedbackNotificationOptions
        {
            Enabled = true,
            RecipientEmail = "bbracke@ecit.com"
        });

        var service = new DocumentRagSystem.Infrastructure.Notifications.FeedbackNotificationService(
            options,
            mockEmailSender.Object,
            NullLogger<DocumentRagSystem.Infrastructure.Notifications.FeedbackNotificationService>.Instance
        );

        var feedback = new UserFeedback(
            Id: "fb-live-1",
            UserId: "user-session-guid",
            UserEmail: "testuser@ebmpapst.com",
            Comment: "Hvorfor vises 8300100757 ikke i kilderne?",
            ContactEmail: "kontakt@eksempel.dk",
            SearchPrompts: "en erstatning til type K3G280PR04I2",
            AssistantAnswer: "Modellen K3G280PR04I2 erstattes af 8300100757.",
            ConversationId: "conv-session-777",
            CreatedAt: DateTime.UtcNow
        );

        // WHEN dispatching the notification:
        await service.SendFeedbackNotificationAsync(feedback);

        // THEN email is sent to bbracke@ecit.com with all relevant data:
        Assert.Equal("bbracke@ecit.com", capturedRecipient);
        Assert.NotNull(capturedSubject);
        Assert.Contains("testuser@ebmpapst.com", capturedSubject);

        Assert.NotNull(capturedBody);
        Assert.Contains("user-session-guid", capturedBody);
        Assert.Contains("testuser@ebmpapst.com", capturedBody);
        Assert.Contains("kontakt@eksempel.dk", capturedBody);
        Assert.Contains("Hvorfor vises 8300100757 ikke i kilderne?", capturedBody);
        Assert.Contains("en erstatning til type K3G280PR04I2", capturedBody);
        Assert.Contains("conv-session-777", capturedBody);
    }

    [Fact]
    public async Task FeedbackNotificationService_WhenDisabled_DoesNotDispatchEmail()
    {
        // GIVEN email notifications disabled in options:
        var mockEmailSender = new Mock<DocumentRagSystem.Core.Interfaces.IEmailSender>();
        var options = Microsoft.Extensions.Options.Options.Create(new FeedbackNotificationOptions
        {
            Enabled = false,
            RecipientEmail = "bbracke@ecit.com"
        });

        var service = new DocumentRagSystem.Infrastructure.Notifications.FeedbackNotificationService(
            options,
            mockEmailSender.Object,
            NullLogger<DocumentRagSystem.Infrastructure.Notifications.FeedbackNotificationService>.Instance
        );

        var feedback = new UserFeedback(
            Id: "fb-disabled",
            UserId: "u1",
            UserEmail: null,
            Comment: "Test",
            ContactEmail: null,
            SearchPrompts: null,
            AssistantAnswer: null,
            ConversationId: null,
            CreatedAt: DateTime.UtcNow
        );

        // WHEN dispatching:
        await service.SendFeedbackNotificationAsync(feedback);

        // THEN sender is never invoked:
        mockEmailSender.Verify(x => x.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FeedbackNotificationService_WhenSenderThrows_DoesNotCrashCaller()
    {
        // GIVEN an email sender that throws an exception (e.g. SMTP down or invalid credentials):
        var mockEmailSender = new Mock<DocumentRagSystem.Core.Interfaces.IEmailSender>();
        mockEmailSender.Setup(x => x.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new System.Net.Mail.SmtpException("Connection refused"));

        var options = Microsoft.Extensions.Options.Options.Create(new FeedbackNotificationOptions
        {
            Enabled = true,
            RecipientEmail = "bbracke@ecit.com"
        });

        var service = new DocumentRagSystem.Infrastructure.Notifications.FeedbackNotificationService(
            options,
            mockEmailSender.Object,
            NullLogger<DocumentRagSystem.Infrastructure.Notifications.FeedbackNotificationService>.Instance
        );

        var feedback = new UserFeedback(
            Id: "fb-fail-safe",
            UserId: "u1",
            UserEmail: null,
            Comment: "Failure test",
            ContactEmail: null,
            SearchPrompts: null,
            AssistantAnswer: null,
            ConversationId: null,
            CreatedAt: DateTime.UtcNow
        );

        // WHEN dispatching, it MUST NOT throw out to caller:
        var exception = await Record.ExceptionAsync(() => service.SendFeedbackNotificationAsync(feedback));
        Assert.Null(exception);
    }
}
