using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Skylab.Cms.Api.AccountAccess;
using Skylab.Cms.Infrastructure.AccountAccess;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Skylab.Cms.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AccountAccessGateIntegrationCollection :
    ICollectionFixture<AccountAccessRedisFixture>
{
    public const string Name = "cms-account-access-gate-redis";
}

public sealed class AccountAccessRedisFixture : IAsyncLifetime
{
    public const string Password = "cms-access-gate-test-secret";
    public const int GateDatabase = 13;
    public const int CacheDatabase = 12;

    private readonly RedisContainer _container = new RedisBuilder("redis:7.4.2-alpine")
        .WithCommand("redis-server", "--requirepass", Password, "--appendonly", "no")
        .Build();

    public IConnectionMultiplexer Writer { get; private set; } = null!;
    public string Endpoint => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Writer = await ConnectionMultiplexer.ConnectAsync(
            $"{Endpoint},user=default,password={Password},defaultDatabase={GateDatabase},allowAdmin=true");
    }

    public async Task DisposeAsync()
    {
        await Writer.DisposeAsync();
        await _container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        await Writer.GetDatabase(GateDatabase).ExecuteAsync("FLUSHDB");
        await Writer.GetDatabase(CacheDatabase).ExecuteAsync("FLUSHDB");
    }
}

[Collection(AccountAccessGateIntegrationCollection.Name)]
public sealed class AccountAccessGateIntegrationTests : IAsyncLifetime
{
    private const string ProtectedRoute = "/cms/content";
    private const string PublicSchemaRoute = "/cms/collections/Teams/schema";
    private const string PublicListRoute = "/cms/collections/Teams";
    private const string PublicItemRoute = "/cms/collections/Teams/example";

    private readonly AccountAccessRedisFixture _redis;
    private readonly RSA _signingRsa = RSA.Create(2048);
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private SecurityKey _signingKey = null!;
    private int _sideEffects;

    public AccountAccessGateIntegrationTests(AccountAccessRedisFixture redis)
    {
        _redis = redis;
    }

    public async Task InitializeAsync()
    {
        await _redis.ResetAsync();
        (_app, _client, _signingKey) = await StartAppAsync(_redis.Endpoint);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _signingRsa.Dispose();
    }

    [Fact]
    public async Task Allowed_CmsAccess_principal_reaches_protected_and_all_public_editor_routes()
    {
        await WriteContractAsync();

        var protectedResponse = await SendAuthenticatedAsync(HttpMethod.Get, ProtectedRoute);
        var schemaResponse = await SendAuthenticatedAsync(HttpMethod.Get, PublicSchemaRoute);
        var listResponse = await SendAuthenticatedAsync(HttpMethod.Get, PublicListRoute);
        var itemResponse = await SendAuthenticatedAsync(HttpMethod.Get, PublicItemRoute);

        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        Assert.Equal(4, _sideEffects);
    }

    [Theory]
    [InlineData(ProtectedRoute)]
    [InlineData(PublicSchemaRoute)]
    [InlineData(PublicListRoute)]
    [InlineData(PublicItemRoute)]
    public async Task Blocked_principal_gets_generic_401_before_any_route_side_effect(string route)
    {
        await WriteContractAsync();
        await GateDatabase.StringSetAsync(
            AccountAccessGateContract.MarkerKey(AccountAccessGateContract.GoldenSubject),
            AccountAccessGateContract.MarkerValue);

        var response = await SendAuthenticatedAsync(HttpMethod.Get, route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, _sideEffects);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-contract")]
    public async Task Missing_or_wrong_contract_returns_503_before_CmsAccess_handler(
        string? contractValue)
    {
        if (contractValue is not null)
        {
            await GateDatabase.StringSetAsync(
                AccountAccessGateContract.ContractKey,
                contractValue);
        }

        var response = await SendAuthenticatedAsync(HttpMethod.Get, ProtectedRoute);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("1", response.Headers.RetryAfter?.ToString());
        Assert.Equal(0, _sideEffects);
    }

    [Fact]
    public async Task Malformed_marker_returns_503_before_authenticated_public_handler()
    {
        await WriteContractAsync();
        await GateDatabase.StringSetAsync(
            AccountAccessGateContract.MarkerKey(AccountAccessGateContract.GoldenSubject),
            "true");

        var response = await SendAuthenticatedAsync(HttpMethod.Get, PublicSchemaRoute);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(0, _sideEffects);
    }

    [Fact]
    public async Task Draft_cache_success_does_not_mask_access_gate_Redis_outage()
    {
        var (outageApp, outageClient, _) = await StartAppAsync("127.0.0.1:1");
        try
        {
            var draftCache = outageApp.Services.GetRequiredService<IDistributedCache>();
            await draftCache.SetStringAsync("draft:test", "available");
            Assert.Equal("available", await draftCache.GetStringAsync("draft:test"));

            using var request = AuthenticatedRequest(HttpMethod.Get, ProtectedRoute);
            var response = await outageClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(0, _sideEffects);
        }
        finally
        {
            outageClient.Dispose();
            await outageApp.DisposeAsync();
        }
    }

    [Fact]
    public async Task Anonymous_public_collection_schema_list_and_item_skip_an_unavailable_gate()
    {
        var (outageApp, outageClient, _) = await StartAppAsync("127.0.0.1:1");
        try
        {
            var schemaResponse = await outageClient.GetAsync(PublicSchemaRoute);
            var listResponse = await outageClient.GetAsync(PublicListRoute);
            var itemResponse = await outageClient.GetAsync(PublicItemRoute);

            Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
            Assert.Equal(3, _sideEffects);
        }
        finally
        {
            outageClient.Dispose();
            await outageApp.DisposeAsync();
        }
    }

    [Fact]
    public async Task Malformed_Bearer_cannot_downgrade_to_anonymous_collection_read()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PublicSchemaRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "forged.token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _sideEffects);
    }

