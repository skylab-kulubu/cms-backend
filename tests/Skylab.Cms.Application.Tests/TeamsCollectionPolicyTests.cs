using Skylab.Cms.Application.Services.Policies;

namespace Skylab.Cms.Application.Tests;

public sealed class TeamsCollectionPolicyTests
{
    private readonly TeamsCollectionPolicy _policy = new();

    [Fact]
    public void WeblabLeader_CanEditWeblab_NotAgc()
    {
        var user = PrincipalFactory.FromGroups("/UYELER/ARGE/WEBLAB/LIDERLER");

        Assert.True(_policy.CanEdit(user, "weblab"));
        Assert.False(_policy.CanEdit(user, "agc"));
        Assert.Equal(["weblab"], _policy.GetVirtualSlugs(user));
    }

    [Fact]
    public void WeblabCoordinator_CanEditWeblab()
    {
        var user = PrincipalFactory.FromGroups("/UYELER/ARGE/WEBLAB/KOORDINATORLER");

        Assert.True(_policy.CanEdit(user, "weblab"));
        Assert.False(_policy.CanEdit(user, "agc"));
    }

    [Fact]
    public void LeaderRoleClaim_DoesNotGrantTeamEdit()
    {
        var user = PrincipalFactory.FromRoles("WEBLAB_LEADER", "AGC_LEADER");

        Assert.False(_policy.CanEdit(user, "weblab"));
        Assert.False(_policy.CanEdit(user, "agc"));
        Assert.Empty(_policy.GetVirtualSlugs(user));
    }

    [Fact]
    public void WeblabMember_CannotEditWeblab()
    {
        var user = PrincipalFactory.FromGroups("/UYELER/ARGE/WEBLAB");

        Assert.False(_policy.CanEdit(user, "weblab"));
    }
}
