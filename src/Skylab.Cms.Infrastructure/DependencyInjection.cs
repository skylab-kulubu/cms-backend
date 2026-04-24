using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Skylab.Cms.Application.Contracts.Repositories;
using Skylab.Cms.Infrastructure.Storage;
using Skylab.Cms.Infrastructure.Storage.Repositories;

namespace Skylab.Cms.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddDbContext<CmsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(CmsDbContext).Assembly.FullName)));

        services.AddScoped<IContentBlockRepository, ContentBlockRepository>();

        return services;
    }
}