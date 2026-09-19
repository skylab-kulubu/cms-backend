using Microsoft.EntityFrameworkCore;
using Skylab.Cms.Domain.Entities;
using Skylab.Cms.Infrastructure.Storage;

namespace Skylab.Cms.Application.Tests;

public sealed class LifecycleQueryFilterTests
{
    [Fact]
    public void DurableEntities_HaveDefaultArchiveFilters_AndUnconditionalUniqueKeys()
    {
        var options = new DbContextOptionsBuilder<CmsDbContext>()
            .UseNpgsql("Host=localhost;Database=contract;Username=contract;Password=contract")
            .Options;

        using var context = new CmsDbContext(options);
        var collection = context.Model.FindEntityType(typeof(CollectionItem))!;
        var content = context.Model.FindEntityType(typeof(ContentBlock))!;

        Assert.Contains("IsArchived", collection.GetQueryFilter()!.ToString());
        Assert.Contains("IsArchived", content.GetQueryFilter()!.ToString());

        var collectionKey = Assert.Single(collection.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(["CollectionKey", "Slug"]));
        Assert.True(collectionKey.IsUnique);
        Assert.Null(collectionKey.GetFilter());

        var contentKey = Assert.Single(content.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(["ClientId", "Slug", "BlockPath"]));
        Assert.True(contentKey.IsUnique);
        Assert.Null(contentKey.GetFilter());
    }
}
