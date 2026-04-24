using System.Text.Json.Nodes;

namespace Skylab.Cms.Application.Contracts.Requests;

public sealed record UpdateBlockItem(
    string BlockPath,
    JsonNode Value,
    int Version
);