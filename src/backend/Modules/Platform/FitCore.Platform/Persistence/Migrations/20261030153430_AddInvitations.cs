using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    email_normalized = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    send_count = table.Column<int>(type: "integer", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invitations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_tenant_id_email_normalized",
                schema: "identity",
                table: "invitations",
                columns: new[] { "tenant_id", "email_normalized" },
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_invitations_tenant_id_status_created_at",
                schema: "identity",
                table: "invitations",
                columns: new[] { "tenant_id", "status", "created_at" });

            // Invitaciones de personal: aisladas por gimnasio con RLS forzada, con estados acotados y a lo sumo una pendiente por correo y gimnasio.
            migrationBuilder.Sql("""
                ALTER TABLE identity.invitations
                  ADD CONSTRAINT fk_invitations_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_invitations_status CHECK (status IN ('Pending', 'Accepted', 'Revoked'));

                ALTER TABLE identity.invitations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.invitations FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON identity.invitations
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON identity.invitations TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitations",
                schema: "identity");
        }
    }
}
