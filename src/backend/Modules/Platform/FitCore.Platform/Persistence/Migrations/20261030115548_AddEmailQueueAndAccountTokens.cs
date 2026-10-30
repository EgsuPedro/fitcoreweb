using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailQueueAndAccountTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "emails",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    template = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    text_body = table.Column<string>(type: "text", nullable: true),
                    html_body = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_emails", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_tokens_expires_at",
                schema: "identity",
                table: "account_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_account_tokens_purpose_subject_id",
                schema: "identity",
                table: "account_tokens",
                columns: new[] { "purpose", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_account_tokens_token_hash",
                schema: "identity",
                table: "account_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_emails_created_at",
                schema: "messaging",
                table: "emails",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_emails_status_next_attempt_at",
                schema: "messaging",
                table: "emails",
                columns: new[] { "status", "next_attempt_at" });

            // Tablas internas sin RLS (la API y el Worker las usan con el rol limitado). Los permisos son explícitos, no solo por defecto.
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON messaging.emails, identity.account_tokens TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "emails",
                schema: "messaging");
        }
    }
}
