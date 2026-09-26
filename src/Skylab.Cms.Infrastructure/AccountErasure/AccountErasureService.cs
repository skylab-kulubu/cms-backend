using Microsoft.EntityFrameworkCore;
using Skylab.Cms.Application.Contracts.Responses;
using Skylab.Cms.Application.Contracts.Services;
using Skylab.Cms.Domain;
using Skylab.Cms.Domain.Entities;
using Skylab.Cms.Infrastructure.Cache;
using Skylab.Cms.Infrastructure.Storage;
using StackExchange.Redis;

namespace Skylab.Cms.Infrastructure.AccountErasure;

/// <summary>
/// CMS scope of an Erasure command (spec §3.2): actor subjects become
/// Silinmiş kullanıcı and drafts are deleted. Published News author and body
/// are editorial record and stay (ADR-0051, decision 5).
/// </summary>
internal sealed class AccountErasureService(
    CmsDbContext db,
    RedisDraftEraser drafts) : IAccountErasureService
{
    public const string ActorColumnsReplaced = "actor_columns_replaced";
    public const string DraftsDeleted = "drafts_deleted";

    public async Task<AccountErasureResponse?> FindCompletedAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var receipt = await db.AccountErasureReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);
        return receipt is null ? null : ToResponse(receipt);
    }

    public async Task<AccountErasureResponse> EraseAsync(
        Guid requestId,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        // Redis cannot join the transaction, so drafts go first: a committed
        // receipt then implies they are gone. Repeating it deletes nothing.
        int draftsDeleted;
        try
        {
            draftsDeleted = await drafts.DeleteSubjectDraftsAsync(subjectId, cancellationToken);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            throw new AccountErasureUnavailableException(exception);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = $"cms:account-erasure:{requestId:D}";
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);

        var existing = await db.AccountErasureReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResponse(existing);
        }

        // Bulk updates leave UpdatedAt and Version alone and, with the
        // query filters ignored, reach archived rows too.
        var replaced =
            await db.CollectionItems.IgnoreQueryFilters()
                .Where(x => x.UpdatedBy == subjectId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.UpdatedBy, DeletedUser.Subject), cancellationToken) +
            await db.CollectionItems.IgnoreQueryFilters()
                .Where(x => x.ArchivedBy == subjectId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ArchivedBy, DeletedUser.Subject), cancellationToken) +
            await db.ContentBlocks.IgnoreQueryFilters()
                .Where(x => x.UpdatedBy == subjectId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.UpdatedBy, DeletedUser.Subject), cancellationToken) +
            await db.ContentBlocks.IgnoreQueryFilters()
                .Where(x => x.ArchivedBy == subjectId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ArchivedBy, DeletedUser.Subject), cancellationToken);

        var receipt = AccountErasureReceipt.Create(
            requestId,
            DateTime.UtcNow,
            new Dictionary<string, int>
            {
                [ActorColumnsReplaced] = replaced,
                [DraftsDeleted] = draftsDeleted
            });
        db.AccountErasureReceipts.Add(receipt);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ToResponse(receipt);
    }

    private static AccountErasureResponse ToResponse(AccountErasureReceipt receipt) =>
        new(
            receipt.RequestId,
            AccountErasureResponse.Completed,
            receipt.CompletedAt,
            receipt.CountsByName());
}
