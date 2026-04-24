using Skylab.Cms.Domain.Entities;

namespace Skylab.Cms.Application.Contracts.Repositories;

public interface IContentBlockRepository
{
    Task<IReadOnlyList<ContentBlock>> GetBySlugAsync(string slug, bool includeArchived = false, CancellationToken cancellationToken = default);

    Task AddRangeAsync(IEnumerable<ContentBlock> blocks, CancellationToken cancellationToken = default);

    void ArchiveRange(IEnumerable<ContentBlock> blocks);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}