    [Fact]
    public async Task Authenticated_service_without_CmsAccess_is_gated_before_authorization()
    {
        using var request = AuthenticatedRequest(
            HttpMethod.Get,
            ProtectedRoute,
            includeCmsAccessRole: false);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, _sideEffects);
    }

    [Fact]
    public async Task Liveness_is_process_only_and_readiness_checks_the_exact_contract()
    {
        var liveWithoutContract = await _client.GetAsync("/health/live");
        var authenticatedLiveWithoutContract = await SendAuthenticatedAsync(
            HttpMethod.Get,
            "/health/live");
        var notReady = await _client.GetAsync("/health/ready");

        await WriteContractAsync();
        await GateDatabase.StringSetAsync(
            AccountAccessGateContract.MarkerKey(AccountAccessGateContract.GoldenSubject),
            AccountAccessGateContract.MarkerValue);

        var ready = await _client.GetAsync("/health/ready");
        var authenticatedReadyForBlockedSubject = await SendAuthenticatedAsync(
            HttpMethod.Get,
            "/health/ready");

        Assert.Equal(HttpStatusCode.OK, liveWithoutContract.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticatedLiveWithoutContract.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticatedReadyForBlockedSubject.StatusCode);
        Assert.Equal(0, _sideEffects);
    }

    private IDatabase GateDatabase =>
        _redis.Writer.GetDatabase(AccountAccessRedisFixture.GateDatabase);

    private Task WriteContractAsync() => GateDatabase.StringSetAsync(
        AccountAccessGateContract.ContractKey,
        AccountAccessGateContract.ContractValue);

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpMethod method, string path)
    {
        using var request = AuthenticatedRequest(method, path);
        return await _client.SendAsync(request);
    }

    private HttpRequestMessage AuthenticatedRequest(
        HttpMethod method,
        string path,
        bool includeCmsAccessRole = true)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(includeCmsAccessRole));
        return request;
    }

    private async Task<(WebApplication App, HttpClient Client, SecurityKey SigningKey)> StartAppAsync(
        string gateEndpoint)
    {
        var signingKey = new RsaSecurityKey(_signingRsa) { KeyId = "cms-access-gate-test-key" };
        var validationKey = new RsaSecurityKey(_signingRsa.ExportParameters(false))
        {
            KeyId = signingKey.KeyId
        };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AccountAccessGateMiddleware).Assembly.GetName().Name,
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Keycloak:Authority"] = AccountAccessGateContract.ExactIssuer,
            ["Keycloak:Audience"] = "skycms",
            ["AccountAccessGate:Mode"] = "enforce",
            ["AccountAccessGate:Endpoint"] = gateEndpoint,
            ["AccountAccessGate:Username"] = "default",
            ["AccountAccessGate:Password"] = AccountAccessRedisFixture.Password,
            ["AccountAccessGate:Database"] = AccountAccessRedisFixture.GateDatabase.ToString(),
            ["AccountAccessGate:Tls"] = "false",
            ["AccountAccessGate:OperationTimeoutMilliseconds"] = "200",
            ["AccountAccessGate:RetryAfterSeconds"] = "1"
        });

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.Authority = AccountAccessGateContract.ExactIssuer;
                options.Audience = "skycms";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = AccountAccessGateContract.ExactIssuer,
                    ValidateAudience = true,
                    ValidAudience = "skycms",
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = validationKey,
                    RoleClaimType = "roles"
                };
                var openIdConfiguration = new OpenIdConnectConfiguration
                {
                    Issuer = AccountAccessGateContract.ExactIssuer
                };
                openIdConfiguration.SigningKeys.Add(validationKey);
                options.ConfigurationManager =
                    new StaticConfigurationManager<OpenIdConnectConfiguration>(openIdConfiguration);
            });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("CmsAccess", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole("cms:access");
            });
        builder.Services.AddAccountAccessGate(builder.Configuration, builder.Environment);
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration =
                $"{_redis.Endpoint},user=default,password={AccountAccessRedisFixture.Password},defaultDatabase={AccountAccessRedisFixture.CacheDatabase}";
        });

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAccountAccessGate();
        app.UseAuthorization();
        app.MapAccountAccessHealthEndpoints();
        app.MapGet(ProtectedRoute, HandleRequest).RequireAuthorization("CmsAccess");
        app.MapGet(PublicSchemaRoute, HandleRequest).AllowAnonymous();
        app.MapGet(PublicListRoute, HandleRequest).AllowAnonymous();
        app.MapGet(PublicItemRoute, HandleRequest).AllowAnonymous();

        await app.StartAsync();
        return (app, app.GetTestClient(), signingKey);
    }

    private IResult HandleRequest()
    {
        Interlocked.Increment(ref _sideEffects);
        return Results.Ok();
    }

    private string CreateToken(bool includeCmsAccessRole)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", AccountAccessGateContract.GoldenSubject)
        };
        if (includeCmsAccessRole)
            claims.Add(new Claim("roles", "cms:access"));

        var token = new JwtSecurityToken(
            issuer: AccountAccessGateContract.ExactIssuer,
            audience: "skycms",
            claims: claims,
            notBefore: now.AddSeconds(-5),
            expires: now.AddMinutes(5),
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
