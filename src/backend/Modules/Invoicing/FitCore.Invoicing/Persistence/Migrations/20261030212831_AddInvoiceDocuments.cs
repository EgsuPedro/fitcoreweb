using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Invoicing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoice_documents",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_documents_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_documents_invoice_id_kind",
                schema: "invoicing",
                table: "invoice_documents",
                columns: new[] { "invoice_id", "kind" },
                unique: true);

            // Documentos del comprobante (XML y XML firmado): inmutables, con huella SHA-256 y aislados por gimnasio. Además, la base hace cumplir el ciclo de vida: un comprobante solo avanza por
            // las transiciones válidas y no puede quedar «firmado» (ni pasar de ahí) sin su XML firmado guardado.
            migrationBuilder.Sql("""
                ALTER TABLE invoicing.invoice_documents
                  ADD CONSTRAINT fk_invoice_documents_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_invoice_documents_kind CHECK (kind IN ('Xml', 'SignedXml')),
                  ADD CONSTRAINT ck_invoice_documents_sha256 CHECK (sha256 ~ '^[0-9a-f]{64}$'),
                  ADD CONSTRAINT ck_invoice_documents_content CHECK (size_bytes > 0 AND length(content) > 0);

                ALTER TABLE invoicing.invoice_documents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoice_documents FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoice_documents
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE TRIGGER trg_invoice_documents_guard BEFORE UPDATE OR DELETE ON invoicing.invoice_documents FOR EACH ROW EXECUTE FUNCTION invoicing.invoice_children_guard();

                CREATE OR REPLACE FUNCTION invoicing.invoices_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede borrar.' USING ERRCODE = 'FC130';
                  END IF;
                  IF (to_jsonb(NEW) - 'status' - 'updated_at') IS DISTINCT FROM (to_jsonb(OLD) - 'status' - 'updated_at') THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede modificar: solo cambia su estado.' USING ERRCODE = 'FC130';
                  END IF;
                  IF NEW.status <> OLD.status THEN
                    IF NOT (
                      (OLD.status = 'PendingSignature' AND NEW.status = 'SignedDocument') OR
                      (OLD.status = 'SignedDocument' AND NEW.status = 'PendingSubmission') OR
                      (OLD.status = 'PendingSubmission' AND NEW.status IN ('Received', 'Rejected')) OR
                      (OLD.status = 'Received' AND NEW.status IN ('Processing', 'Authorized', 'NotAuthorized')) OR
                      (OLD.status = 'Processing' AND NEW.status IN ('Authorized', 'NotAuthorized')) OR
                      (OLD.status = 'Authorized' AND NEW.status = 'Voided')) THEN
                      RAISE EXCEPTION 'Transición de estado no permitida: % -> %.', OLD.status, NEW.status USING ERRCODE = 'FC131';
                    END IF;
                    IF NEW.status = 'SignedDocument' AND NOT EXISTS (
                        SELECT 1 FROM invoicing.invoice_documents d WHERE d.invoice_id = NEW.id AND d.kind = 'SignedXml') THEN
                      RAISE EXCEPTION 'Un comprobante no puede quedar firmado sin su XML firmado.' USING ERRCODE = 'FC131';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT ON invoicing.invoice_documents TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_documents",
                schema: "invoicing");
        }
    }
}
