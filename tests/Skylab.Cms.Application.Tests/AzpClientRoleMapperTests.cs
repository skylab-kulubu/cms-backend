using Skylab.Cms.Application.Services.Helpers;

namespace Skylab.Cms.Application.Tests;

public sealed class AzpClientRoleMapperTests
{
    private const string ResourceAccess = """
        {
          "gecekodu": { "roles": ["cms:access"] },
          "agc": { "roles": ["cms:access"] },
          "skycms": { "roles": ["cms:access"] }
        }
        """;

    [Fact]
    public void GeceKoduAzp_ReceivesCmsAccessOnThatClientOnly()
    {
        var roles = AzpClientRoleMapper.RolesForAzp("gecekodu", ResourceAccess);

        Assert.Contains("cms:access", roles);
        Assert.Equal(["cms:access"], roles);
    }

    [Fact]
    public void OtherClientId_DoesNotReceiveGeceKoduCmsAccess()
    {
        var withoutAgcAccess = """
            {
              "gecekodu": { "roles": ["cms:access"] },
              "agc": { "roles": ["view"] }
            }
            """;

        var roles = AzpClientRoleMapper.RolesForAzp("agc", withoutAgcAccess);

        Assert.DoesNotContain("cms:access", roles);
        Assert.Equal(["view"], roles);
    }

    [Fact]
    public void MissingAzp_YieldsNoRoles()
    {
        Assert.Empty(AzpClientRoleMapper.RolesForAzp(null, ResourceAccess));
        Assert.Empty(AzpClientRoleMapper.RolesForAzp("", ResourceAccess));
    }

    [Fact]
    public void RolesForClient_ReadsTheNamedResourceClient_NotTheAzp()
    {
        var erasureToken = """
            {
              "core": { "roles": ["cms:account:erase"] },
              "skycms": { "roles": ["cms:account:erase"] }
            }
            """;

        Assert.Equal(["cms:account:erase"], AzpClientRoleMapper.RolesForClient("skycms", erasureToken));
        Assert.Empty(AzpClientRoleMapper.RolesForClient("core-erasure", erasureToken));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{ "skycms": "cms:account:erase" }""")]
    [InlineData("""{ "skycms": { "roles": "cms:account:erase" } }""")]
    [InlineData("""{ "skycms": { "roles": [1, null, { "role": "cms:account:erase" }] } }""")]
    public void MalformedResourceAccess_YieldsNoRoles(string resourceAccess)
    {
        Assert.Empty(AzpClientRoleMapper.RolesForClient("skycms", resourceAccess));
        Assert.Empty(AzpClientRoleMapper.RolesForAzp("skycms", resourceAccess));
    }
}
