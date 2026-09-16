using System.Text.Json.Nodes;
using Skylab.Cms.Application.Contracts.Repositories;
using Skylab.Cms.Application.Contracts.Requests;
using Skylab.Cms.Application.Contracts.Services;
using Skylab.Cms.Application.Services;
using Skylab.Cms.Domain.Entities;
using Skylab.Cms.Domain.Enums;

namespace Skylab.Cms.Application.Tests;

public sealed class ContentClientIsolationTests
{
    [Fact]
    public async Task GeceKoduLeader_CanWriteOwnClient_NotAnotherClientId()
    {
        var repo = new MemoryContentBlockRepository();
        var now = DateTime.UtcNow;
        var gece = ContentBlock.Create(
            "gecekodu",
            "/home",
            "hero",
            BlockType.Text,
            JsonNode.Parse("""{"text":"old-gece"}""")!,
            0,
            "seed",
            now);
        var agc = ContentBlock.Create(
            "agc",
            "/home",
            "hero",
            BlockType.Text,
            JsonNode.Parse("""{"text":"old-agc"}""")!,
            0,
            "seed",
            now);
        await repo.AddRangeAsync([gece, agc]);
        await repo.SaveChangesAsync();

        var service = new ContentService(repo, new NoopDraftService());
        var request = new UpdatePageRequest(
            "home",
            [new UpdateBlockItem("hero", JsonNode.Parse("""{"text":"new-gece"}""")!, gece.Version)]);

        var result = await service.UpdatePageAsync("gecekodu", request, "gecekodu-leader");

        Assert.Equal(1, result.Updated);
        var geceAfter = (await repo.GetBySlugAsync("gecekodu", "/home")).Single();
        var agcAfter = (await repo.GetBySlugAsync("agc", "/home")).Single();
        Assert.Equal("""{"text":"new-gece"}""", geceAfter.Value.ToJsonString());
        Assert.Equal("""{"text":"old-agc"}""", agcAfter.Value.ToJsonString());
        Assert.Equal("seed", agcAfter.UpdatedBy);
        Assert.Equal("gecekodu-leader", geceAfter.UpdatedBy);
    }

    private sealed class MemoryContentBlockRepository : IContentBlockRepository
    {
        private readonly List<ContentBlock> _blocks = [];

        public Task<IReadOnlyList<ContentBlock>> GetBySlugAsync(
            string clientId,
            string slug,
            bool includeArchived = false,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<ContentBlock> query = _blocks.Where(b => b.ClientId == clientId && b.Slug == slug);
            if (!includeArchived)
                query = query.Where(b => !b.IsArchived);
            return Task.FromResult<IReadOnlyList<ContentBlock>>(query.ToList());
        }

        public Task<IReadOnlyList<ContentBlock>> GetByClientAsync(
            string clientId,
            bool includeArchived = false,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<ContentBlock> query = _blocks.Where(b => b.ClientId == clientId);
            if (!includeArchived)
                query = query.Where(b => !b.IsArchived);
            return Task.FromResult<IReadOnlyList<ContentBlock>>(query.ToList());
        }

        public Task AddRangeAsync(IEnumerable<ContentBlock> blocks, CancellationToken cancellationToken = default)
        {
            _blocks.AddRange(blocks);
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class NoopDraftService : IDraftService
    {
        public Task SaveDraftAsync(
            string clientId,
            string userId,
            string slug,
            IReadOnlyList<DraftBlock> blocks,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DraftBlock>?> GetDraftAsync(
            string clientId,
            string userId,
            string slug,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DraftBlock>?>(null);

        public Task DeleteDraftAsync(
            string clientId,
            string userId,
            string slug,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
