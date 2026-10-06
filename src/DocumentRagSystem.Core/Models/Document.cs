namespace DocumentRagSystem.Core.Models;

public enum DocumentStatus
{
    Pending,
    Processing,
    Processed,
    Failed,
    Skipped
}

public record Document(
    string Id,
    string FileName,
    string FilePath,
    DateTime UploadedAt,
    DocumentStatus Status = DocumentStatus.Pending,
    string? ErrorMessage = null,
    string? Language = null,
    string? ArticleId = null,
    string? SourceDocumentId = null
);

public record DocumentChunk(
    string Id,
    string DocumentId,
    string Text,
    int Index,
    string? FileName = null,
    string? FilePath = null,
    DateTime? UploadedAt = null,
    string? OriginalFileName = null,
    string? ArticleId = null,
    string? SourceDocumentId = null
);
