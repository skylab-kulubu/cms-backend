using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Skylab.Cms.Infrastructure.AccountAccess;

public static class AccountAccessGateRegistration
{
    public static IServiceCollection AddAccountAccessGate(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment)
    {
        var options = AccountAccessGateOptions.FromConfiguration(configuration, hostEnvironment);
        services.AddSingleton(options);

        if (options.Mode == AccountAccessGateMode.Off)
        {
            services.AddSingleton<IAccountAccessGate, DisabledAccountAccessGate>();
            return services;
        }

        services.AddKeyedSingleton<IConnectionMultiplexer>(
            AccountAccessGateContract.RedisConnectionName,
            (_, _) => ConnectionMultiplexer.Connect(options.ToRedisConfiguration()));
        services.AddSingleton<IAccountAccessGate>(serviceProvider =>
            new RedisAccountAccessGate(
                serviceProvider.GetRequiredKeyedService<IConnectionMultiplexer>(
                    AccountAccessGateContract.RedisConnectionName),
                options));

        return services;
    }
}
