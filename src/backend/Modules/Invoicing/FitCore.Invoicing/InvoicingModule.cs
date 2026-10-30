using FitCore.Application.Core;
using FitCore.Infrastructure.Core;
using FitCore.Invoicing.Application.Security;
using FitCore.Invoicing.Application.Settings;
using FitCore.Invoicing.Persistence;
using FitCore.Platform.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Invoicing;

public static class InvoicingModule
{
    /// <summary>Acceso a datos y protección de secretos (sin casos de uso). Lo usa también el Worker; requiere <c>AddPlatformPersistence</c>.</summary>
    public static IServiceCollection AddInvoicingPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<InvoicingDbContext>((provider, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("Default"), npgsql => npgsql.MigrationsHistoryTable(InvoicingMigrations.HistoryTable, InvoicingDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
                .AddInterceptors(
                    provider.GetRequiredService<TenantConnectionInterceptor>(),
                    provider.GetRequiredService<AuditSaveChangesInterceptor>()));

        services.Configure<InvoicingOptions>(configuration.GetSection(InvoicingOptions.SectionName));
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        return services;
    }

    /// <summary>Requiere <c>AddPlatformModule</c> (comparte contexto de tenant, interceptores y control de acceso).</summary>
    public static IServiceCollection AddInvoicingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddInvoicingPersistence(configuration);
        services.AddScoped<SettingsReader>();
        services.AddScoped<FitCore.Invoicing.Application.Sources.InvoicePlanner>();
        services.AddSingleton<IModuleInitializer, InvoicingInitializer>();
        services.AddValidatorsFromAssemblyContaining<SaveIssuerRequestValidator>(includeInternalTypes: true);
        services.AddHandlersFromAssembly(typeof(InvoicingModule).Assembly);
        return services;
    }
}

internal static class InvoicingMigrations
{
    public const string HistoryTable = "__ef_migrations_history";

    public static DbContextOptions<InvoicingDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<InvoicingDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, InvoicingDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.MappedEntityTypeIgnoredWarning))
            .Options;
}

internal sealed class InvoicingInitializer : IModuleInitializer
{
    public async Task InitializeAsync(string migrationsConnection, CancellationToken cancellationToken)
    {
        await using var db = new InvoicingDbContext(InvoicingMigrations.Options(migrationsConnection), new TenantContext());
        await db.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Solo para <c>dotnet ef</c>. Requiere FITCORE_MIGRATIONS_CONNECTION (nunca un secreto en el código).</summary>
internal sealed class InvoicingDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<InvoicingDbContext>
{
    public InvoicingDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FITCORE_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException("Defina FITCORE_MIGRATIONS_CONNECTION para ejecutar migraciones.");
        return new InvoicingDbContext(InvoicingMigrations.Options(connection), new TenantContext());
    }
}
