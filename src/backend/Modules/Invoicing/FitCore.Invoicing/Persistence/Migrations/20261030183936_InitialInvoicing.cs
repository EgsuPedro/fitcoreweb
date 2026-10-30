using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Invoicing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "invoicing");

            migrationBuilder.CreateTable(
                name: "emission_points",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    establishment_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    point_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    establishment_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_emission_points", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "issuer_profiles",
                schema: "invoicing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ruc = table.Column<string>(type: "character(13)", fixedLength: true, maxLength: 13, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    trade_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    head_office_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    tax_regime = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    accounting_required = table.Column<bool>(type: "boolean", nullable: false),
                    special_taxpayer_number = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    withholding_agent_number = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    emission_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notification_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issuer_profiles", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "signing_certificates",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    serial_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    thumbprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    subject_ruc = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    encrypted_p12 = table.Column<byte[]>(type: "bytea", nullable: false),
                    encrypted_password = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signing_certificates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_emission_points_tenant_id_establishment_code_point_code",
                schema: "invoicing",
                table: "emission_points",
                columns: new[] { "tenant_id", "establishment_code", "point_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_signing_certificates_tenant_id",
                schema: "invoicing",
                table: "signing_certificates",
                column: "tenant_id",
                unique: true,
                filter: "is_active");

            // Seguridad de la facturación electrónica: aislamiento por gimnasio (RLS forzada, como el resto de tablas con tenant_id), valores acotados por la base y
            // que los secretos del certificado solo existan mientras está activo. Los puntos de emisión y los certificados jamás se borran (se desactivan o se retiran).
            migrationBuilder.Sql("""
                ALTER TABLE invoicing.issuer_profiles
                  ADD CONSTRAINT fk_issuer_profiles_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_issuer_profiles_ruc CHECK (ruc ~ '^[0-9]{13}$'),
                  ADD CONSTRAINT ck_issuer_profiles_regime CHECK (tax_regime IN ('General', 'RimpeEmprendedor', 'RimpeNegocioPopular')),
                  ADD CONSTRAINT ck_issuer_profiles_environment CHECK (environment IN ('Test', 'Production')),
                  ADD CONSTRAINT ck_issuer_profiles_emission_type CHECK (emission_type IN ('Normal'));

                ALTER TABLE invoicing.emission_points
                  ADD CONSTRAINT fk_emission_points_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_emission_points_branch FOREIGN KEY (tenant_id, branch_id) REFERENCES organizations.branches (tenant_id, id),
                  ADD CONSTRAINT ck_emission_points_codes CHECK (establishment_code ~ '^[0-9]{3}$' AND point_code ~ '^[0-9]{3}$' AND establishment_code <> '000' AND point_code <> '000');

                ALTER TABLE invoicing.signing_certificates
                  ADD CONSTRAINT fk_signing_certificates_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_signing_certificates_thumbprint CHECK (thumbprint ~ '^[0-9a-f]{64}$'),
                  ADD CONSTRAINT ck_signing_certificates_secrets CHECK (
                    (is_active AND length(encrypted_p12) > 0 AND length(encrypted_password) > 0)
                    OR (NOT is_active AND length(encrypted_p12) = 0 AND length(encrypted_password) = 0));

                ALTER TABLE invoicing.issuer_profiles ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.issuer_profiles FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.issuer_profiles
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE invoicing.emission_points ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.emission_points FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.emission_points
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE invoicing.signing_certificates ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.signing_certificates FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.signing_certificates
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT USAGE ON SCHEMA invoicing TO fitcore_app;
                    GRANT SELECT, INSERT, UPDATE ON invoicing.issuer_profiles, invoicing.emission_points, invoicing.signing_certificates TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "emission_points",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "issuer_profiles",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "signing_certificates",
                schema: "invoicing");
        }
    }
}
