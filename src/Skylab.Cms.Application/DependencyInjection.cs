using Microsoft.Extensions.DependencyInjection;
using Skylab.Cms.Application.Services;

namespace Skylab.Cms.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IContentService, ContentService>();

        return services;
    }
}