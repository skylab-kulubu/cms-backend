namespace Skylab.Cms.Application.Contracts.Responses;

public sealed record UpdatePageResponse(
    int Updated,
    int Unchanged
);