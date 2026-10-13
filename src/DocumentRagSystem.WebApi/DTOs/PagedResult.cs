using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DocumentRagSystem.WebApi.DTOs;

public record PagedResult<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("hasPreviousPage")] bool HasPreviousPage,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage
);
