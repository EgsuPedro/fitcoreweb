using FitCore.Analytics.Persistence;
using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Analytics;

public static class AnalyticsModule
{
    /// <summary>
    /// Requiere <c>AddPlatformModule</c>. Lee los datos de los demás módulos sin seguimiento de cambios; lo único que escribe es el registro de sus exportaciones (sin el interceptor de
    /// auditoría (no escribe nada).
    /// </summary>
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AnalyticsDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"), npgsql => npgsql.MigrationsHistoryTable(AnalyticsMigrations.HistoryTable, AnalyticsDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(provider.GetRequiredService<TenantConnectionInterceptor>()));

        services.AddSingleton<IModuleInitializer, AnalyticsInitializer>();
        services.AddHandlersFromAssembly(typeof(AnalyticsModule).Assembly);
        return services;
    }
}

internal static class AnalyticsMigrations
{
    public const string HistoryTable = "__ef_migrations_history";

    public static DbContextOptions<AnalyticsDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, AnalyticsDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
            .Options;
}

internal sealed class AnalyticsInitializer : IModuleInitializer
{
    public async Task InitializeAsync(string migrationsConnection, CancellationToken cancellationToken)
    {
        await using var db = new AnalyticsDbContext(AnalyticsMigrations.Options(migrationsConnection), new TenantContext());
        await db.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Solo para <c>dotnet ef</c>. Requiere FITCORE_MIGRATIONS_CONNECTION (nunca un secreto en el código).</summary>
internal sealed class AnalyticsDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FITCORE_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Defina FITCORE_MIGRATIONS_CONNECTION para ejecutar migraciones.");
        return new AnalyticsDbContext(AnalyticsMigrations.Options(connection), new TenantContext());
    }
}
