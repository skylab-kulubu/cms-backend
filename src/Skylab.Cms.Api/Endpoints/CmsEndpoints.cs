using Skylab.Cms.Application.Contracts.Requests;
using Skylab.Cms.Application.Services;

namespace Skylab.Cms.Api.Endpoints;

public static class CmsEndpoints
{
    private const string SyncedByDeployPipeline = "deploy-pipeline";
    private const string UserSubHeader = "X-User-Sub";

    public static IEndpointRouteBuilder MapCmsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/cms");

        group.MapGet("/content", async (string? slug, IContentService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug))
                return Results.BadRequest("Slug is required.");
            
            var response = await service.GetBySlugAsync(slug, ct);
            return Results.Ok(response);
        });

        group.MapGet("/data", async (string? slug, IContentService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug))
                return Results.BadRequest("Slug is required.");
            
            var response = await service.GetDataBySlugAsync(slug, ct);
            return Results.Ok(response);
        });

        group.MapPut("/content", async (HttpContext context, UpdatePageRequest request, IContentService service, CancellationToken ct) =>
        {
            var updatedBy = context.Request.Headers[UserSubHeader].ToString();
            if (string.IsNullOrWhiteSpace(updatedBy))
                return Results.Unauthorized();

            var response = await service.UpdatePageAsync(request, updatedBy, ct);
            return Results.Ok(response);
        });

        group.MapPost("/sync", async (SyncManifestRequest request, IContentService service, CancellationToken ct) =>
        {
            var response = await service.SyncAsync(request, SyncedByDeployPipeline, ct);
            return Results.Ok(response);
        });

        return app;
    }
}