using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.Platform.Persistence;
using FitCore.Subscriptions.Application.Entitlements;
using FitCore.Subscriptions.Application.Plans;
using FitCore.Subscriptions.Application.Subscriptions;
using FitCore.Subscriptions.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Subscriptions;

public static class SubscriptionsModule
{
    /// <summary>Acceso a datos del módulo (sin casos de uso). Lo usa también el Worker; requiere <c>AddPlatformPersistence</c>.</summary>
    public static IServiceCollection AddSubscriptionsPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<SubscriptionsDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"), npgsql => npgsql.MigrationsHistoryTable(SubscriptionsMigrations.HistoryTable, SubscriptionsDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(
                    provider.GetRequiredService<TenantConnectionInterceptor>(),
                    provider.GetRequiredService<AuditSaveChangesInterceptor>()));

        // Compartido con el Worker: el proceso de vencimientos y sus valores de configuración.
        services.Configure<SubscriptionOptions>(configuration.GetSection(SubscriptionOptions.SectionName));
        services.AddSingleton<SubscriptionLifecycleRunner>();
        return services;
    }

    /// <summary>Requiere <c>AddPlatformModule</c> (comparte contexto de tenant, interceptores y control de acceso).</summary>
    public static IServiceCollection AddSubscriptionsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSubscriptionsPersistence(configuration);
        services.AddScoped<ITenantProvisioningHook, SubscriptionProvisioningHook>();
        services.AddScoped<ICapabilityEvaluator, CapabilityEvaluator>();
        services.AddSingleton<IModuleInitializer, SubscriptionsInitializer>();
        services.AddValidatorsFromAssemblyContaining<CreatePlanRequestValidator>(includeInternalTypes: true);
        services.AddHandlersFromAssembly(typeof(SubscriptionsModule).Assembly);
        return services;
    }
}

internal static class SubscriptionsMigrations
{
    public const string HistoryTable = "__ef_migrations_history";

    public static DbContextOptions<SubscriptionsDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<SubscriptionsDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, SubscriptionsDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
            .Options;
}

internal sealed class SubscriptionsInitializer : IModuleInitializer
{
    public async Task InitializeAsync(string migrationsConnection, CancellationToken cancellationToken)
    {
        await using var db = new SubscriptionsDbContext(SubscriptionsMigrations.Options(migrationsConnection), new TenantContext());
        await db.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Solo para <c>dotnet ef</c>. Requiere FITCORE_MIGRATIONS_CONNECTION (nunca un secreto en el código).</summary>
internal sealed class SubscriptionsDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<SubscriptionsDbContext>
{
    public SubscriptionsDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FITCORE_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Defina FITCORE_MIGRATIONS_CONNECTION para ejecutar migraciones.");
        return new SubscriptionsDbContext(SubscriptionsMigrations.Options(connection), new TenantContext());
    }
}
