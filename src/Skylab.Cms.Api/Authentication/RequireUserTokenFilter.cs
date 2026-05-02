using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Skylab.Cms.Api.Authentication;

public sealed class RequireUserTokenFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        var result = await httpContext.AuthenticateAsync(AuthSchemes.User);
        if (!result.Succeeded || result.Principal?.Identity is not ClaimsIdentity userIdentity)
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(userIdentity.FindFirst("sub")?.Value))
            return Results.Unauthorized();

        httpContext.User.AddIdentity(userIdentity);

        return await next(context);
    }
}

public static class UserPrincipalExtensions
{
    public static string? GetClientId(this ClaimsPrincipal principal) => principal.FindFirst("azp")?.Value;

    public static string? GetUserSub(this ClaimsPrincipal principal) => principal.Identities.FirstOrDefault(i => i.AuthenticationType == AuthSchemes.User)?.FindFirst("sub")?.Value;
}