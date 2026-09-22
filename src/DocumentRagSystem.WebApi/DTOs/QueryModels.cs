using System.Collections.Generic;
using FluentValidation;

namespace DocumentRagSystem.WebApi.DTOs;

public record QueryRequest(string Question, string? ConversationId = null);

public record QueryResponse(string Answer, IEnumerable<CitationDto> Citations, string? ConversationId = null);

public record CitationDto(string ChunkId, string DocumentId, string Text, int Index, string filename, string filepath);

public class QueryRequestValidator : AbstractValidator<QueryRequest>
{
    public QueryRequestValidator()
    {
        RuleFor(x => x.Question)
            .NotEmpty().WithMessage("Question cannot be empty.")
            .MinimumLength(3).WithMessage("Question must be at least 3 characters long.");
    }
}
