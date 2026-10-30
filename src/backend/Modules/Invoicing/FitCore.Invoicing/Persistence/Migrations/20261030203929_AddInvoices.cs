using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Invoicing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "card_payment_method",
                schema: "invoicing",
                table: "issuer_profiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Credit");

            migrationBuilder.CreateTable(
                name: "emission_sequences",
                schema: "invoicing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    emission_point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_emission_sequences", x => new { x.tenant_id, x.emission_point_id, x.document_type, x.environment });
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    emission_point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    establishment_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    point_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    sequential = table.Column<int>(type: "integer", nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    access_key = table.Column<string>(type: "character(49)", fixedLength: true, maxLength: 49, nullable: false),
                    numeric_code = table.Column<string>(type: "character(8)", fixedLength: true, maxLength: 8, nullable: false),
                    issuer_ruc = table.Column<string>(type: "character(13)", fixedLength: true, maxLength: 13, nullable: false),
                    issuer_legal_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    issuer_trade_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    issuer_head_office_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    issuer_establishment_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    tax_regime = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    accounting_required = table.Column<bool>(type: "boolean", nullable: false),
                    special_taxpayer_number = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    withholding_agent_number = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    buyer_id_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    buyer_id_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    buyer_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    buyer_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    buyer_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    buyer_phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    buyer_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    subtotal_without_taxes = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total_discount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total_tax = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    rounding_difference = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invoice_events",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_events_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    main_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false),
                    discount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tax_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    tax_rate_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    tax_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_payments",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_payments", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_payments_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_taxes",
                schema: "invoicing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    rate_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    taxable_base = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_taxes", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_taxes_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "invoicing",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_events_invoice_id_at",
                schema: "invoicing",
                table: "invoice_events",
                columns: new[] { "invoice_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_invoice_id_line_number",
                schema: "invoicing",
                table: "invoice_lines",
                columns: new[] { "invoice_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoice_payments_invoice_id",
                schema: "invoicing",
                table: "invoice_payments",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_taxes_invoice_id_tax_code_rate_code",
                schema: "invoicing",
                table: "invoice_taxes",
                columns: new[] { "invoice_id", "tax_code", "rate_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_access_key",
                schema: "invoicing",
                table: "invoices",
                column: "access_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_emission_point_id_document_type_environm",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "tenant_id", "emission_point_id", "document_type", "environment", "sequential" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_issue_date",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "tenant_id", "issue_date" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_source_type_source_id_document_type",
                schema: "invoicing",
                table: "invoices",
                columns: new[] { "tenant_id", "source_type", "source_id", "document_type" },
                unique: true);

            // Comprobantes electrónicos. Todo lo que el SRI valida o que da fe de la venta queda acotado por la base: estados, ambiente, clave de acceso (49 dígitos que deben coincidir con el
            // emisor, la numeración y la fecha del propio registro), totales coherentes y, para el consumidor final, la identificación fija. Los comprobantes y sus detalles nunca se borran ni se
            // reescriben: un disparador solo deja cambiar el estado, y el rol de la aplicación no tiene DELETE.
            migrationBuilder.Sql("""
                ALTER TABLE invoicing.issuer_profiles
                  ADD CONSTRAINT ck_issuer_profiles_card CHECK (card_payment_method IN ('Credit', 'Debit'));

                ALTER TABLE invoicing.invoices
                  ADD CONSTRAINT fk_invoices_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_invoices_emission_point FOREIGN KEY (emission_point_id) REFERENCES invoicing.emission_points (id),
                  ADD CONSTRAINT ck_invoices_document_type CHECK (document_type IN ('Invoice')),
                  ADD CONSTRAINT ck_invoices_status CHECK (status IN ('PendingSignature', 'SignedDocument', 'PendingSubmission', 'Received', 'Processing', 'Authorized', 'NotAuthorized', 'Rejected', 'Voided')),
                  ADD CONSTRAINT ck_invoices_environment CHECK (environment IN ('Test', 'Production')),
                  ADD CONSTRAINT ck_invoices_codes CHECK (establishment_code ~ '^[0-9]{3}$' AND point_code ~ '^[0-9]{3}$' AND numeric_code ~ '^[0-9]{8}$'),
                  ADD CONSTRAINT ck_invoices_sequential CHECK (sequential BETWEEN 1 AND 999999999),
                  ADD CONSTRAINT ck_invoices_currency CHECK (currency = 'USD'),
                  ADD CONSTRAINT ck_invoices_amounts CHECK (
                    subtotal_without_taxes >= 0 AND total_discount >= 0 AND total_tax >= 0 AND total > 0 AND total = subtotal_without_taxes + total_tax),
                  ADD CONSTRAINT ck_invoices_final_consumer CHECK (
                    buyer_id_type <> 'FinalConsumer' OR (buyer_id_number = '9999999999999' AND buyer_name = 'CONSUMIDOR FINAL')),
                  ADD CONSTRAINT ck_invoices_buyer_type CHECK (buyer_id_type IN ('Ruc', 'Cedula', 'Passport', 'FinalConsumer', 'Foreign')),
                  ADD CONSTRAINT ck_invoices_access_key CHECK (
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

                ALTER TABLE invoicing.invoice_lines
                  ADD CONSTRAINT fk_invoice_lines_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_invoice_lines_amounts CHECK (quantity > 0 AND unit_price >= 0 AND discount >= 0 AND subtotal >= 0 AND tax_amount >= 0 AND tax_percent >= 0);

                ALTER TABLE invoicing.invoice_taxes
                  ADD CONSTRAINT fk_invoice_taxes_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_invoice_taxes_amounts CHECK (taxable_base >= 0 AND amount >= 0 AND percent >= 0);

                ALTER TABLE invoicing.invoice_payments
                  ADD CONSTRAINT fk_invoice_payments_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_invoice_payments_amount CHECK (amount > 0);

                ALTER TABLE invoicing.invoice_events
                  ADD CONSTRAINT fk_invoice_events_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);

                ALTER TABLE invoicing.emission_sequences
                  ADD CONSTRAINT fk_emission_sequences_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_emission_sequences_point FOREIGN KEY (emission_point_id) REFERENCES invoicing.emission_points (id),
                  ADD CONSTRAINT ck_emission_sequences_value CHECK (last_value BETWEEN 0 AND 999999999);

                ALTER TABLE invoicing.invoices ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoices FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoices
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE invoicing.invoice_lines ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoice_lines FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoice_lines
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE invoicing.invoice_taxes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoice_taxes FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoice_taxes
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE invoicing.invoice_payments ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoice_payments FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoice_payments
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE invoicing.invoice_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.invoice_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.invoice_events
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE invoicing.emission_sequences ENABLE ROW LEVEL SECURITY;
                ALTER TABLE invoicing.emission_sequences FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON invoicing.emission_sequences
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                -- Un comprobante solo cambia de estado: todo lo demás (emisor, comprador, numeración, líneas, impuestos, totales) es inmutable. Sus detalles y eventos no se editan ni se borran.
                CREATE FUNCTION invoicing.invoices_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede borrar.' USING ERRCODE = 'FC130';
                  END IF;
                  IF (to_jsonb(NEW) - 'status' - 'updated_at') IS DISTINCT FROM (to_jsonb(OLD) - 'status' - 'updated_at') THEN
                    RAISE EXCEPTION 'Un comprobante electrónico no se puede modificar: solo cambia su estado.' USING ERRCODE = 'FC130';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_invoices_guard BEFORE UPDATE OR DELETE ON invoicing.invoices FOR EACH ROW EXECUTE FUNCTION invoicing.invoices_guard();

                CREATE FUNCTION invoicing.invoice_children_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'El detalle de un comprobante electrónico no se puede modificar ni borrar.' USING ERRCODE = 'FC130';
                END
                $fn$;
                CREATE TRIGGER trg_invoice_lines_guard BEFORE UPDATE OR DELETE ON invoicing.invoice_lines FOR EACH ROW EXECUTE FUNCTION invoicing.invoice_children_guard();
                CREATE TRIGGER trg_invoice_taxes_guard BEFORE UPDATE OR DELETE ON invoicing.invoice_taxes FOR EACH ROW EXECUTE FUNCTION invoicing.invoice_children_guard();
                CREATE TRIGGER trg_invoice_payments_guard BEFORE UPDATE OR DELETE ON invoicing.invoice_payments FOR EACH ROW EXECUTE FUNCTION invoicing.invoice_children_guard();
                CREATE TRIGGER trg_invoice_events_guard BEFORE UPDATE OR DELETE ON invoicing.invoice_events FOR EACH ROW EXECUTE FUNCTION invoicing.invoice_children_guard();

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON invoicing.invoices, invoicing.emission_sequences TO fitcore_app;
                    GRANT SELECT, INSERT ON invoicing.invoice_lines, invoicing.invoice_taxes, invoicing.invoice_payments, invoicing.invoice_events TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "emission_sequences",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "invoice_events",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "invoice_payments",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "invoice_taxes",
                schema: "invoicing");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "invoicing");

            migrationBuilder.DropColumn(
                name: "card_payment_method",
                schema: "invoicing",
                table: "issuer_profiles");
        }
    }
}
