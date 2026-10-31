using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Invoicing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.AddColumn<string>(
                name: "modification_reason",
                schema: "invoicing",
                table: "invoices",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "modified_document_number",
                schema: "invoicing",
                table: "invoices",
                type: "character varying(17)",
                maxLength: 17,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "modified_invoice_id",
                schema: "invoicing",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "modified_issue_date",
                schema: "invoicing",
                table: "invoices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "modified_line_id",
                schema: "invoicing",
                table: "invoice_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_modified_invoice_id",
                schema: "invoicing",
                table: "invoices",
                column: "modified_invoice_id",
                filter: "modified_invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "tenant_id", "source_type", "source_id", "document_type" },
                unique: true,
                filter: "status <> 'Discarded' AND document_type = 'Invoice'");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_modified_line_id",
                schema: "invoicing",
                table: "invoice_lines",
                column: "modified_line_id",
                filter: "modified_line_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_invoice_lines_invoice_lines_modified_line_id",
                schema: "invoicing",
                table: "invoice_lines",
                column: "modified_line_id",
                principalSchema: "invoicing",
                principalTable: "invoice_lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_invoices_invoices_modified_invoice_id",
                schema: "invoicing",
                table: "invoices",
                column: "modified_invoice_id",
                principalSchema: "invoicing",
                principalTable: "invoices",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE invoicing.invoices DROP CONSTRAINT ck_invoices_document_type;
                ALTER TABLE invoicing.invoices ADD CONSTRAINT ck_invoices_document_type CHECK (document_type IN ('Invoice', 'CreditNote'));

                -- La clave de acceso lleva el código del tipo de comprobante: 01 factura, 04 nota de crédito.
                ALTER TABLE invoicing.invoices DROP CONSTRAINT ck_invoices_access_key;
                ALTER TABLE invoicing.invoices ADD CONSTRAINT ck_invoices_access_key CHECK (
                  access_key ~ '^[0-9]{49}$'
                  AND substr(access_key, 1, 8) = to_char(issue_date, 'DDMMYYYY')
                  AND substr(access_key, 9, 2) = CASE document_type WHEN 'CreditNote' THEN '04' ELSE '01' END
                  AND substr(access_key, 11, 13) = issuer_ruc
                  AND substr(access_key, 24, 1) = CASE environment WHEN 'Production' THEN '2' ELSE '1' END
                  AND substr(access_key, 25, 3) = establishment_code
                  AND substr(access_key, 28, 3) = point_code
                  AND substr(access_key, 31, 9) = lpad(sequential::text, 9, '0')
                  AND substr(access_key, 40, 8) = numeric_code
                  AND substr(access_key, 48, 1) = '1');

                -- El SRI exige identificar al receptor de una nota de crédito: nunca consumidor final.
                ALTER TABLE invoicing.invoices ADD CONSTRAINT ck_invoices_credit_note_buyer CHECK (document_type <> 'CreditNote' OR buyer_id_type <> 'FinalConsumer');

                -- Una nota de crédito siempre dice qué factura modifica y por qué; una factura nunca.
                ALTER TABLE invoicing.invoices ADD CONSTRAINT ck_invoices_credit_note CHECK (
                  (document_type = 'CreditNote') = (modified_invoice_id IS NOT NULL AND modified_document_number IS NOT NULL AND modified_issue_date IS NOT NULL AND modification_reason IS NOT NULL)
                  AND (document_type = 'CreditNote' OR modified_invoice_id IS NULL));

                -- Solo se puede emitir una nota de crédito sobre una factura AUTORIZADA, del mismo gimnasio y del mismo ambiente.
                CREATE FUNCTION invoicing.credit_note_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE original invoicing.invoices%ROWTYPE;
                BEGIN
                  IF NEW.document_type = 'CreditNote' THEN
                    SELECT * INTO original FROM invoicing.invoices WHERE id = NEW.modified_invoice_id;
                    IF NOT FOUND OR original.document_type <> 'Invoice' OR original.tenant_id <> NEW.tenant_id OR original.status <> 'Authorized' OR original.environment <> NEW.environment THEN
                      RAISE EXCEPTION 'Una nota de crédito solo puede modificar una factura autorizada del mismo gimnasio y ambiente.' USING ERRCODE = 'FC132';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_invoices_credit_note_guard BEFORE INSERT ON invoicing.invoices FOR EACH ROW EXECUTE FUNCTION invoicing.credit_note_guard();

                -- Una línea de nota de crédito acredita una línea de la factura que modifica, y todas las notas vigentes juntas no pueden superar su base.
                CREATE FUNCTION invoicing.credit_note_line_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  original_line invoicing.invoice_lines%ROWTYPE;
                  expected_invoice uuid;
                  already numeric;
                BEGIN
                  IF NEW.modified_line_id IS NOT NULL THEN
                    SELECT * INTO original_line FROM invoicing.invoice_lines WHERE id = NEW.modified_line_id;
                    SELECT modified_invoice_id INTO expected_invoice FROM invoicing.invoices WHERE id = NEW.invoice_id;
                    IF NOT FOUND OR original_line.id IS NULL OR expected_invoice IS NULL OR original_line.invoice_id <> expected_invoice THEN
                      RAISE EXCEPTION 'La línea acreditada no pertenece a la factura que modifica la nota de crédito.' USING ERRCODE = 'FC132';
                    END IF;
                    SELECT COALESCE(SUM(l.subtotal), 0) INTO already
                      FROM invoicing.invoice_lines l JOIN invoicing.invoices i ON i.id = l.invoice_id
                      WHERE l.modified_line_id = NEW.modified_line_id AND i.status NOT IN ('Discarded', 'NotAuthorized');
                    IF already + NEW.subtotal > original_line.subtotal THEN
                      RAISE EXCEPTION 'Las notas de crédito de una línea no pueden superar su valor.' USING ERRCODE = 'FC132';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_invoice_lines_credit_note_guard BEFORE INSERT ON invoicing.invoice_lines FOR EACH ROW EXECUTE FUNCTION invoicing.credit_note_line_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_invoice_lines_credit_note_guard ON invoicing.invoice_lines;
                DROP FUNCTION IF EXISTS invoicing.credit_note_line_guard();
                DROP TRIGGER IF EXISTS trg_invoices_credit_note_guard ON invoicing.invoices;
                DROP FUNCTION IF EXISTS invoicing.credit_note_guard();
                ALTER TABLE invoicing.invoices DROP CONSTRAINT IF EXISTS ck_invoices_credit_note_buyer;
                ALTER TABLE invoicing.invoices DROP CONSTRAINT IF EXISTS ck_invoices_credit_note;
                ALTER TABLE invoicing.invoices DROP CONSTRAINT ck_invoices_access_key;
                ALTER TABLE invoicing.invoices ADD CONSTRAINT ck_invoices_access_key CHECK (
                  access_key ~ '^[0-9]{49}$'
                  AND substr(access_key, 1, 8) = to_char(issue_date, 'DDMMYYYY')
                  AND substr(access_key, 9, 2) = '01'
                  AND substr(access_key, 11, 13) = issuer_ruc
                  AND substr(access_key, 24, 1) = CASE environment WHEN 'Production' THEN '2' ELSE '1' END
                  AND substr(access_key, 25, 3) = establishment_code
                  AND substr(access_key, 28, 3) = point_code
                  AND substr(access_key, 31, 9) = lpad(sequential::text, 9, '0')
                  AND substr(access_key, 40, 8) = numeric_code
                  AND substr(access_key, 48, 1) = '1');
                ALTER TABLE invoicing.invoices DROP CONSTRAINT ck_invoices_document_type;
                ALTER TABLE invoicing.invoices ADD CONSTRAINT ck_invoices_document_type CHECK (document_type IN ('Invoice'));
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_invoice_lines_invoice_lines_modified_line_id",
                schema: "invoicing",
                table: "invoice_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_invoices_invoices_modified_invoice_id",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoices_modified_invoice_id",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoice_lines_modified_line_id",
                schema: "invoicing",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "modification_reason",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "modified_document_number",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "modified_invoice_id",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "modified_issue_date",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "modified_line_id",
                schema: "invoicing",
                table: "invoice_lines");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "tenant_id", "source_type", "source_id", "document_type" },
                unique: true,
                filter: "status <> 'Discarded'");
        }
    }
}
