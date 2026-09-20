using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Skylab.Cms.Infrastructure.AccountAccess;
using StackExchange.Redis;

namespace Skylab.Cms.Api.Tests;

public sealed class AccountAccessGateContractTests : IDisposable
{
    private readonly string tlsDirectory;
    private readonly string caCertificateFile;
    private readonly string clientCertificateFile;
    private readonly string clientKeyFile;

    public AccountAccessGateContractTests()
    {
        tlsDirectory = Path.Combine(Path.GetTempPath(), $"cms-access-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tlsDirectory);
        caCertificateFile = Path.Combine(tlsDirectory, "ca.crt");
        clientCertificateFile = Path.Combine(tlsDirectory, "cms.crt");
        clientKeyFile = Path.Combine(tlsDirectory, "cms.key");
        WriteTlsMaterial(caCertificateFile, clientCertificateFile, clientKeyFile);
    }

    public void Dispose()
    {
        Directory.Delete(tlsDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Golden_digest_matches_the_cross_language_contract()
    {
        var digest = AccountAccessGateContract.DigestSubject(
            AccountAccessGateContract.GoldenSubject);

        Assert.Equal(AccountAccessGateContract.GoldenDigest, digest);
        Assert.Equal(digest, digest.ToLowerInvariant());
        Assert.Equal(64, digest.Length);
        Assert.Equal(
            AccountAccessGateContract.MarkerPrefix + AccountAccessGateContract.GoldenDigest,
            AccountAccessGateContract.MarkerKey(AccountAccessGateContract.GoldenSubject));
        Assert.DoesNotContain(
            AccountAccessGateContract.GoldenSubject,
            AccountAccessGateContract.MarkerKey(AccountAccessGateContract.GoldenSubject),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_requires_an_explicit_gate_mode()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AccountAccessGateOptions.FromConfiguration(
                Configuration([]),
                TestEnvironment(Environments.Production)));

        Assert.Contains("off", exception.Message, StringComparison.Ordinal);
        Assert.Contains("enforce", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("observe")]
    [InlineData("allow")]
    [InlineData("")]
    public void Startup_rejects_unknown_gate_modes(string mode)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AccountAccessGateOptions.FromConfiguration(
                Configuration(new()
                {
                    ["AccountAccessGate:Mode"] = mode
                }),
                TestEnvironment(Environments.Production)));

        Assert.Contains("off", exception.Message, StringComparison.Ordinal);
        Assert.Contains("enforce", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_mode_needs_no_Redis_configuration()
    {
        var options = AccountAccessGateOptions.FromConfiguration(
            Configuration(new()
            {
                ["AccountAccessGate:Mode"] = "off"
            }),
            TestEnvironment(Environments.Production));

        Assert.Equal(AccountAccessGateMode.Off, options.Mode);
        Assert.Null(options.Endpoint);
        Assert.Null(options.Username);
        Assert.Null(options.Password);
    }

    [Fact]
    public void Enforce_mode_registers_a_separately_named_Redis_connection()
    {
        var services = new ServiceCollection();

        services.AddAccountAccessGate(
            Configuration(CompleteSettings()),
            TestEnvironment(Environments.Production));

        var redisDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        Assert.True(redisDescriptor.IsKeyedService);
        Assert.Equal(AccountAccessGateContract.RedisConnectionName, redisDescriptor.ServiceKey);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IConnectionMultiplexer) && !descriptor.IsKeyedService);
    }

    [Theory]
    [InlineData("Endpoint")]
    [InlineData("Username")]
    [InlineData("Password")]
    [InlineData("Database")]
    [InlineData("Tls")]
    [InlineData("CaCertificateFile")]
    [InlineData("ClientCertificateFile")]
    [InlineData("ClientKeyFile")]
    public void Enforce_mode_requires_every_connection_setting(string missingSetting)
    {
        var settings = CompleteSettings();
        settings.Remove($"AccountAccessGate:{missingSetting}");

        var exception = Assert.Throws<InvalidOperationException>(
            () => AccountAccessGateOptions.FromConfiguration(
                Configuration(settings),
                TestEnvironment(Environments.Production)));

        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tls_uses_the_private_CA_and_client_certificate()
    {
        var options = AccountAccessGateOptions.FromConfiguration(
            Configuration(CompleteSettings()),
            TestEnvironment(Environments.Production));
        var redis = options.ToRedisConfiguration();

        Assert.NotNull(redis.SslClientAuthenticationOptions);
        var tls = redis.SslClientAuthenticationOptions("redis.internal");
        Assert.Equal("redis.internal", tls.TargetHost);
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, tls.EnabledSslProtocols);
        Assert.Single(tls.ClientCertificates!);
        Assert.True(((X509Certificate2)tls.ClientCertificates![0]!).HasPrivateKey);
        Assert.Equal(X509ChainTrustMode.CustomRootTrust, tls.CertificateChainPolicy!.TrustMode);
        Assert.Single(tls.CertificateChainPolicy.CustomTrustStore);
    }

    [Fact]
    public void Enforce_mode_requires_the_exact_versioned_issuer()
    {
        var settings = CompleteSettings();
        settings["Keycloak:Authority"] = AccountAccessGateContract.ExactIssuer + "/";

        var exception = Assert.Throws<InvalidOperationException>(
            () => AccountAccessGateOptions.FromConfiguration(
                Configuration(settings),
                TestEnvironment(Environments.Production)));

        Assert.Contains(AccountAccessGateContract.ExactIssuer, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unset_host_environment_defaults_to_Production_and_rejects_non_TLS()
    {
        WithHostEnvironment(dotnetEnvironment: null, aspNetCoreEnvironment: null, builder =>
        {
            Assert.True(builder.Environment.IsProduction());
            var settings = CompleteSettings();
            settings["AccountAccessGate:Tls"] = "false";
            builder.Configuration.AddInMemoryCollection(settings);

            var exception = Assert.Throws<InvalidOperationException>(
                () => AccountAccessGateOptions.FromConfiguration(
                    builder.Configuration,
                    builder.Environment));

            Assert.Contains("must be true in production", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Dotnet_environment_takes_precedence_over_AspNetCore_environment()
    {
        WithHostEnvironment("Development", "Production", builder =>
        {
            Assert.True(builder.Environment.IsDevelopment());
        });
    }

    [Fact]
    public void Development_host_allows_documented_local_non_TLS_configuration()
    {
        WithHostEnvironment("Development", null, builder =>
        {
            var settings = CompleteSettings();
            settings["AccountAccessGate:Tls"] = "false";
            builder.Configuration.AddInMemoryCollection(settings);

            var options = AccountAccessGateOptions.FromConfiguration(
                builder.Configuration,
                builder.Environment);

            Assert.False(options.UseTls);
            Assert.Equal(AccountAccessGateMode.Enforce, options.Mode);
        });
    }

    [Theory]
    [InlineData("redis://localhost:6379")]
    [InlineData("localhost:6379,password=secret")]
    [InlineData("localhost")]
    [InlineData("localhost:not-a-port")]
    public void Endpoint_accepts_only_one_host_and_port(string endpoint)
    {
        var settings = CompleteSettings();
        settings["AccountAccessGate:Endpoint"] = endpoint;

        var exception = Assert.Throws<InvalidOperationException>(
            () => AccountAccessGateOptions.FromConfiguration(
                Configuration(settings),
                TestEnvironment(Environments.Production)));

        Assert.Contains("host:port", exception.Message, StringComparison.Ordinal);
    }

    private Dictionary<string, string?> CompleteSettings() => new()
    {
        ["Keycloak:Authority"] = AccountAccessGateContract.ExactIssuer,
        ["AccountAccessGate:Mode"] = "enforce",
        ["AccountAccessGate:Endpoint"] = "localhost:6379",
        ["AccountAccessGate:Username"] = "cms-reader",
        ["AccountAccessGate:Password"] = "secret",
        ["AccountAccessGate:Database"] = "4",
        ["AccountAccessGate:Tls"] = "true",
        ["AccountAccessGate:CaCertificateFile"] = caCertificateFile,
        ["AccountAccessGate:ClientCertificateFile"] = clientCertificateFile,
        ["AccountAccessGate:ClientKeyFile"] = clientKeyFile
    };

    private static void WriteTlsMaterial(
        string caPath,
        string certificatePath,
        string keyPath)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=SKY LAB test access gate CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var caCertificate = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));

        using var clientKey = RSA.Create(2048);
        var clientRequest = new CertificateRequest(
            "CN=cms-account-access-gate",
            clientKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        clientRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        clientRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        clientRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.2")],
                true));
        using var clientCertificate = clientRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
        using var exportedClientKey = clientCertificate.GetRSAPrivateKey();

        File.WriteAllText(caPath, caCertificate.ExportCertificatePem());
        File.WriteAllText(certificatePath, clientCertificate.ExportCertificatePem());
        File.WriteAllText(keyPath, exportedClientKey!.ExportPkcs8PrivateKeyPem());
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IHostEnvironment TestEnvironment(string environmentName) =>
        new TestHostEnvironment
        {
            EnvironmentName = environmentName
        };

    private static void WithHostEnvironment(
        string? dotnetEnvironment,
        string? aspNetCoreEnvironment,
        Action<WebApplicationBuilder> assertion)
    {
        var originalDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var originalAspNetCore = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspNetCoreEnvironment);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = []
            });
            assertion(builder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnet);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalAspNetCore);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Skylab.Cms.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
