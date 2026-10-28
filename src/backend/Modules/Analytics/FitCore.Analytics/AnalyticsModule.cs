using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Analytics;

public static class AnalyticsModule
{
    /// <summary>
    /// Requiere <c>AddPlatformModule</c>. Solo lee los datos de los demás módulos: sin seguimiento de cambios y sin el interceptor de
    /// auditoría (no escribe nada).
    /// </summary>
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AnalyticsDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"))
                .UseSnakeCaseNamingConvention()
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(provider.GetRequiredService<TenantConnectionInterceptor>()));

        services.AddHandlersFromAssembly(typeof(AnalyticsModule).Assembly);
        return services;
    }
}
