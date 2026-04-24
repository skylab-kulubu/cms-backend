namespace Skylab.Cms.Application.Contracts.Responses;

public sealed record ContentResponse(
    string Slug,
    IReadOnlyList<BlockResponse> Blocks
);