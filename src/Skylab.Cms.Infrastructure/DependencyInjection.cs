using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Skylab.Cms.Application.Contracts.Repositories;
using Skylab.Cms.Infrastructure.Storage;
using Skylab.Cms.Infrastructure.Storage.Repositories;

namespace Skylab.Cms.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        services.AddDbContext<CmsDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql =>
                npgsql.MigrationsAssembly(typeof(CmsDbContext).Assembly.FullName)));

        services.AddScoped<IContentBlockRepository, ContentBlockRepository>();

        return services;
    }
}