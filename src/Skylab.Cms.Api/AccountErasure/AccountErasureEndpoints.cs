using Skylab.Cms.Application.Contracts.Services;
using Skylab.Cms.Infrastructure.AccountAccess;

namespace Skylab.Cms.Api.AccountErasure;

/// <summary>
/// <c>PUT /internal/v1/account-erasures/{request_id}</c>: core's Erasure
/// command for CMS (spec §2, ADR-0051). Idempotent on the request id; a
/// repeat returns the stored 200 body.
/// </summary>
public static class AccountErasureEndpoints
{
    public const string Route = "/internal/v1/account-erasures/{requestId}";
    public const string LogCategory = "Skylab.Cms.Api.AccountErasure";

    // Core clamps Retry-After to 30 s .. 15 min (spec §2.4).
    private const int TransientRetryAfterSeconds = 30;
    private const int GateOffRetryAfterSeconds = 300;

    public static IEndpointRouteBuilder MapAccountErasureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut(Route, EraseAsync)
            .RequireAuthorization(AccountErasureAuthorization.PolicyName)
            .WithMetadata(AccountErasureEndpointMetadata.Instance)
            .ExcludeFromDescription();

        return app;
    }

    // Logs carry the request id and a fixed code only: never the body, the
    // subject or an address (spec §2.7).
    private static async Task<IResult> EraseAsync(
        string requestId,
        HttpContext context,
        IAccountAccessGate gate,
        IAccountErasureService erasure,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);
        if (!Guid.TryParseExact(requestId, "D", out var id))
            return AccountErasureProblems.InvalidCommand();

        var command = await AccountErasureCommand.ReadAsync(context.Request, cancellationToken);
        if (command is null || command.RequestId != id)
        {
            LogRejected(logger, id, AccountErasureProblems.InvalidCommandCode);
            return AccountErasureProblems.InvalidCommand();
        }

        var completed = await erasure.FindCompletedAsync(id, cancellationToken);
        if (completed is not null)
            return Results.Ok(completed);

        // A compromised caller must not erase someone who never asked: the
        // subject has to be blocked in account-access Redis already.
        if (gate.Mode == AccountAccessGateMode.Off)
        {
            LogRejected(logger, id, AccountErasureProblems.BlockUnverifiableCode);
            return AccountErasureProblems.Unavailable(
                context,
                AccountErasureProblems.BlockUnverifiableCode,
                GateOffRetryAfterSeconds);
        }

        switch (await gate.CheckSubjectAsync(command.SubjectId, cancellationToken))
        {
            case AccountAccessDecision.Blocked:
                break;
            case AccountAccessDecision.Allowed:
                LogRejected(logger, id, AccountErasureProblems.SubjectNotBlockedCode);
                return AccountErasureProblems.SubjectNotBlocked();
            default:
                LogRejected(logger, id, AccountErasureProblems.BlockUnverifiableCode);
                return AccountErasureProblems.Unavailable(
                    context,
                    AccountErasureProblems.BlockUnverifiableCode,
                    TransientRetryAfterSeconds);
        }

        try
        {
            var result = await erasure.EraseAsync(id, command.SubjectId, cancellationToken);
            logger.LogInformation(
                "Account erasure {RequestId} completed; counts {Counts}",
                id,
                string.Join(',', result.Counts.Select(pair => $"{pair.Key}={pair.Value}")));
            return Results.Ok(result);
        }
        catch (AccountErasureUnavailableException)
        {
            LogRejected(logger, id, AccountErasureProblems.StoreUnavailableCode);
            return AccountErasureProblems.Unavailable(
                context,
                AccountErasureProblems.StoreUnavailableCode,
                TransientRetryAfterSeconds);
        }
    }

    private static void LogRejected(ILogger logger, Guid requestId, string code) =>
        logger.LogWarning("Account erasure {RequestId} not done: {Code}", requestId, code);
}
