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
    public string? ProductCode { get; set; }
    public string? ProductCategory { get; set; }
    public string? Phase { get; set; }
    public string? NominalVoltage { get; set; }
    public string? VoltageRange { get; set; }
    public int? PowerWatts { get; set; }
    public int? DiameterMm { get; set; }
    public int? MinVoltage { get; set; }
    public int? MaxVoltage { get; set; }
}
