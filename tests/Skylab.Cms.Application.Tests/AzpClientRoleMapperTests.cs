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
}
