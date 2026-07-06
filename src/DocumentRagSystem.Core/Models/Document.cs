namespace DocumentRagSystem.Core.Models;

public enum DocumentStatus
{
    Pending,
    Processing,
    Processed,
    Failed
}

public record Document(
    string Id,
    string FileName,
    string FilePath,
    DateTime UploadedAt,
    DocumentStatus Status = DocumentStatus.Pending,
    string? ErrorMessage = null
);

public record DocumentChunk(
    string Id,
    string DocumentId,
    string Text,
    int Index
);
