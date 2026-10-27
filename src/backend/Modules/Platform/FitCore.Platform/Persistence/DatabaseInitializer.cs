using FitCore.Infrastructure.Core;
using FitCore.Platform.Application.Tenants;
using FitCore.Platform.Domain;
using FitCore.Platform.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FitCore.Platform.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Aplica migraciones al arrancar. Solo para desarrollo y pruebas: en producción se ejecutan como un paso de despliegue.</summary>
    public bool AutoMigrate { get; set; }

    public string AppRoleName { get; set; } = "fitcore_app";

    /// <summary>Si se define, se crea el rol de la aplicación (sin privilegios de superusuario) cuando no existe. Solo desarrollo.</summary>
    public string? AppRolePassword { get; set; }
}

public sealed class PlatformBootstrapOptions
{
    public const string SectionName = "Platform";

    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
    public string AdminFullName { get; set; } = "Administrador de plataforma";
}

public static class DatabaseInitializer
{
    /// <summary>Migraciones, catálogo de permisos y administrador de plataforma. Es idempotente.</summary>
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var database = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("FitCore.DatabaseInitializer");
        if (!database.AutoMigrate)
        {
            return;
        }

        var migrationsConnection = configuration.GetConnectionString("Migrations") ?? configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(migrationsConnection))
        {
            throw new InvalidOperationException("Falta ConnectionStrings:Migrations (o Default) para aplicar migraciones.");
        }

        if (!string.IsNullOrWhiteSpace(database.AppRolePassword))
        {
            await EnsureAppRoleAsync(migrationsConnection, database, cancellationToken);
        }

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(migrationsConnection)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.MappedEntityTypeIgnoredWarning))
            .Options;

        await using (var migrator = new PlatformDbContext(options, new TenantContext()))
        {
            await migrator.Database.MigrateAsync(cancellationToken);
            await SeedPermissionCatalogAsync(migrator, cancellationToken);
        }

        logger.LogInformation("Base de datos migrada y catálogo de permisos sincronizado");
        await BootstrapPlatformAdminAsync(services, logger, cancellationToken);
    }

    private static async Task SeedPermissionCatalogAsync(PlatformDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.PermissionCatalog.ToDictionaryAsync(p => p.Code, cancellationToken);
        foreach (var definition in Permissions.All)
        {
            if (existing.TryGetValue(definition.Code, out var current))
            {
                current.Module = definition.Module;
                current.Description = definition.Description;
            }
            else
            {
                db.PermissionCatalog.Add(new PermissionEntity
                {
                    Code = definition.Code,
                    Module = definition.Module,
                    Description = definition.Description
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await SyncOwnerRolesAsync(db, cancellationToken);
    }

    /// <summary>
    /// El rol Propietario (inmutable) siempre tiene todos los permisos de gimnasio: cuando se agrega un permiso al catálogo,
    /// los gimnasios existentes lo reciben aquí. Se fija el tenant por gimnasio para respetar RLS.
    /// </summary>
    private static async Task SyncOwnerRolesAsync(PlatformDbContext db, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            DO $do$
            DECLARE t record;
            BEGIN
              FOR t IN SELECT id FROM organizations.tenants LOOP
                PERFORM set_config('app.tenant_id', t.id::text, true);
                INSERT INTO identity.role_permissions (tenant_id, role_id, permission_code)
                SELECT r.tenant_id, r.id, p.code
                FROM identity.app_roles r CROSS JOIN identity.permissions p
                WHERE r.tenant_id = t.id AND r.is_system AND r.name = 'Propietario' AND p.code NOT LIKE 'platform.%'
                ON CONFLICT DO NOTHING;
              END LOOP;
            END
            $do$;
            """,
            cancellationToken);

    private static async Task BootstrapPlatformAdminAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var bootstrap = services.GetRequiredService<IOptions<PlatformBootstrapOptions>>().Value;
        if (string.IsNullOrWhiteSpace(bootstrap.AdminEmail) || string.IsNullOrWhiteSpace(bootstrap.AdminPassword))
        {
            logger.LogInformation("Platform:AdminEmail/AdminPassword no definidos: no se crea administrador de plataforma");
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        if (await db.Tenants.AnyAsync(t => t.Slug == Tenant.PlatformSlug, cancellationToken))
        {
            return;
        }

        var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
        var result = await provisioning.ProvisionAsync(
            new CreateTenantRequest(
                Tenant.PlatformSlug,
                "FitCore 360 (plataforma)",
                Tenant.DefaultTimeZone,
                Tenant.DefaultLocale,
                bootstrap.AdminEmail,
                bootstrap.AdminFullName,
                bootstrap.AdminPassword),
            isPlatformTenant: true,
            cancellationToken);

        if (result.IsFailure)
        {
            throw new InvalidOperationException($"No se pudo crear el administrador de plataforma: {result.Error.Code} {result.Error.Description}");
        }

        logger.LogInformation("Administrador de plataforma creado: {Email}", bootstrap.AdminEmail);
    }

    private static async Task EnsureAppRoleAsync(string migrationsConnection, DatabaseOptions options, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.AppRoleName, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new InvalidOperationException("Database:AppRoleName no es un identificador válido.");
        }

        var password = options.AppRolePassword!.Replace("'", "''", StringComparison.Ordinal);
        var sql = $"""
            DO $do$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{options.AppRoleName}') THEN
                CREATE ROLE {options.AppRoleName} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
              END IF;
            END
            $do$;
            """;

        await using var connection = new NpgsqlConnection(migrationsConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
