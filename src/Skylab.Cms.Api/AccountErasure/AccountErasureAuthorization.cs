using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Skylab.Cms.Application.Services.Helpers;

namespace Skylab.Cms.Api.AccountErasure;

/// <summary>
/// Only core's erasure client, holding the CMS erase role on the skycms
/// resource client, may call the erase route (spec §2.5). JwtBearer has
/// already checked signature, issuer, lifetime and <c>aud</c> = skycms.
/// </summary>
public static class AccountErasureAuthorization
{
    public const string PolicyName = "AccountErasure";
    public const string CallerClientId = "core-erasure";
    public const string ResourceClientId = "skycms";
    public const string EraseRole = "cms:account:erase";

    public static AuthorizationBuilder AddAccountErasurePolicy(this AuthorizationBuilder builder)
    {
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AccountErasureAuthorizationResultHandler>();
        return builder.AddPolicy(PolicyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("azp", CallerClientId);
            policy.RequireAssertion(context => HasEraseRole(context.User));
        });
    }

    // The role is read from resource_access.skycms, never from the "roles"
    // claims AzpClientRoleMapper derives from resource_access[azp].
    internal static bool HasEraseRole(ClaimsPrincipal user) =>
        user.FindAll("resource_access").Any(claim =>
            AzpClientRoleMapper.RolesForClient(ResourceClientId, claim.Value)
                .Contains(EraseRole, StringComparer.Ordinal));
}

/// <summary>Marks the erase route so a forbidden caller gets its problem body.</summary>
public sealed class AccountErasureEndpointMetadata
{
    public static AccountErasureEndpointMetadata Instance { get; } = new();

    private AccountErasureEndpointMetadata()
    {
    }
}

internal sealed class AccountErasureAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden &&
            context.GetEndpoint()?.Metadata.GetMetadata<AccountErasureEndpointMetadata>() is not null)
        {
            await AccountErasureProblems.Forbidden().ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
