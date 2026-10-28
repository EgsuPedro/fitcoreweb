using FitCore.Application.Core;
using FitCore.Classes.Application.GroupClasses;
using FitCore.Classes.Application.Reservations;
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
    /// <summary>Acceso a datos del módulo (sin casos de uso). Lo usa también el Worker; requiere <c>AddPlatformPersistence</c>.</summary>
    public static IServiceCollection AddClassesPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ClassesDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"), npgsql => npgsql.MigrationsHistoryTable(ClassesMigrations.HistoryTable, ClassesDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(
                    provider.GetRequiredService<TenantConnectionInterceptor>(),
                    provider.GetRequiredService<AuditSaveChangesInterceptor>()));

        return services;
    }

    /// <summary>Mantenimiento de la lista de espera (invitaciones vencidas y cupos por ofrecer); lo ejecuta el Worker.</summary>
    public static IServiceCollection AddWaitlistMaintenance(this IServiceCollection services)
    {
        services.AddSingleton<WaitlistRunner>();
        return services;
    }

    /// <summary>Requiere <c>AddPlatformModule</c> y se registra después de <c>AddWellnessModule</c> (sus claves foráneas apuntan a entrenadores).</summary>
    public static IServiceCollection AddClassesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddClassesPersistence(configuration);
        services.AddWaitlistMaintenance();

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
