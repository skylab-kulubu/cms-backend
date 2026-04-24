namespace Skylab.Cms.Application.Contracts.Responses;

public sealed record SyncResultResponse(
    int Created,
    int Deleted,
    int Unchanged
);