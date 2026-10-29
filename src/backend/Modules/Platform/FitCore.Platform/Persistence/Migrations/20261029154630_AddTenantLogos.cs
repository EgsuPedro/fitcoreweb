using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLogos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_logos",
                schema: "organizations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    content_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_logos", x => x.tenant_id);
                });

            // Constancia del logo de cada gimnasio: aislada por RLS forzada como el resto de tablas con tenant_id, con las medidas y el tipo
            // acotados por la base (aunque alguien escriba directamente). El archivo vive en el almacén privado, no aquí.
            migrationBuilder.Sql("""
                ALTER TABLE organizations.tenant_logos
                  ADD CONSTRAINT fk_tenant_logos_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_tenant_logos_type CHECK (content_type IN ('image/png', 'image/jpeg')),
                  ADD CONSTRAINT ck_tenant_logos_size CHECK (size_bytes BETWEEN 1 AND 1048576),
                  ADD CONSTRAINT ck_tenant_logos_dimensions CHECK (width BETWEEN 32 AND 2000 AND height BETWEEN 32 AND 2000),
                  ADD CONSTRAINT ck_tenant_logos_hash CHECK (sha256 ~ '^[0-9a-f]{64}$');

                ALTER TABLE organizations.tenant_logos ENABLE ROW LEVEL SECURITY;
                ALTER TABLE organizations.tenant_logos FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON organizations.tenant_logos
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON organizations.tenant_logos TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON organizations.tenant_logos;");
            migrationBuilder.DropTable(
                name: "tenant_logos",
                schema: "organizations");
        }
    }
}
