using System.Security.Claims;

namespace Skylab.Cms.Application.Tests;

internal static class PrincipalFactory
{
    public static ClaimsPrincipal FromGroups(params string[] groups)
    {
        var identity = new ClaimsIdentity("test");
        foreach (var group in groups)
            identity.AddClaim(new Claim("groups", group));
        return new ClaimsPrincipal(identity);
    }

    public static ClaimsPrincipal FromRoles(params string[] roles)
    {
        var identity = new ClaimsIdentity("test");
        foreach (var role in roles)
            identity.AddClaim(new Claim(identity.RoleClaimType, role));
        return new ClaimsPrincipal(identity);
    }
}
