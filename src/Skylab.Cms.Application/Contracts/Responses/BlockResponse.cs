using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Skylab.Cms.Application.Contracts.Responses;

public sealed record BlockResponse(
    string BlockPath,
    string BlockType,
    JsonNode Value,
    int SortOrder,
    int Version,
    JsonNode? Data,
    JsonNode? DraftValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsArchived = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? ArchivedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArchivedBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Slug = null
);
