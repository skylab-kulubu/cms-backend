using Skylab.Cms.Application.Services.Policies;

namespace Skylab.Cms.Application.Tests;

public sealed class NewsCollectionPolicyTests
{
    private readonly NewsCollectionPolicy _policy = new();

    [Theory]
    [InlineData("/UYELER/YK")]
    [InlineData("/UYELER/ADMIN")]
    [InlineData("/UYELER/DK")]
    [InlineData("/UYELER/YK/BASKAN")]
    public void Privileged_CanEditNews(string group)
    {
        var user = PrincipalFactory.FromGroups(group);

        Assert.True(_policy.CanEdit(user, "any-slug"));
        Assert.True(_policy.CanCreate(user));
    }

    [Fact]
    public void Member_CannotEditNews()
    {
        var user = PrincipalFactory.FromGroups("/UYELER");

        Assert.False(_policy.CanEdit(user, "weekly-update"));
        Assert.False(_policy.CanCreate(user));
    }

    [Fact]
    public void WeblabLeader_CannotEditNews()
    {
        var user = PrincipalFactory.FromGroups("/UYELER/ARGE/WEBLAB/LIDERLER");

        Assert.False(_policy.CanEdit(user, "weekly-update"));
        Assert.False(_policy.CanCreate(user));
    }
}
