using Skylab.Cms.Application.Contracts.Responses;

namespace Skylab.Cms.Application.Contracts.Services;

/// <summary>
/// Erases one person's CMS data for one Erasure command (ADR-0051).
/// </summary>
public interface IAccountErasureService
{
    /// <summary>The stored result of a request that already finished, or null.</summary>
    Task<AccountErasureResponse?> FindCompletedAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the subject's drafts, then replaces the subject in every actor
    /// column and writes the receipt in one transaction. A request that
    /// already has a receipt returns it unchanged.
    /// </summary>
    /// <exception cref="AccountErasureUnavailableException">The draft store cannot be reached.</exception>
    Task<AccountErasureResponse> EraseAsync(
        Guid requestId,
        string subjectId,
        CancellationToken cancellationToken = default);
}

public sealed class AccountErasureUnavailableException(Exception innerException)
    : Exception("A store the erasure needs is unavailable.", innerException);
