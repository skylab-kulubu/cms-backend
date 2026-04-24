using Skylab.Cms.Application.Contracts.Requests;
using Skylab.Cms.Application.Contracts.Responses;

namespace Skylab.Cms.Application.Services;

public interface IContentService
{
    Task<ContentResponse> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<ContentResponse> GetDataBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<UpdatePageResponse> UpdatePageAsync(UpdatePageRequest request, string updatedBy, CancellationToken cancellationToken = default);

    Task<SyncResultResponse> SyncAsync(SyncManifestRequest request, string syncedBy, CancellationToken cancellationToken = default);
}
