using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Invoicing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoice_emails",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_emails", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_emails_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_emails_invoice_id",
                schema: "invoicing",
                table: "invoice_emails",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_emails_next_attempt_at",
                schema: "invoicing",
                table: "invoice_emails",
                column: "next_attempt_at",
                filter: "status IN ('Pending', 'Sending')");

            migrationBuilder.Sql("""
                ALTER TABLE invoicing.invoice_emails
                  ADD CONSTRAINT fk_invoice_emails_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_invoice_emails_status CHECK (status IN ('Pending', 'Sending', 'Sent', 'Failed')),
                  ADD CONSTRAINT ck_invoice_emails_attempts CHECK (attempts >= 0),
                  ADD CONSTRAINT ck_invoice_emails_sent CHECK ((status = 'Sent') = (sent_at IS NOT NULL));

                ALTER TABLE invoicing.invoice_emails ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoice_emails FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoice_emails
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON invoicing.invoice_emails TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_emails",
                schema: "invoicing");
        }
    }
}
