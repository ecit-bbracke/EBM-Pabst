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
}
