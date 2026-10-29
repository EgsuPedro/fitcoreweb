using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "account_kind",
                schema: "identity",
                table: "app_users",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Staff");

            // Las cuentas de alumnos (rol de plantilla «Alumno») no cuentan como usuarios internos del plan. Se recorre gimnasio por
            // gimnasio fijando el contexto, porque la tabla tiene Row-Level Security forzado.
            migrationBuilder.Sql("""
                DO $do$
                DECLARE t record;
                BEGIN
                  FOR t IN SELECT id FROM organizations.tenants LOOP
                    PERFORM set_config('app.tenant_id', t.id::text, true);
                    UPDATE identity.app_users u SET account_kind = 'Member'
                    WHERE u.tenant_id = t.id AND EXISTS (
                      SELECT 1 FROM identity.user_roles ur JOIN identity.app_roles r ON r.id = ur.role_id
                      WHERE ur.user_id = u.id AND r.is_system AND r.name = 'Alumno');
                  END LOOP;
                END
                $do$;
                ALTER TABLE identity.app_users ADD CONSTRAINT ck_app_users_account_kind CHECK (account_kind IN ('Staff', 'Member'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE identity.app_users DROP CONSTRAINT IF EXISTS ck_app_users_account_kind;");
            migrationBuilder.DropColumn(
                name: "account_kind",
                schema: "identity",
                table: "app_users");
        }
    }
}
