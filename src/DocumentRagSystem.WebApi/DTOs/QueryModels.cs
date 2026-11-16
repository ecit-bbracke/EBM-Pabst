using System.Collections.Generic;
using System.Text.Json.Serialization;
using FluentValidation;

namespace DocumentRagSystem.WebApi.DTOs;

public record QueryRequest(
    string Question,
    string? ConversationId = null,
    string? Mode = null,
    string? Pages = null,
    bool? Compact = null,
    bool? IncludeBase64 = null
);

public record QueryResponse(string Answer, IEnumerable<CitationDto> Citations, string? ConversationId = null);

public record JsonQueryResponse(
    [property: JsonPropertyName("response")] string Response,
    [property: JsonPropertyName("files")] IReadOnlyList<JsonQueryFileDto> Files,
    [property: JsonPropertyName("conversationId")] string? ConversationId = null
);

public record JsonQueryFileDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl = null,
    [property: JsonPropertyName("pages")] IReadOnlyList<int>? Pages = null,
    [property: JsonPropertyName("originalSizeBytes")] long? OriginalSizeBytes = null,
    [property: JsonPropertyName("deliveredSizeBytes")] long? DeliveredSizeBytes = null
);

public record CitationDto(
    [property: JsonPropertyName("chunkId")] string ChunkId,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("filename")] string filename,
    [property: JsonPropertyName("filepath")] string filepath,
    [property: JsonPropertyName("language")] string? language = null,
    [property: JsonPropertyName("isCompanion")] bool isCompanion = false
);

public class QueryRequestValidator : AbstractValidator<QueryRequest>
{
    public QueryRequestValidator()
    {
        RuleFor(x => x.Question)
            .NotEmpty().WithMessage("Question cannot be empty.")
            .MinimumLength(3).WithMessage("Question must be at least 3 characters long.");
    }
}
