using System.Collections.Generic;
using System.Text.Json.Serialization;
using FluentValidation;

namespace DocumentRagSystem.WebApi.DTOs;

public record QueryRequest(string Question, string? ConversationId = null);

public record QueryResponse(string Answer, IEnumerable<CitationDto> Citations, string? ConversationId = null);

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
