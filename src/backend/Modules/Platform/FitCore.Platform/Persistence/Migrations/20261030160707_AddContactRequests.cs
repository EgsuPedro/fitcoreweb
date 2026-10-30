using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContactRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contact_requests",
                schema: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    email_normalized = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    gym_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    privacy_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attended_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contact_requests_email_normalized_created_at",
                schema: "organizations",
                table: "contact_requests",
                columns: new[] { "email_normalized", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_contact_requests_status_created_at",
                schema: "organizations",
                table: "contact_requests",
                columns: new[] { "status", "created_at" });

            // Solicitudes del sitio público: sin gimnasio (no hay RLS por gimnasio); tipo y estado acotados por la base.
            migrationBuilder.Sql("""
                ALTER TABLE organizations.contact_requests
                  ADD CONSTRAINT ck_contact_requests_kind CHECK (kind IN ('Contact', 'Demo')),
                  ADD CONSTRAINT ck_contact_requests_status CHECK (status IN ('New', 'Attended'));

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON organizations.contact_requests TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_requests",
                schema: "organizations");
        }
    }
}
