using System;
using System.Text.Json.Serialization;

namespace DocumentRagSystem.WebApi.DTOs;

public record DocumentStatusResponse(
    [property: JsonPropertyName("hasBeenUploaded")] bool HasBeenUploaded,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("documentId")] string? DocumentId = null,
    [property: JsonPropertyName("fileName")] string? FileName = null,
    [property: JsonPropertyName("filePath")] string? FilePath = null,
    [property: JsonPropertyName("uploadedAt")] DateTime? UploadedAt = null,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage = null
)
{
    [JsonPropertyName("isUploaded")]
    public bool IsUploaded => HasBeenUploaded;
}
