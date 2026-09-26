namespace Skylab.Cms.Api.AccountErasure;

/// <summary>
/// RFC 7807 answers of the erase route (spec §2.4). Each carries a fixed
/// <c>code</c> and never echoes a value from the request.
/// </summary>
internal static class AccountErasureProblems
{
    public const string InvalidCommandCode = "invalid_erasure_command";
    public const string ForbiddenCode = "erasure_forbidden";
    public const string SubjectNotBlockedCode = "subject_not_blocked";
    public const string BlockUnverifiableCode = "subject_block_unverifiable";
    public const string StoreUnavailableCode = "erasure_store_unavailable";

    public static IResult InvalidCommand() =>
        Problem(StatusCodes.Status400BadRequest, InvalidCommandCode, "Invalid erasure command");

    public static IResult Forbidden() =>
        Problem(StatusCodes.Status403Forbidden, ForbiddenCode, "Erasure forbidden");

    public static IResult SubjectNotBlocked() =>
        Problem(StatusCodes.Status409Conflict, SubjectNotBlockedCode, "Subject is not blocked");

    public static IResult Unavailable(HttpContext context, string code, int retryAfterSeconds)
    {
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        context.Response.Headers.CacheControl = "no-store";
        return Problem(StatusCodes.Status503ServiceUnavailable, code, "Erasure temporarily unavailable");
    }

    private static IResult Problem(int statusCode, string code, string title) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
