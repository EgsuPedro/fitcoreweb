using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.Platform.Persistence;
using FitCore.Wellness.Application.Trainers;
using FitCore.Wellness.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Wellness;

public static class WellnessModule
{
    /// <summary>Requiere <c>AddPlatformModule</c> (comparte contexto de tenant, interceptores y control de acceso).</summary>
    public static IServiceCollection AddWellnessModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<WellnessDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"), npgsql => npgsql.MigrationsHistoryTable(WellnessMigrations.HistoryTable, WellnessDbContext.TrainingSchema))
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(
                    provider.GetRequiredService<TenantConnectionInterceptor>(),
                    provider.GetRequiredService<AuditSaveChangesInterceptor>()));

        services.AddSingleton<IModuleInitializer, WellnessInitializer>();
        services.AddValidatorsFromAssemblyContaining<CreateTrainerRequestValidator>(includeInternalTypes: true);
        services.AddHandlersFromAssembly(typeof(WellnessModule).Assembly);

        return services;
    }
}

internal static class WellnessMigrations
{
    public const string HistoryTable = "__ef_migrations_history";

    public static DbContextOptions<WellnessDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<WellnessDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, WellnessDbContext.TrainingSchema))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
            .Options;
}

internal sealed class WellnessInitializer : IModuleInitializer
{
    public async Task InitializeAsync(string migrationsConnection, CancellationToken cancellationToken)
    {
        await using var db = new WellnessDbContext(WellnessMigrations.Options(migrationsConnection), new TenantContext());
        await db.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Solo para <c>dotnet ef</c>. Requiere FITCORE_MIGRATIONS_CONNECTION (nunca un secreto en el código).</summary>
internal sealed class WellnessDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<WellnessDbContext>
{
    public WellnessDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FITCORE_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Defina FITCORE_MIGRATIONS_CONNECTION para ejecutar migraciones.");
        return new WellnessDbContext(WellnessMigrations.Options(connection), new TenantContext());
    }
}
