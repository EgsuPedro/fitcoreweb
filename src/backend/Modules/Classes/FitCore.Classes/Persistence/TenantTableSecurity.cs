using Microsoft.EntityFrameworkCore.Migrations;

namespace FitCore.Classes.Persistence;

/// <summary>
/// SQL de seguridad que EF no modela y que cada migración repite para sus tablas nuevas: Row-Level Security forzado,
/// claves foráneas hacia tenants y sucursales, y permisos del rol de la aplicación.
/// </summary>
internal static class TenantTableSecurity
{
    private const string TenantExpression = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

    public static void EnableRowLevelSecurity(MigrationBuilder migrationBuilder, params string[] tables)
    {
        foreach (var table in tables)
        {
            migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
            // FORCE: ni siquiera el propietario de la tabla (que no sea superusuario) se salta la política.
            migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON {table} USING ({TenantExpression}) WITH CHECK ({TenantExpression});");
        }
    }

    public static void DisableRowLevelSecurity(MigrationBuilder migrationBuilder, params string[] tables)
    {
        foreach (var table in tables)
        {
            migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
            migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
        }
    }

    /// <summary>FK compuesta (tenant_id, branch_id) hacia la sucursal: la base rechaza sucursales de otro gimnasio.</summary>
    public static void AddBranchForeignKey(MigrationBuilder migrationBuilder, string table, string constraint, string column = "branch_id") =>
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ADD CONSTRAINT {constraint}
              FOREIGN KEY (tenant_id, {column}) REFERENCES organizations.branches (tenant_id, id);
            """);

    public static void AddTenantForeignKey(MigrationBuilder migrationBuilder, string table, string constraint) =>
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ADD CONSTRAINT {constraint}
              FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);
            """);

    /// <summary>Permisos del rol de la aplicación (si existe) sobre los esquemas indicados, incluidas las tablas futuras.</summary>
    public static void GrantApplicationRole(MigrationBuilder migrationBuilder, params string[] schemas)
    {
        var list = string.Join(", ", schemas);
        migrationBuilder.Sql($"""
            DO $do$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                GRANT USAGE ON SCHEMA {list} TO fitcore_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {list} TO fitcore_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {list} TO fitcore_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA {list} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO fitcore_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA {list} GRANT USAGE, SELECT ON SEQUENCES TO fitcore_app;
              END IF;
            END
            $do$;
            """);

        AppendOnlyRevokes(migrationBuilder);
    }

    /// <summary>
    /// Tablas de las que el rol de la aplicación no puede borrar (o cuyas trazas solo agrega). Como cada migración repite los permisos
    /// del esquema, estas restricciones se reaplican siempre al final: así una migración posterior no las deshace.
    /// </summary>
    private static void AppendOnlyRevokes(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            DO $do$
            DECLARE r record;
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                FOR r IN SELECT * FROM (VALUES
                  ('classes.group_classes', 'DELETE')
                ) AS t(tbl, privs) LOOP
                  IF to_regclass(r.tbl) IS NOT NULL THEN
                    EXECUTE format('REVOKE %s ON %s FROM fitcore_app', r.privs, r.tbl);
                  END IF;
                END LOOP;
              END IF;
            END
            $do$;
            """);
}
