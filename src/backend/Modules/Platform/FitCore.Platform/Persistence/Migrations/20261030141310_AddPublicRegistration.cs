using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "setup_status",
                schema: "organizations",
                table: "tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Ready");

            migrationBuilder.CreateTable(
                name: "legal_acceptances",
                schema: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_acceptances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pending_registrations",
                schema: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    email_normalized = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    owner_full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    gym_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    plan_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    locale = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    terms_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    privacy_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    send_count = table.Column<int>(type: "integer", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_registrations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "trial_claims",
                schema: "organizations",
                columns: table => new
                {
                    email_normalized = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trial_claims", x => x.email_normalized);
                });

            migrationBuilder.CreateIndex(
                name: "ix_legal_acceptances_tenant_id_user_id",
                schema: "organizations",
                table: "legal_acceptances",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_registrations_created_at",
                schema: "organizations",
                table: "pending_registrations",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_pending_registrations_email_normalized_status",
                schema: "organizations",
                table: "pending_registrations",
                columns: new[] { "email_normalized", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_registrations_slug_status",
                schema: "organizations",
                table: "pending_registrations",
                columns: new[] { "slug", "status" });

            // Registro público. Los estados y el estado de configuración quedan acotados por la base; la aceptación legal se aísla por gimnasio con RLS
            // forzada como el resto de tablas con tenant_id. Las otras dos no tienen gimnasio al que pertenecer (el registro es previo al alta; la prueba es global).
            migrationBuilder.Sql("""
                ALTER TABLE organizations.tenants
                  ADD CONSTRAINT ck_tenants_setup_status CHECK (setup_status IN ('PendingSetup', 'Ready'));
                ALTER TABLE organizations.pending_registrations
                  ADD CONSTRAINT ck_pending_registrations_status
                  CHECK (status IN ('Pending', 'Provisioning', 'Verified', 'Expired', 'Superseded', 'Rejected', 'Failed'));
                ALTER TABLE organizations.trial_claims
                  ADD CONSTRAINT fk_trial_claims_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);
                ALTER TABLE organizations.legal_acceptances
                  ADD CONSTRAINT fk_legal_acceptances_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_legal_acceptances_document CHECK (document IN ('terms', 'privacy'));

                ALTER TABLE organizations.legal_acceptances ENABLE ROW LEVEL SECURITY;
                ALTER TABLE organizations.legal_acceptances FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON organizations.legal_acceptances
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON organizations.pending_registrations, organizations.trial_claims, organizations.legal_acceptances TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_acceptances",
                schema: "organizations");

            migrationBuilder.DropTable(
                name: "pending_registrations",
                schema: "organizations");

            migrationBuilder.DropTable(
                name: "trial_claims",
                schema: "organizations");

            migrationBuilder.DropColumn(
                name: "setup_status",
                schema: "organizations",
                table: "tenants");
        }
    }
}
