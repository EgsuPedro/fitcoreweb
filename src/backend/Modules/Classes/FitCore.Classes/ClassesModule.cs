using FitCore.Application.Core;
using FitCore.Classes.Application.GroupClasses;
using FitCore.Classes.Persistence;
using FitCore.Infrastructure.Core;
using FitCore.Platform.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Classes;

public static class ClassesModule
{
    /// <summary>Requiere <c>AddPlatformModule</c> y se registra después de <c>AddWellnessModule</c> (sus claves foráneas apuntan a entrenadores).</summary>
    public static IServiceCollection AddClassesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ClassesDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"), npgsql => npgsql.MigrationsHistoryTable(ClassesMigrations.HistoryTable, ClassesDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(
                    provider.GetRequiredService<TenantConnectionInterceptor>(),
                    provider.GetRequiredService<AuditSaveChangesInterceptor>()));

        services.AddSingleton<IModuleInitializer, ClassesInitializer>();
        services.AddValidatorsFromAssemblyContaining<GroupClassRequestValidator>(includeInternalTypes: true);
        services.AddHandlersFromAssembly(typeof(ClassesModule).Assembly);

        return services;
    }
}

internal static class ClassesMigrations
{
    public const string HistoryTable = "__ef_migrations_history";

    public static DbContextOptions<ClassesDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<ClassesDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, ClassesDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
            .Options;
}

internal sealed class ClassesInitializer : IModuleInitializer
{
    public async Task InitializeAsync(string migrationsConnection, CancellationToken cancellationToken)
    {
        await using var db = new ClassesDbContext(ClassesMigrations.Options(migrationsConnection), new TenantContext());
        await db.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Solo para <c>dotnet ef</c>. Requiere FITCORE_MIGRATIONS_CONNECTION (nunca un secreto en el código).</summary>
internal sealed class ClassesDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ClassesDbContext>
{
    public ClassesDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FITCORE_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Defina FITCORE_MIGRATIONS_CONNECTION para ejecutar migraciones.");
        return new ClassesDbContext(ClassesMigrations.Options(connection), new TenantContext());
    }
}
