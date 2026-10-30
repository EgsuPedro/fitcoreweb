using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Invoicing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "authorization_number",
                schema: "invoicing",
                table: "invoices",
                type: "character varying(49)",
                maxLength: 49,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "authorized_at",
                schema: "invoicing",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "invoice_sri_attempts",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sri_state = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    messages = table.Column<string>(type: "jsonb", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_sri_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_sri_attempts_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_transmissions",
                schema: "invoicing",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    parked = table.Column<bool>(type: "boolean", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    polls = table.Column<int>(type: "integer", nullable: false),
                    first_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_transmissions", x => x.invoice_id);
                    table.ForeignKey(
                        name: "fk_invoice_transmissions_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_sri_attempts_invoice_id_at",
                schema: "invoicing",
                table: "invoice_sri_attempts",
                columns: new[] { "invoice_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_transmissions_next_attempt_at",
                schema: "invoicing",
                table: "invoice_transmissions",
                column: "next_attempt_at",
                filter: "NOT parked");

            // Transmisión al SRI: la autorización queda en el comprobante (se fija una sola vez y solo al autorizarse), la cola de trabajo es mutable (una fila por comprobante mientras está en curso),
            // los intentos son una constancia inmutable y el XML autorizado es un documento más. La base también exige que «autorizado» tenga número, fecha y su XML autorizado, y habilita el reenvío
            // de un comprobante devuelto (el SRI permite reenviarlo sin cambiar clave ni secuencial).
            migrationBuilder.Sql("""
                ALTER TABLE invoicing.invoices
                  ADD CONSTRAINT ck_invoices_authorization CHECK (
                    (status <> 'Authorized' OR (authorization_number IS NOT NULL AND authorized_at IS NOT NULL))
                    AND (authorization_number IS NULL OR status IN ('Authorized', 'Voided')));

                ALTER TABLE invoicing.invoice_documents DROP CONSTRAINT ck_invoice_documents_kind;
                ALTER TABLE invoicing.invoice_documents ADD CONSTRAINT ck_invoice_documents_kind CHECK (kind IN ('Xml', 'SignedXml', 'AuthorizedXml'));

                ALTER TABLE invoicing.invoice_transmissions
                  ADD CONSTRAINT fk_invoice_transmissions_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_invoice_transmissions_counters CHECK (attempts >= 0 AND polls >= 0);

                ALTER TABLE invoicing.invoice_sri_attempts
                  ADD CONSTRAINT fk_invoice_sri_attempts_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_invoice_sri_attempts_operation CHECK (operation IN ('Reception', 'Authorization')),
                  ADD CONSTRAINT ck_invoice_sri_attempts_outcome CHECK (outcome IN ('Received', 'Returned', 'Authorized', 'NotAuthorized', 'Processing', 'NotFound', 'Unknown', 'Unavailable', 'SignFailed')),
                  ADD CONSTRAINT ck_invoice_sri_attempts_duration CHECK (duration_ms >= 0);

                ALTER TABLE invoicing.invoice_transmissions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoice_transmissions FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoice_transmissions
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE invoicing.invoice_sri_attempts ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoice_sri_attempts FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoice_sri_attempts
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE TRIGGER trg_invoice_sri_attempts_guard BEFORE UPDATE OR DELETE ON invoicing.invoice_sri_attempts FOR EACH ROW EXECUTE FUNCTION invoicing.invoice_children_guard();

                CREATE OR REPLACE FUNCTION invoicing.invoices_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede borrar.' USING ERRCODE = 'FC130';
                  END IF;
                  IF (to_jsonb(NEW) - 'status' - 'updated_at' - 'authorization_number' - 'authorized_at')
                     IS DISTINCT FROM (to_jsonb(OLD) - 'status' - 'updated_at' - 'authorization_number' - 'authorized_at') THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede modificar: solo cambia su estado.' USING ERRCODE = 'FC130';
                  END IF;
                  -- La autorización se fija una sola vez.
                  IF (OLD.authorization_number IS NOT NULL AND NEW.authorization_number IS DISTINCT FROM OLD.authorization_number)
                     OR (OLD.authorized_at IS NOT NULL AND NEW.authorized_at IS DISTINCT FROM OLD.authorized_at) THEN
                    RAISE EXCEPTION 'La autorización de un comprobante no se puede cambiar.' USING ERRCODE = 'FC130';
                  END IF;
                  IF NEW.status <> OLD.status THEN
                    IF NOT (
                      (OLD.status = 'PendingSignature' AND NEW.status = 'SignedDocument') OR
                      (OLD.status = 'SignedDocument' AND NEW.status = 'PendingSubmission') OR
                      (OLD.status = 'PendingSubmission' AND NEW.status IN ('Received', 'Rejected')) OR
                      (OLD.status = 'Rejected' AND NEW.status = 'PendingSubmission') OR
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

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE ON invoicing.invoice_transmissions TO fitcore_app;
                    GRANT SELECT, INSERT ON invoicing.invoice_sri_attempts TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_sri_attempts",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "invoice_transmissions",
                schema: "invoicing");

            migrationBuilder.DropColumn(
                name: "authorization_number",
                schema: "invoicing",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "authorized_at",
                schema: "invoicing",
                table: "invoices");
        }
    }
}
