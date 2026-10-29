using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionBillingSecurity : Migration
    {
        private static readonly string[] Tables = ["invoices", "payments", "payment_attempts", "payment_refunds"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE subscriptions.subscription_events DROP CONSTRAINT ck_subscription_events_type;
                ALTER TABLE subscriptions.subscription_events ADD CONSTRAINT ck_subscription_events_type CHECK (type IN (
                  'Created', 'TrialStarted', 'Activated', 'PastDue', 'Suspended', 'Reactivated', 'Expired', 'Cancelled',
                  'CancellationScheduled', 'CancellationResumed', 'Renewed', 'PlanChanged', 'PlanChangeScheduled', 'Extended',
                  'InvoiceIssued', 'PaymentConfirmed', 'PaymentFailed', 'InvoiceVoided', 'PaymentRefunded'));

                -- Numeración de las facturas internas (FC-AAAA-NNNNNN). Puede haber saltos si una transacción se revierte: no es un comprobante tributario.
                CREATE SEQUENCE subscriptions.invoice_seq AS bigint START 1;

                ALTER TABLE subscriptions.invoices
                  ADD CONSTRAINT fk_invoices_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_invoices_subscription FOREIGN KEY (subscription_id) REFERENCES subscriptions.subscriptions (id),
                  ADD CONSTRAINT ck_invoices_status CHECK (status IN ('Pending', 'Paid', 'Failed', 'Void', 'Refunded', 'PartiallyRefunded')),
                  ADD CONSTRAINT ck_invoices_kind CHECK (kind IN ('Activation', 'Renewal')),
                  ADD CONSTRAINT ck_invoices_currency CHECK (currency ~ '^[A-Z]{3}$'),
                  ADD CONSTRAINT ck_invoices_amount CHECK (amount > 0 AND amount <= 1000000),
                  ADD CONSTRAINT ck_invoices_refunded CHECK (refunded_amount >= 0 AND refunded_amount <= amount),
                  ADD CONSTRAINT ck_invoices_period CHECK (period_end > period_start),
                  ADD CONSTRAINT ck_invoices_attempts CHECK (attempt_count >= 0),
                  -- Pagada (o reembolsada) ⇔ tiene fecha de pago; anulada ⇔ tiene fecha y motivo; el reembolso coincide con el estado.
                  ADD CONSTRAINT ck_invoices_paid CHECK ((status IN ('Paid', 'Refunded', 'PartiallyRefunded')) = (paid_at IS NOT NULL)),
                  ADD CONSTRAINT ck_invoices_void CHECK ((status = 'Void') = (voided_at IS NOT NULL AND void_reason IS NOT NULL)),
                  ADD CONSTRAINT ck_invoices_refund_status CHECK (
                    (status = 'Refunded' AND refunded_amount = amount) OR (status = 'PartiallyRefunded' AND refunded_amount > 0 AND refunded_amount < amount)
                    OR (status NOT IN ('Refunded', 'PartiallyRefunded') AND refunded_amount = 0));

                ALTER TABLE subscriptions.payments
                  ADD CONSTRAINT fk_payments_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_payments_invoice FOREIGN KEY (invoice_id) REFERENCES subscriptions.invoices (id),
                  ADD CONSTRAINT ck_payments_method CHECK (method IN ('BankTransfer', 'Cash', 'Other', 'Card')),
                  ADD CONSTRAINT ck_payments_currency CHECK (currency ~ '^[A-Z]{3}$'),
                  ADD CONSTRAINT ck_payments_amount CHECK (amount > 0),
                  ADD CONSTRAINT ck_payments_refunded CHECK (refunded_amount >= 0 AND refunded_amount <= amount),
                  -- Un pago con tarjeta solo existe a través de un proveedor (que guarda los datos de la tarjeta); a mano nunca.
                  ADD CONSTRAINT ck_payments_card_provider CHECK (method <> 'Card' OR provider IS NOT NULL),
                  ADD CONSTRAINT ck_payments_provider_ref CHECK ((provider IS NULL) = (provider_reference IS NULL));

                ALTER TABLE subscriptions.payment_attempts
                  ADD CONSTRAINT fk_attempts_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_attempts_invoice FOREIGN KEY (invoice_id) REFERENCES subscriptions.invoices (id),
                  ADD CONSTRAINT ck_attempts_method CHECK (method IN ('BankTransfer', 'Cash', 'Other', 'Card'));

                ALTER TABLE subscriptions.payment_refunds
                  ADD CONSTRAINT fk_refunds_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_refunds_payment FOREIGN KEY (payment_id) REFERENCES subscriptions.payments (id),
                  ADD CONSTRAINT fk_refunds_invoice FOREIGN KEY (invoice_id) REFERENCES subscriptions.invoices (id),
                  ADD CONSTRAINT ck_refunds_amount CHECK (amount > 0);

                -- Una misma transferencia o comprobante no se registra dos veces.
                CREATE UNIQUE INDEX ux_payments_manual_reference ON subscriptions.payments (method, lower(reference))
                  WHERE reference IS NOT NULL AND provider IS NULL AND method <> 'Cash';

                -- El pago coincide con su factura en gimnasio, importe y moneda: la base rechaza lo demás (importe o moneda distintos, gimnasio ajeno).
                CREATE FUNCTION subscriptions.check_payment_matches_invoice() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE inv record;
                BEGIN
                  SELECT tenant_id, amount, currency INTO inv FROM subscriptions.invoices WHERE id = NEW.invoice_id;
                  IF NOT FOUND OR inv.tenant_id <> NEW.tenant_id OR inv.amount <> NEW.amount OR inv.currency <> NEW.currency THEN
                    RAISE EXCEPTION 'El pago no coincide con la factura (gimnasio, importe o moneda)' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_payments_match_invoice BEFORE INSERT ON subscriptions.payments
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.check_payment_matches_invoice();

                -- Lo emitido y lo cobrado no se reescribe. De una factura solo avanzan el estado, el pago, los intentos y lo reembolsado (y el período aplicado
                -- al pagarse); de un pago, solo lo reembolsado. Los intentos y reembolsos no cambian. Nada se borra.
                CREATE FUNCTION subscriptions.guard_invoice_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.id IS DISTINCT FROM OLD.id OR NEW.number IS DISTINCT FROM OLD.number OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.subscription_id IS DISTINCT FROM OLD.subscription_id OR NEW.kind IS DISTINCT FROM OLD.kind OR NEW.amount IS DISTINCT FROM OLD.amount
                     OR NEW.currency IS DISTINCT FROM OLD.currency OR NEW.plan_code IS DISTINCT FROM OLD.plan_code OR NEW.plan_version IS DISTINCT FROM OLD.plan_version
                     OR NEW.issued_at IS DISTINCT FROM OLD.issued_at OR NEW.due_on IS DISTINCT FROM OLD.due_on
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'Una factura emitida no cambia su número, gimnasio, importe, moneda ni plan' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
                       (OLD.status = 'Pending' AND NEW.status IN ('Paid', 'Failed', 'Void'))
                    OR (OLD.status = 'Failed' AND NEW.status IN ('Paid', 'Void'))
                    OR (OLD.status = 'Paid' AND NEW.status IN ('Refunded', 'PartiallyRefunded'))
                    OR (OLD.status = 'PartiallyRefunded' AND NEW.status = 'Refunded')) THEN
                    RAISE EXCEPTION 'Transición de factura no permitida: % -> %', OLD.status, NEW.status USING ERRCODE = '23514';
                  END IF;
                  IF NEW.refunded_amount < OLD.refunded_amount THEN
                    RAISE EXCEPTION 'Lo reembolsado no puede disminuir' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.attempt_count < OLD.attempt_count THEN
                    RAISE EXCEPTION 'Los intentos de cobro no pueden disminuir' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_invoices_guard_update BEFORE UPDATE ON subscriptions.invoices
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.guard_invoice_update();

                CREATE FUNCTION subscriptions.guard_payment_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.id IS DISTINCT FROM OLD.id OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.invoice_id IS DISTINCT FROM OLD.invoice_id
                     OR NEW.amount IS DISTINCT FROM OLD.amount OR NEW.currency IS DISTINCT FROM OLD.currency OR NEW.method IS DISTINCT FROM OLD.method
                     OR NEW.reference IS DISTINCT FROM OLD.reference OR NEW.provider IS DISTINCT FROM OLD.provider
                     OR NEW.provider_reference IS DISTINCT FROM OLD.provider_reference OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                     OR NEW.paid_at IS DISTINCT FROM OLD.paid_at OR NEW.confirmed_at IS DISTINCT FROM OLD.confirmed_at
                     OR NEW.recorded_by IS DISTINCT FROM OLD.recorded_by OR NEW.notes IS DISTINCT FROM OLD.notes THEN
                    RAISE EXCEPTION 'Un pago confirmado no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.refunded_amount < OLD.refunded_amount THEN
                    RAISE EXCEPTION 'Lo reembolsado no puede disminuir' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_payments_guard_update BEFORE UPDATE ON subscriptions.payments
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.guard_payment_update();

                CREATE TRIGGER trg_invoices_no_delete BEFORE DELETE ON subscriptions.invoices FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                CREATE TRIGGER trg_payments_no_delete BEFORE DELETE ON subscriptions.payments FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                CREATE TRIGGER trg_attempts_immutable BEFORE UPDATE OR DELETE ON subscriptions.payment_attempts FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                CREATE TRIGGER trg_refunds_immutable BEFORE UPDATE OR DELETE ON subscriptions.payment_refunds FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                """);

            // Aislamiento: cada gimnasio ve sus propias facturas y pagos; solo el contexto de plataforma escribe.
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE subscriptions.{table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE subscriptions.{table} FORCE ROW LEVEL SECURITY;
                    CREATE POLICY read_own_or_platform ON subscriptions.{table} FOR SELECT
                      USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());
                    CREATE POLICY platform_insert ON subscriptions.{table} FOR INSERT WITH CHECK (subscriptions.is_platform_context());
                    """);
            }

            migrationBuilder.Sql("""
                CREATE POLICY platform_update ON subscriptions.invoices FOR UPDATE USING (subscriptions.is_platform_context()) WITH CHECK (subscriptions.is_platform_context());
                CREATE POLICY platform_update ON subscriptions.payments FOR UPDATE USING (subscriptions.is_platform_context()) WITH CHECK (subscriptions.is_platform_context());

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON subscriptions.invoices, subscriptions.payments TO fitcore_app;
                    GRANT SELECT, INSERT ON subscriptions.payment_attempts, subscriptions.payment_refunds TO fitcore_app;
                    GRANT USAGE, SELECT ON SEQUENCE subscriptions.invoice_seq TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"""
                    DROP POLICY IF EXISTS platform_update ON subscriptions.{table};
                    DROP POLICY IF EXISTS platform_insert ON subscriptions.{table};
                    DROP POLICY IF EXISTS read_own_or_platform ON subscriptions.{table};
                    ALTER TABLE subscriptions.{table} NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE subscriptions.{table} DISABLE ROW LEVEL SECURITY;
                    """);
            }

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_refunds_immutable ON subscriptions.payment_refunds;
                DROP TRIGGER IF EXISTS trg_attempts_immutable ON subscriptions.payment_attempts;
                DROP TRIGGER IF EXISTS trg_payments_no_delete ON subscriptions.payments;
                DROP TRIGGER IF EXISTS trg_invoices_no_delete ON subscriptions.invoices;
                DROP TRIGGER IF EXISTS trg_payments_guard_update ON subscriptions.payments;
                DROP TRIGGER IF EXISTS trg_invoices_guard_update ON subscriptions.invoices;
                DROP TRIGGER IF EXISTS trg_payments_match_invoice ON subscriptions.payments;
                DROP FUNCTION IF EXISTS subscriptions.guard_payment_update();
                DROP FUNCTION IF EXISTS subscriptions.guard_invoice_update();
                DROP FUNCTION IF EXISTS subscriptions.check_payment_matches_invoice();
                DROP INDEX IF EXISTS subscriptions.ux_payments_manual_reference;
                ALTER TABLE subscriptions.payment_refunds DROP CONSTRAINT IF EXISTS ck_refunds_amount, DROP CONSTRAINT IF EXISTS fk_refunds_invoice, DROP CONSTRAINT IF EXISTS fk_refunds_payment, DROP CONSTRAINT IF EXISTS fk_refunds_tenant;
                ALTER TABLE subscriptions.payment_attempts DROP CONSTRAINT IF EXISTS ck_attempts_method, DROP CONSTRAINT IF EXISTS fk_attempts_invoice, DROP CONSTRAINT IF EXISTS fk_attempts_tenant;
                ALTER TABLE subscriptions.payments
                  DROP CONSTRAINT IF EXISTS ck_payments_provider_ref, DROP CONSTRAINT IF EXISTS ck_payments_card_provider, DROP CONSTRAINT IF EXISTS ck_payments_refunded,
                  DROP CONSTRAINT IF EXISTS ck_payments_amount, DROP CONSTRAINT IF EXISTS ck_payments_currency, DROP CONSTRAINT IF EXISTS ck_payments_method,
                  DROP CONSTRAINT IF EXISTS fk_payments_invoice, DROP CONSTRAINT IF EXISTS fk_payments_tenant;
                ALTER TABLE subscriptions.invoices
                  DROP CONSTRAINT IF EXISTS ck_invoices_refund_status, DROP CONSTRAINT IF EXISTS ck_invoices_void, DROP CONSTRAINT IF EXISTS ck_invoices_paid,
                  DROP CONSTRAINT IF EXISTS ck_invoices_attempts, DROP CONSTRAINT IF EXISTS ck_invoices_period, DROP CONSTRAINT IF EXISTS ck_invoices_refunded,
                  DROP CONSTRAINT IF EXISTS ck_invoices_amount, DROP CONSTRAINT IF EXISTS ck_invoices_currency, DROP CONSTRAINT IF EXISTS ck_invoices_kind,
                  DROP CONSTRAINT IF EXISTS ck_invoices_status, DROP CONSTRAINT IF EXISTS fk_invoices_subscription, DROP CONSTRAINT IF EXISTS fk_invoices_tenant;
                DROP SEQUENCE IF EXISTS subscriptions.invoice_seq;
                ALTER TABLE subscriptions.subscription_events DROP CONSTRAINT ck_subscription_events_type;
                ALTER TABLE subscriptions.subscription_events ADD CONSTRAINT ck_subscription_events_type CHECK (type IN (
                  'Created', 'TrialStarted', 'Activated', 'PastDue', 'Suspended', 'Reactivated', 'Expired', 'Cancelled',
                  'CancellationScheduled', 'CancellationResumed', 'Renewed', 'PlanChanged', 'PlanChangeScheduled', 'Extended'));
                """);
        }
    }
}
