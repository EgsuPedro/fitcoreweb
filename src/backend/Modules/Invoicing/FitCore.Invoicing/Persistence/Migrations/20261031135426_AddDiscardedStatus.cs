using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Invoicing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscardedStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "tenant_id", "source_type", "source_id", "document_type" },
                unique: true,
                filter: "status <> 'Discarded'");

            migrationBuilder.Sql("""
                ALTER TABLE invoicing.invoices DROP CONSTRAINT ck_invoices_status;
                ALTER TABLE invoicing.invoices ADD CONSTRAINT ck_invoices_status CHECK (status IN ('PendingSignature', 'SignedDocument', 'PendingSubmission', 'Received', 'Processing', 'Authorized', 'NotAuthorized', 'Rejected', 'Voided', 'Discarded'));

                CREATE OR REPLACE FUNCTION invoicing.invoices_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede borrar.' USING ERRCODE = 'FC130';
                  END IF;
                  IF (to_jsonb(NEW) - 'status' - 'updated_at' - 'authorization_number' - 'authorized_at')
                     IS DISTINCT FROM (to_jsonb(OLD) - 'status' - 'updated_at' - 'authorization_number' - 'authorized_at') THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede modificar: solo cambia su estado.' USING ERRCODE = 'FC130';
                  END IF;
                  IF (OLD.authorization_number IS NOT NULL AND NEW.authorization_number IS DISTINCT FROM OLD.authorization_number)
                     OR (OLD.authorized_at IS NOT NULL AND NEW.authorized_at IS DISTINCT FROM OLD.authorized_at) THEN
                    RAISE EXCEPTION 'La autorización de un comprobante no se puede cambiar.' USING ERRCODE = 'FC130';
                  END IF;
                  IF NEW.status <> OLD.status THEN
                    IF NOT (
                      (OLD.status = 'PendingSignature' AND NEW.status IN ('SignedDocument', 'Discarded')) OR
                      (OLD.status = 'SignedDocument' AND NEW.status IN ('PendingSubmission', 'Discarded')) OR
                      (OLD.status = 'PendingSubmission' AND NEW.status IN ('Received', 'Rejected')) OR
                      (OLD.status = 'Rejected' AND NEW.status IN ('PendingSubmission', 'Discarded')) OR
                      (OLD.status = 'Received' AND NEW.status IN ('Processing', 'Authorized', 'NotAuthorized')) OR
                      (OLD.status = 'Processing' AND NEW.status IN ('Authorized', 'NotAuthorized')) OR
                      (OLD.status = 'NotAuthorized' AND NEW.status IN ('Discarded')) OR
                      (OLD.status = 'Authorized' AND NEW.status = 'Voided')) THEN
                      RAISE EXCEPTION 'Transición de estado no permitida: % -> %.', OLD.status, NEW.status USING ERRCODE = 'FC131';
                    END IF;
                    IF NEW.status = 'SignedDocument' AND NOT EXISTS (
                        SELECT 1 FROM invoicing.invoice_documents d WHERE d.invoice_id = NEW.id AND d.kind = 'SignedXml') THEN
                      RAISE EXCEPTION 'Un comprobante no puede quedar firmado sin su XML firmado.' USING ERRCODE = 'FC131';
                    END IF;
                    IF NEW.status = 'Authorized' AND NOT EXISTS (
                        SELECT 1 FROM invoicing.invoice_documents d WHERE d.invoice_id = NEW.id AND d.kind = 'AuthorizedXml') THEN
                      RAISE EXCEPTION 'Un comprobante no puede quedar autorizado sin el XML de autorización del SRI.' USING ERRCODE = 'FC131';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "tenant_id", "source_type", "source_id", "document_type" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE invoicing.invoices DROP CONSTRAINT ck_invoices_status;
                ALTER TABLE invoicing.invoices ADD CONSTRAINT ck_invoices_status CHECK (status IN ('PendingSignature', 'SignedDocument', 'PendingSubmission', 'Received', 'Processing', 'Authorized', 'NotAuthorized', 'Rejected', 'Voided'));

                CREATE OR REPLACE FUNCTION invoicing.invoices_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede borrar.' USING ERRCODE = 'FC130';
                  END IF;
                  IF (to_jsonb(NEW) - 'status' - 'updated_at' - 'authorization_number' - 'authorized_at')
                     IS DISTINCT FROM (to_jsonb(OLD) - 'status' - 'updated_at' - 'authorization_number' - 'authorized_at') THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede modificar: solo cambia su estado.' USING ERRCODE = 'FC130';
                  END IF;
                  IF (OLD.authorization_number IS NOT NULL AND NEW.authorization_number IS DISTINCT FROM OLD.authorization_number)
                     OR (OLD.authorized_at IS NOT NULL AND NEW.authorized_at IS DISTINCT FROM OLD.authorized_at) THEN
                    RAISE EXCEPTION 'La autorización de un comprobante no se puede cambiar.' USING ERRCODE = 'FC130';
                  END IF;
                  IF NEW.status <> OLD.status THEN
                    IF NOT (
                      (OLD.status = 'PendingSignature' AND NEW.status IN ('SignedDocument')) OR
                      (OLD.status = 'SignedDocument' AND NEW.status IN ('PendingSubmission')) OR
                      (OLD.status = 'PendingSubmission' AND NEW.status IN ('Received', 'Rejected')) OR
                      (OLD.status = 'Rejected' AND NEW.status IN ('PendingSubmission')) OR
                      (OLD.status = 'Received' AND NEW.status IN ('Processing', 'Authorized', 'NotAuthorized')) OR
                      (OLD.status = 'Processing' AND NEW.status IN ('Authorized', 'NotAuthorized')) OR
                      (OLD.status = 'Authorized' AND NEW.status = 'Voided')) THEN
                      RAISE EXCEPTION 'Transición de estado no permitida: % -> %.', OLD.status, NEW.status USING ERRCODE = 'FC131';
                    END IF;
                    IF NEW.status = 'SignedDocument' AND NOT EXISTS (
                        SELECT 1 FROM invoicing.invoice_documents d WHERE d.invoice_id = NEW.id AND d.kind = 'SignedXml') THEN
                      RAISE EXCEPTION 'Un comprobante no puede quedar firmado sin su XML firmado.' USING ERRCODE = 'FC131';
                    END IF;
                    IF NEW.status = 'Authorized' AND NOT EXISTS (
                        SELECT 1 FROM invoicing.invoice_documents d WHERE d.invoice_id = NEW.id AND d.kind = 'AuthorizedXml') THEN
                      RAISE EXCEPTION 'Un comprobante no puede quedar autorizado sin el XML de autorización del SRI.' USING ERRCODE = 'FC131';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """);
        }
    }
}
