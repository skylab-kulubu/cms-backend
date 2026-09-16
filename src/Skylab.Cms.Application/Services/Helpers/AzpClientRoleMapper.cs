using System.Security.Claims;
using System.Text.Json;

namespace Skylab.Cms.Application.Services.Helpers;

public static class AzpClientRoleMapper
{
    public static IReadOnlyList<string> RolesForAzp(string? azp, string? resourceAccessJson)
    {
        if (string.IsNullOrWhiteSpace(azp) || string.IsNullOrWhiteSpace(resourceAccessJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(resourceAccessJson);
            if (!doc.RootElement.TryGetProperty(azp, out var clientAccess))
                return [];
            if (!clientAccess.TryGetProperty("roles", out var clientRoles) ||
                clientRoles.ValueKind != JsonValueKind.Array)
                return [];

            var roles = new List<string>();
            foreach (var role in clientRoles.EnumerateArray())
            {
                var value = role.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    roles.Add(value);
            }

            return roles;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void AddTo(ClaimsIdentity identity, ClaimsPrincipal principal)
    {
        var azp = principal.FindFirst("azp")?.Value;
        var resourceAccessJson = principal.FindFirst("resource_access")?.Value;
        foreach (var role in RolesForAzp(azp, resourceAccessJson))
            identity.AddClaim(new Claim(identity.RoleClaimType, role));
    }
}
