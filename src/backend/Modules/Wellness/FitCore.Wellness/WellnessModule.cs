using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.Platform.Persistence;
using FitCore.Wellness.Application.Trainers;
using FitCore.Wellness.Domain;
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
        await SeedExerciseCatalogAsync(db, cancellationToken);
    }

    /// <summary>
    /// Carga el catálogo base de ejercicios (idempotente: solo agrega los que faltan por nombre). Las filas globales las puede escribir
    /// por política solo la plataforma, así que el dueño de la tabla desactiva un instante el RLS forzado para esta carga inicial.
    /// </summary>
    private static async Task SeedExerciseCatalogAsync(WellnessDbContext db, CancellationToken cancellationToken)
    {
        var existing = (await db.Exercises.AsNoTracking().Where(e => e.TenantId == null).Select(e => e.Name).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = ExerciseCatalog.All.Where(e => !existing.Contains(e.Name)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE training.exercises NO FORCE ROW LEVEL SECURITY", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        db.Exercises.AddRange(missing.Select(details => Exercise.Create(null, details, null, now)));
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE training.exercises FORCE ROW LEVEL SECURITY", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
