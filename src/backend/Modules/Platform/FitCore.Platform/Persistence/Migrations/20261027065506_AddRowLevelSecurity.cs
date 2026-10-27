using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Platform.Persistence.Migrations;

/// <inheritdoc />
public partial class AddRowLevelSecurity : Migration
{
    // Tablas con tenant_id: toda lectura y escritura queda limitada al tenant activo de la conexión.
    private static readonly string[] TenantTables =
    [
        "identity.app_users",
        "identity.app_roles",
        "identity.user_roles",
        "identity.role_permissions",
        "identity.user_branches",
        "identity.refresh_tokens",
        "organizations.branches",
        "audit.audit_logs"
    ];

    private const string TenantExpression = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
            // FORCE: ni siquiera el propietario de la tabla (que no sea superusuario) se salta la política.
            migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                $"CREATE POLICY tenant_isolation ON {table} USING ({TenantExpression}) WITH CHECK ({TenantExpression});");
        }

        // user_roles no tiene FK compuestas en el modelo de EF (la entidad es de Identity): se crean aquí.
        migrationBuilder.Sql("""
            ALTER TABLE identity.user_roles
              ADD CONSTRAINT fk_user_roles_user_tenant FOREIGN KEY (tenant_id, user_id)
              REFERENCES identity.app_users (tenant_id, id) ON DELETE CASCADE;
            ALTER TABLE identity.user_roles
              ADD CONSTRAINT fk_user_roles_role_tenant FOREIGN KEY (tenant_id, role_id)
              REFERENCES identity.app_roles (tenant_id, id) ON DELETE CASCADE;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE organizations.tenants
              ADD CONSTRAINT ck_tenants_status CHECK (status IN ('Active', 'Suspended'));
            """);

        // Permisos del rol de la aplicación (si existe). La auditoría es append-only: solo SELECT e INSERT.
        migrationBuilder.Sql("""
            DO $do$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                GRANT USAGE ON SCHEMA identity, organizations, audit, messaging TO fitcore_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, organizations, messaging TO fitcore_app;
                GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO fitcore_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity, organizations, audit, messaging TO fitcore_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA identity, organizations, messaging
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO fitcore_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT, INSERT ON TABLES TO fitcore_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA identity, organizations, audit, messaging
                  GRANT USAGE, SELECT ON SEQUENCES TO fitcore_app;
              END IF;
            END
            $do$;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE organizations.tenants DROP CONSTRAINT IF EXISTS ck_tenants_status;");
        migrationBuilder.Sql("ALTER TABLE identity.user_roles DROP CONSTRAINT IF EXISTS fk_user_roles_role_tenant;");
        migrationBuilder.Sql("ALTER TABLE identity.user_roles DROP CONSTRAINT IF EXISTS fk_user_roles_user_tenant;");

        foreach (var table in TenantTables)
        {
            migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
            migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
        }
    }
}
