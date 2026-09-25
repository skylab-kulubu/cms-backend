using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Skylab.Cms.Infrastructure.AccountAccess;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Skylab.Cms.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AccountErasureCollection : ICollectionFixture<AccountErasureFixture>
{
    public const string Name = "cms-account-erasure";
}

// Real Postgres and Redis behind the real Program: migrations, JwtBearer, the
// account-access gate and the draft cache are the production wiring. Only the
// token signing key is swapped for a local one.
public sealed class AccountErasureFixture : IAsyncLifetime
{
    public const string RedisPassword = "cms-erasure-test-secret";
    public const int GateDatabase = 13;
    public const int DraftDatabase = 12;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("skylab_cms")
        .WithUsername("skylab_cms")
        .WithPassword("cms-erasure-test-db")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4.2-alpine")
        .WithCommand("redis-server", "--requirepass", RedisPassword, "--appendonly", "no")
        .Build();

    private readonly RSA _signingRsa = RSA.Create(2048);

    public CapturingLoggerProvider Logs { get; } = new();
    public IConnectionMultiplexer Redis { get; private set; } = null!;
    public CmsApiFactory Factory { get; private set; } = null!;
    public string PostgresConnectionString => _postgres.GetConnectionString();
    public string RedisEndpoint => _redis.GetConnectionString();

    public string DraftRedisConnectionString =>
        $"{RedisEndpoint},user=default,password={RedisPassword},defaultDatabase={DraftDatabase}";

    public IDatabase GateDb => Redis.GetDatabase(GateDatabase);
    public IDatabase DraftDb => Redis.GetDatabase(DraftDatabase);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        Redis = await ConnectionMultiplexer.ConnectAsync(
            $"{RedisEndpoint},user=default,password={RedisPassword},allowAdmin=true");
        Factory = CreateFactory();
        _ = Factory.Server;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Redis.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
        _signingRsa.Dispose();
    }

    public CmsApiFactory CreateFactory(
        string gateMode = "enforce",
        string? gateEndpoint = null,
        string? draftRedis = null) =>
        new(this, gateMode, gateEndpoint ?? RedisEndpoint, draftRedis ?? DraftRedisConnectionString);

    public async Task ResetAsync()
    {
        await using (var connection = new NpgsqlConnection(PostgresConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "TRUNCATE collection_items, content_blocks, account_erasure_receipts",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        await GateDb.ExecuteAsync("FLUSHDB");
        await DraftDb.ExecuteAsync("FLUSHDB");
        await GateDb.StringSetAsync(
            AccountAccessGateContract.ContractKey,
            AccountAccessGateContract.ContractValue);
        Logs.Clear();
    }

    public string CreateToken(
        string azp,
        IDictionary<string, object>? resourceAccess,
        string subject,
        string audience = "skycms")
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["azp"] = azp
        };
        if (resourceAccess is not null)
            claims["resource_access"] = resourceAccess;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = AccountAccessGateContract.ExactIssuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private RsaSecurityKey SigningKey => new(_signingRsa) { KeyId = "cms-erasure-test-key" };

    internal RsaSecurityKey ValidationKey =>
        new(_signingRsa.ExportParameters(false)) { KeyId = "cms-erasure-test-key" };
}

public sealed class CmsApiFactory(
    AccountErasureFixture fixture,
    string gateMode,
    string gateEndpoint,
    string draftRedis) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", fixture.PostgresConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", draftRedis);
        builder.UseSetting("Keycloak:Authority", AccountAccessGateContract.ExactIssuer);
        builder.UseSetting("Keycloak:Audience", "skycms");
        builder.UseSetting("AccountAccessGate:Mode", gateMode);
        builder.UseSetting("AccountAccessGate:Endpoint", gateEndpoint);
        builder.UseSetting("AccountAccessGate:Username", "default");
        builder.UseSetting("AccountAccessGate:Password", AccountErasureFixture.RedisPassword);
        builder.UseSetting("AccountAccessGate:Database", AccountErasureFixture.GateDatabase.ToString());
        builder.UseSetting("AccountAccessGate:Tls", "false");
        builder.UseSetting("AccountAccessGate:OperationTimeoutMilliseconds", "200");
        builder.UseSetting("AccountAccessGate:RetryAfterSeconds", "1");

        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(fixture.Logs);
            logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
        });

        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = AccountAccessGateContract.ExactIssuer
                    };
                    configuration.SigningKeys.Add(fixture.ValidationKey);
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                }));
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            entries.Enqueue($"{category} scope {Describe(state)}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(
                $"{logLevel} {category} {formatter(state, exception)} {Describe(state)} {exception}");
        }

        private static string Describe(object? state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(";", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : state?.ToString() ?? string.Empty;
    }
}
