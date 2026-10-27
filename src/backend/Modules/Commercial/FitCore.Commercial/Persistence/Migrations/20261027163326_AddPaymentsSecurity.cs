using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentsSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "billing.payments", "billing.payment_allocations", "billing.idempotency_keys"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantTableSecurity.AddTenantForeignKey(migrationBuilder, table, $"fk_{table.Split('.')[1]}_tenant");
            }

            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "billing.payments", "fk_payments_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "members", "billing");

            migrationBuilder.Sql("""
                ALTER TABLE billing.payments
                  ADD CONSTRAINT ck_payments_method CHECK (method IN ('Cash', 'BankTransfer', 'Card', 'Other')),
                  ADD CONSTRAINT ck_payments_status CHECK (status IN ('PendingVerification', 'Confirmed', 'Reversed')),
                  ADD CONSTRAINT ck_payments_amount CHECK (amount > 0 AND amount <= 100000),
                  ADD CONSTRAINT ck_payments_reversal CHECK ((status = 'Reversed') = (reversal_reason IS NOT NULL)),
                  ADD CONSTRAINT ck_payments_confirmed CHECK (status <> 'Confirmed' OR confirmed_at IS NOT NULL),
                  ADD CONSTRAINT ck_payments_reference CHECK (method NOT IN ('BankTransfer', 'Card') OR (reference IS NOT NULL AND length(btrim(reference)) > 0)),
                  ADD CONSTRAINT ck_payments_method_detail CHECK (method <> 'Other' OR (method_detail IS NOT NULL AND length(btrim(method_detail)) > 0));

                ALTER TABLE billing.payment_allocations
                  ADD CONSTRAINT ck_payment_allocations_amount CHECK (amount > 0);

                -- Respaldo contra la sobreasignación: lo asignado a una obligación por pagos no revertidos nunca supera lo exigible.
                -- La aplicación bloquea la obligación antes de calcular (FOR UPDATE); esto atrapa cualquier camino que no lo haga.
                CREATE FUNCTION billing.check_allocation_within_payable() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  allocated numeric;
                  payable numeric;
                BEGIN
                  SELECT payable_amount INTO payable FROM billing.payment_obligations WHERE id = NEW.obligation_id;
                  SELECT COALESCE(SUM(a.amount), 0) INTO allocated
                    FROM billing.payment_allocations a
                    JOIN billing.payments p ON p.id = a.payment_id
                   WHERE a.obligation_id = NEW.obligation_id AND p.status <> 'Reversed';
                  IF allocated > payable THEN
                    RAISE EXCEPTION 'Las asignaciones (%) superan el importe exigible (%) de la obligacion %', allocated, payable, NEW.obligation_id
                      USING ERRCODE = '23514';
                  END IF;
                  RETURN NULL;
                END
                $fn$;

                CREATE CONSTRAINT TRIGGER trg_payment_allocations_within_payable
                  AFTER INSERT ON billing.payment_allocations
                  DEFERRABLE INITIALLY IMMEDIATE
                  FOR EACH ROW EXECUTE FUNCTION billing.check_allocation_within_payable();

                -- Los pagos son inmutables: solo cambia su estado, y solo hacia adelante (pendiente -> confirmado -> revertido).
                CREATE FUNCTION billing.guard_payment_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.member_id IS DISTINCT FROM OLD.member_id OR NEW.receipt_number IS DISTINCT FROM OLD.receipt_number
                     OR NEW.method IS DISTINCT FROM OLD.method OR NEW.method_detail IS DISTINCT FROM OLD.method_detail
                     OR NEW.amount IS DISTINCT FROM OLD.amount OR NEW.reference IS DISTINCT FROM OLD.reference
                     OR NEW.bank_name IS DISTINCT FROM OLD.bank_name OR NEW.paid_at IS DISTINCT FROM OLD.paid_at
                     OR NEW.notes IS DISTINCT FROM OLD.notes OR NEW.created_at IS DISTINCT FROM OLD.created_at
                     OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'Los datos de un pago no se pueden modificar: se corrige con un reverso' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Reversed' AND NEW.status <> 'Reversed' THEN
                    RAISE EXCEPTION 'Un pago revertido no se puede reactivar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Confirmed' AND NEW.status = 'PendingVerification' THEN
                    RAISE EXCEPTION 'Un pago confirmado no vuelve a pendiente' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER trg_payments_guard_update BEFORE UPDATE ON billing.payments
                  FOR EACH ROW EXECUTE FUNCTION billing.guard_payment_update();

                CREATE FUNCTION billing.forbid_allocation_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'Las asignaciones de un pago no se pueden modificar' USING ERRCODE = '23514';
                END
                $fn$;

                CREATE TRIGGER trg_payment_allocations_immutable BEFORE UPDATE ON billing.payment_allocations
                  FOR EACH ROW EXECUTE FUNCTION billing.forbid_allocation_update();

                -- Solo anexado para el rol de la aplicación: los pagos, sus asignaciones y el historial de membresías no se borran.
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    REVOKE DELETE ON billing.payments FROM fitcore_app;
                    REVOKE UPDATE, DELETE ON billing.payment_allocations FROM fitcore_app;
                    REVOKE UPDATE, DELETE ON members.membership_events FROM fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_payment_allocations_immutable ON billing.payment_allocations;
                DROP TRIGGER IF EXISTS trg_payments_guard_update ON billing.payments;
                DROP TRIGGER IF EXISTS trg_payment_allocations_within_payable ON billing.payment_allocations;
                DROP FUNCTION IF EXISTS billing.forbid_allocation_update();
                DROP FUNCTION IF EXISTS billing.guard_payment_update();
                DROP FUNCTION IF EXISTS billing.check_allocation_within_payable();
                ALTER TABLE billing.payment_allocations DROP CONSTRAINT IF EXISTS ck_payment_allocations_amount;
                ALTER TABLE billing.payments
                  DROP CONSTRAINT IF EXISTS ck_payments_method, DROP CONSTRAINT IF EXISTS ck_payments_status,
                  DROP CONSTRAINT IF EXISTS ck_payments_amount, DROP CONSTRAINT IF EXISTS ck_payments_reversal,
                  DROP CONSTRAINT IF EXISTS ck_payments_confirmed, DROP CONSTRAINT IF EXISTS ck_payments_reference,
                  DROP CONSTRAINT IF EXISTS ck_payments_method_detail;
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT DELETE ON billing.payments TO fitcore_app;
                    GRANT UPDATE, DELETE ON billing.payment_allocations TO fitcore_app;
                    GRANT UPDATE, DELETE ON members.membership_events TO fitcore_app;
                  END IF;
                END
                $do$;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE billing.payments DROP CONSTRAINT IF EXISTS fk_payments_branch;");
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_{table.Split('.')[1]}_tenant;");
            }
        }
    }
}
