using System.Text.Json.Serialization;

namespace Skylab.Cms.Application.Contracts.Responses;

public sealed record AccountErasureResponse(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("completed_at")] DateTime CompletedAt,
    [property: JsonPropertyName("counts")] IReadOnlyDictionary<string, int> Counts)
{
    public const string Completed = "completed";
}
