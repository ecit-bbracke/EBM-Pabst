using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class FrontendContractTests
{
    private static string GetWwwrootFilePath(string fileName)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var wwwrootPath = Path.Combine(current, "src", "DocumentRagSystem.WebApi", "wwwroot", fileName);
            if (File.Exists(wwwrootPath)) return wwwrootPath;
            current = Path.GetDirectoryName(current);
        }
        throw new FileNotFoundException($"Frontend file not found: {fileName}");
    }

    [Fact]
    public void IndexHtml_ContainsRequiredInteractiveElements_AndPromptTextarea()
    {
        // Arrange
        var filePath = GetWwwrootFilePath("index.html");
        var html = File.ReadAllText(filePath);

        // Assert DOM element contracts
        html.Should().Contain("id=\"query-input\"", "Main prompt input textarea must exist.");
        html.Should().Contain("id=\"submit-btn\"", "Submit button must exist.");
        html.Should().Contain("id=\"chat-conversation\"", "Chat log container must exist.");
        html.Should().Contain("onclick=\"startNewChat()\"", "'Ny chat' buttons must call startNewChat().");
    }

    [Fact]
    public void IndexHtml_StartNewChat_EnsuresPromptInputFocuses()
    {
        // Arrange
        var filePath = GetWwwrootFilePath("index.html");
        var html = File.ReadAllText(filePath);

        // Assert that startNewChat focuses query-input
        html.Should().Contain("function startNewChat");
        var startNewChatBlock = Regex.Match(html, @"function\s+startNewChat[\s\S]*?\{([\s\S]*?)\n\s{8}\}").Value;
        startNewChatBlock.Should().Contain("queryInput.focus()", "startNewChat must focus query-input for immediate typing.");
    }

    [Fact]
    public void IndexHtml_ContainsCleanLatexMath_AndRequestAnimationFrameBatching()
    {
        // Arrange
        var filePath = GetWwwrootFilePath("index.html");
        var html = File.ReadAllText(filePath);

        // Assert LaTeX Math and RAF streaming contracts
        html.Should().Contain("function cleanLatexMath", "Frontend must include client-side cleanLatexMath function.");
        html.Should().Contain("requestAnimationFrame", "Streaming chunks must be batched via requestAnimationFrame for smooth 60fps rendering.");
        html.Should().Contain("active-streaming-body", "Streaming tokens must directly update active-streaming-body DOM element.");
        html.Should().Contain("active-streaming-citations", "Citations must directly update active-streaming-citations DOM element.");
        html.Should().Contain("active-streaming-status", "Progressive loading feedback must be provided to the user.");
    }

    [Fact]
    public void DocumentsHtml_ContainsFileFilterDropdown_AndSearchInput()
    {
        // Arrange
        var filePath = GetWwwrootFilePath("documents.html");
        var html = File.ReadAllText(filePath);

        // Assert documents page contracts
        html.Should().Contain("id=\"doc-file-filter\"", "Documents page must have file presence filter dropdown.");
        html.Should().Contain("value=\"existing\"", "Dropdown must have 'existing' option.");
        html.Should().Contain("value=\"missing\"", "Dropdown must have 'missing' option.");
        html.Should().Contain("id=\"doc-search\"", "Documents page must have document search input.");
    }

    [Fact]
    public void IndexHtml_LoadConversation_SkipsFetchActiveState_ForEmptyConversations()
    {
        // Arrange
        var filePath = GetWwwrootFilePath("index.html");
        var html = File.ReadAllText(filePath);

        // Assert that loadConversation guards against fetching active state when messages are empty
        html.Should().Contain("function loadConversation");
        var loadConversationBlock = Regex.Match(html, @"function\s+loadConversation[\s\S]*?\{([\s\S]*?)\n\s{8}\}").Value;
        loadConversationBlock.Should().Contain("chat.messages.length > 0", "loadConversation must only call fetchActiveState if chat has messages.");
        loadConversationBlock.Should().Contain("active-state-panel", "loadConversation must hide active-state-panel if chat is empty.");

        // Assert fetchActiveState also guards against empty conversations
        html.Should().Contain("async function fetchActiveState");
        html.Should().Contain("!chat.messages || chat.messages.length === 0", "fetchActiveState must skip network fetch for empty conversations.");
    }

    [Fact]
    public void IndexHtml_RenderSidebar_VerticallyCentersChatHistoryElements()
    {
        // Arrange
        var filePath = GetWwwrootFilePath("index.html");
        var html = File.ReadAllText(filePath);

        // Assert that renderSidebar applies vertical centering to chat history items
        html.Should().Contain("function renderSidebar");
        html.Should().Contain("chat-history-item group flex items-center justify-between",
            "Chat history item container must use items-center for vertical alignment.");
        html.Should().Contain("flex items-center space-x-2.5 min-w-0 flex-1 pr-1.5",
            "Speech bubble and title text wrapper must use items-center for vertical alignment.");
        html.Should().Contain("fa-regular fa-comment text-slate-400 group-hover:text-slate-600 text-xs shrink-0",
            "Speech bubble icon must not have top margin offset.");
    }
}
