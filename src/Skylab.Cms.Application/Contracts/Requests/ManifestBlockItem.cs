using System.Text.Json.Nodes;
using Skylab.Cms.Domain.Enums;

namespace Skylab.Cms.Application.Contracts.Requests;

public sealed record ManifestBlockItem(
    string BlockPath,
    BlockType BlockType,
    JsonNode DefaultValue,
    int SortOrder
);