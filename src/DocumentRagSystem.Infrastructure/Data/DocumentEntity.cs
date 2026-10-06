using System;

namespace DocumentRagSystem.Infrastructure.Data;

public class DocumentEntity
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public string? Language { get; set; }
    public string? ArticleId { get; set; }
    public string? SourceDocumentId { get; set; }
}
