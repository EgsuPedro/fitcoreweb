using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contact_email",
                schema: "organizations",
                table: "tenants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_phone",
                schema: "organizations",
                table: "tenants",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "organizations",
                table: "tenants",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                defaultValue: "EC");

            migrationBuilder.AddColumn<string>(
                name: "hours",
                schema: "organizations",
                table: "branches",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                schema: "organizations",
                table: "branches",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_onboarding",
                schema: "organizations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    branch_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    settings_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    settings_skipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    team_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    team_skipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_onboarding", x => x.tenant_id);
                });

            // Avance del asistente de configuración: aislado por gimnasio con RLS forzada como el resto de tablas con tenant_id.
            migrationBuilder.Sql("""
                ALTER TABLE organizations.tenant_onboarding
                  ADD CONSTRAINT fk_tenant_onboarding_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);
                ALTER TABLE organizations.tenants
                  ADD CONSTRAINT ck_tenants_country CHECK (country ~ '^[A-Z]{2}$');

                ALTER TABLE organizations.tenant_onboarding ENABLE ROW LEVEL SECURITY;
                ALTER TABLE organizations.tenant_onboarding FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON organizations.tenant_onboarding
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON organizations.tenant_onboarding TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_onboarding",
                schema: "organizations");

            migrationBuilder.DropColumn(
                name: "contact_email",
                schema: "organizations",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "contact_phone",
                schema: "organizations",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "country",
                schema: "organizations",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "hours",
                schema: "organizations",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "phone",
                schema: "organizations",
                table: "branches");
        }
    }
}
