using System.Security.Claims;
using System.Text.Json.Nodes;

namespace Skylab.Cms.Application.Services.Helpers;

public static class GroupPathParser
{
    private static readonly string[] LeaderSubgroups = ["LIDERLER", "KOORDINATORLER"];
    private static readonly string[] PrivilegedGroups = ["ADMIN", "YK", "DK"];

    public static IEnumerable<string> GroupsOf(ClaimsPrincipal user)
    {
        foreach (var claim in user.FindAll("groups"))
        {
            foreach (var path in Expand(claim.Value))
                yield return path;
        }
    }

    public static bool IsPrivileged(ClaimsPrincipal user)
    {
        foreach (var group in GroupsOf(user))
        {
            foreach (var privileged in PrivilegedGroups)
            {
                if (group.EndsWith("/" + privileged, StringComparison.Ordinal) ||
                    group.Contains("/" + privileged + "/", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    public static IReadOnlySet<string> GetLeaderTeamSlugs(ClaimsPrincipal user)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in GroupsOf(user))
        {
            var slug = LeaderTeamSlug(group);
            if (slug is not null)
                slugs.Add(slug);
        }

        return slugs;
    }

    public static string? LeaderTeamSlug(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var last = parts[^1];
        var isLeader = false;
        foreach (var subgroup in LeaderSubgroups)
        {
            if (last.Equals(subgroup, StringComparison.Ordinal))
            {
                isLeader = true;
                break;
            }
        }

        return isLeader ? parts[^2].ToLowerInvariant() : null;
    }

    private static IEnumerable<string> Expand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            JsonArray? array = null;
            try
            {
                array = JsonNode.Parse(trimmed) as JsonArray;
            }
            catch (System.Text.Json.JsonException)
            {
            }

            if (array is not null)
            {
                foreach (var node in array)
                {
                    var value = node?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(value))
                        yield return value;
                }

                yield break;
            }
        }

        yield return trimmed;
    }
}
