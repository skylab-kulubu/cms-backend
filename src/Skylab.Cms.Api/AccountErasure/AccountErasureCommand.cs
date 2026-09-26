using System.Text.Json;

namespace Skylab.Cms.Api.AccountErasure;

/// <summary>
/// The Erasure command body (spec §2.2). CMS keys nothing by e-mail, so the
/// addresses are validated and then dropped; they are never stored or logged.
/// </summary>
internal sealed record AccountErasureCommand(Guid RequestId, string SubjectId)
{
    public const int MaxBodyBytes = 4096;
    public const int MaxEmails = 3;
    public const int MaxEmailLength = 254;

    /// <summary>Reads and validates the body; null means <c>400 invalid_erasure_command</c>.</summary>
    public static async Task<AccountErasureCommand?> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.ContentLength > MaxBodyBytes)
            return null;

        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                break;
            length += read;
        }

        return length > MaxBodyBytes ? null : Parse(buffer.AsMemory(0, length));
    }

    internal static AccountErasureCommand? Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AccountErasureCommand? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        string? requestId = null;
        string? subjectId = null;
        var hasEmails = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return null;

            switch (property.Name)
            {
                case "request_id" when property.Value.ValueKind == JsonValueKind.String:
                    requestId = property.Value.GetString();
                    break;
                case "subject_id" when property.Value.ValueKind == JsonValueKind.String:
                    subjectId = property.Value.GetString();
                    break;
                case "emails" when AreValidEmails(property.Value):
                    hasEmails = true;
                    break;
                default:
                    return null;
            }
        }

        if (!hasEmails ||
            !Guid.TryParseExact(requestId, "D", out var parsedRequestId) ||
            !IsCanonicalUuid(subjectId))
        {
            return null;
        }

        return new AccountErasureCommand(parsedRequestId, subjectId!);
    }

    private static bool AreValidEmails(JsonElement emails)
    {
        if (emails.ValueKind != JsonValueKind.Array || emails.GetArrayLength() > MaxEmails)
            return false;

        foreach (var email in emails.EnumerateArray())
        {
            if (email.ValueKind != JsonValueKind.String ||
                email.GetString() is not { Length: > 0 and <= MaxEmailLength })
            {
                return false;
            }
        }

        return true;
    }

    // Keycloak subjects are lower-case hyphenated UUIDs, and actor columns and
    // draft keys store them verbatim.
    private static bool IsCanonicalUuid(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);
}
