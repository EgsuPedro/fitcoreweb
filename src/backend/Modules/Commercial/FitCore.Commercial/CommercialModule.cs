using FitCore.Application.Core;
using FitCore.Commercial.Application;
using FitCore.Commercial.Application.Members;
using FitCore.Commercial.Persistence;
using FitCore.Infrastructure.Core;
using FitCore.Platform.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Commercial;

public static class CommercialModule
{
    /// <summary>Requiere <c>AddPlatformModule</c> (comparte contexto de tenant, interceptores y control de acceso).</summary>
    public static IServiceCollection AddCommercialModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<CommercialDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"), npgsql => npgsql.MigrationsHistoryTable(CommercialMigrations.HistoryTable, CommercialDbContext.MembersSchema))
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(
                    provider.GetRequiredService<TenantConnectionInterceptor>(),
                    provider.GetRequiredService<AuditSaveChangesInterceptor>()));

        services.AddSingleton<IModuleInitializer, CommercialInitializer>();
        services.AddValidatorsFromAssemblyContaining<CreateMemberRequestValidator>(includeInternalTypes: true);
        services.AddHandlersFromAssembly(typeof(CommercialModule).Assembly);

        return services;
    }
}

internal static class CommercialMigrations
{
    public const string HistoryTable = "__ef_migrations_history";

    public static DbContextOptions<CommercialDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<CommercialDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, CommercialDbContext.MembersSchema))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
            .Options;
}

internal sealed class CommercialInitializer : IModuleInitializer
{
    public async Task InitializeAsync(string migrationsConnection, CancellationToken cancellationToken)
    {
        await using var db = new CommercialDbContext(CommercialMigrations.Options(migrationsConnection), new TenantContext());
        await db.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Solo para <c>dotnet ef</c>. Requiere FITCORE_MIGRATIONS_CONNECTION (nunca un secreto en el código).</summary>
internal sealed class CommercialDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<CommercialDbContext>
{
    public CommercialDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FITCORE_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Defina FITCORE_MIGRATIONS_CONNECTION para ejecutar migraciones.");
        return new CommercialDbContext(CommercialMigrations.Options(connection), new TenantContext());
    }
}
