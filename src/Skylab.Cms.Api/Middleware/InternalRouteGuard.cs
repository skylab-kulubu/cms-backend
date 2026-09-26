namespace Skylab.Cms.Api.Middleware;

/// <summary>
/// Routes under <c>/internal</c> answer only callers on the Docker network
/// (ADR-0016). Traefik always adds forwarding headers, so a request that
/// carries one came through the public ingress and gets a bare 404, before
/// any token is read. The token check behind it stays the real boundary.
/// </summary>
public static class InternalRouteGuard
{
    public const string PathPrefix = "/internal";

    public static IApplicationBuilder UseInternalRouteGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
                CameThroughIngress(context.Request.Headers))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });

    internal static bool CameThroughIngress(IHeaderDictionary headers) =>
        headers.Keys.Any(name =>
            name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("X-Real-Ip", StringComparison.OrdinalIgnoreCase));
}
