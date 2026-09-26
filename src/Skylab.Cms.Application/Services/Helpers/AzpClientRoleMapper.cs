using System.Security.Claims;
using System.Text.Json;

namespace Skylab.Cms.Application.Services.Helpers;

public static class AzpClientRoleMapper
{
    public static IReadOnlyList<string> RolesForAzp(string? azp, string? resourceAccessJson) =>
        RolesForClient(azp, resourceAccessJson);

    /// <summary>Roles under <c>resource_access.{clientId}.roles</c>, whoever the token was issued to.</summary>
    public static IReadOnlyList<string> RolesForClient(string? clientId, string? resourceAccessJson)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(resourceAccessJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(resourceAccessJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(clientId, out var clientAccess))
                return [];
            if (clientAccess.ValueKind != JsonValueKind.Object ||
                !clientAccess.TryGetProperty("roles", out var clientRoles) ||
                clientRoles.ValueKind != JsonValueKind.Array)
                return [];

            var roles = new List<string>();
            foreach (var role in clientRoles.EnumerateArray())
            {
                if (role.ValueKind != JsonValueKind.String)
                    continue;
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
