using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCashSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "cash.cash_registers", "cash.cash_sessions", "cash.cash_movements"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantTableSecurity.AddTenantForeignKey(migrationBuilder, table, $"fk_{table.Split('.')[1]}_tenant");
            }

            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "cash.cash_registers", "fk_cash_registers_branch");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "cash.cash_sessions", "fk_cash_sessions_branch");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "cash.cash_movements", "fk_cash_movements_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "members", "billing", "cash");

            migrationBuilder.Sql("""
                ALTER TABLE cash.cash_sessions
                  ADD CONSTRAINT ck_cash_sessions_status CHECK (status IN ('Open', 'Closed')),
                  ADD CONSTRAINT ck_cash_sessions_opening CHECK (opening_amount >= 0 AND opening_amount <= 100000),
                  ADD CONSTRAINT ck_cash_sessions_closed CHECK (
                    (status = 'Closed') = (closed_at IS NOT NULL AND closed_by IS NOT NULL AND counted_amount IS NOT NULL
                                           AND expected_at_close IS NOT NULL AND difference IS NOT NULL)),
                  ADD CONSTRAINT ck_cash_sessions_difference CHECK (
                    difference IS NULL OR (counted_amount >= 0 AND difference = counted_amount - expected_at_close)),
                  ADD CONSTRAINT ck_cash_sessions_difference_notes CHECK (difference IS NULL OR difference = 0 OR close_notes IS NOT NULL);

                -- Una caja exclusiva (lo normal) admite una sola sesión abierta, y cada usuario solo puede tener una abierta.
                -- La aplicación lo valida primero; estos índices deciden cuando dos aperturas llegan a la vez.
                CREATE UNIQUE INDEX ux_cash_sessions_open_exclusive ON cash.cash_sessions (register_id) WHERE status = 'Open' AND "exclusive";
                CREATE UNIQUE INDEX ux_cash_sessions_open_per_user ON cash.cash_sessions (tenant_id, opened_by) WHERE status = 'Open';

                ALTER TABLE cash.cash_movements
                  ADD CONSTRAINT ck_cash_movements_type CHECK (type IN ('CashPayment', 'PaymentReversal', 'ManualIn', 'ManualOut')),
                  ADD CONSTRAINT ck_cash_movements_direction CHECK (
                    (type IN ('CashPayment', 'ManualIn') AND direction = 'In') OR (type IN ('PaymentReversal', 'ManualOut') AND direction = 'Out')),
                  ADD CONSTRAINT ck_cash_movements_amount CHECK (amount > 0 AND amount <= 100000),
                  ADD CONSTRAINT ck_cash_movements_payment CHECK ((type IN ('CashPayment', 'PaymentReversal')) = (payment_id IS NOT NULL)),
                  ADD CONSTRAINT ck_cash_movements_reason CHECK (type = 'CashPayment' OR (reason IS NOT NULL AND length(btrim(reason)) > 0));

                -- Solo los pagos en efectivo tienen sesión de caja, y todos los pagos en efectivo la tienen.
                ALTER TABLE billing.payments
                  ADD CONSTRAINT ck_payments_cash_session CHECK ((method = 'Cash') = (cash_session_id IS NOT NULL));

                -- Respaldo: solo se mueve dinero en una sesión abierta (la aplicación ya bloquea la sesión antes de escribir).
                CREATE FUNCTION cash.require_open_session() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  session_status text;
                BEGIN
                  SELECT status INTO session_status FROM cash.cash_sessions WHERE id = NEW.session_id;
                  IF session_status IS DISTINCT FROM 'Open' THEN
                    RAISE EXCEPTION 'La sesion de caja % no esta abierta', NEW.session_id USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER trg_cash_movements_open_session BEFORE INSERT ON cash.cash_movements
                  FOR EACH ROW EXECUTE FUNCTION cash.require_open_session();

                CREATE FUNCTION cash.forbid_movement_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'Los movimientos de caja no se modifican ni se borran: se corrigen con un movimiento contrario' USING ERRCODE = '23514';
                END
                $fn$;

                CREATE TRIGGER trg_cash_movements_immutable BEFORE UPDATE OR DELETE ON cash.cash_movements
                  FOR EACH ROW EXECUTE FUNCTION cash.forbid_movement_change();

                -- Una sesión cerrada es definitiva y los datos de apertura nunca cambian.
                CREATE FUNCTION cash.guard_session_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.status = 'Closed' THEN
                    RAISE EXCEPTION 'Una sesion de caja cerrada no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.register_id IS DISTINCT FROM OLD.register_id OR NEW.opened_by IS DISTINCT FROM OLD.opened_by
                     OR NEW.opened_at IS DISTINCT FROM OLD.opened_at OR NEW.opening_amount IS DISTINCT FROM OLD.opening_amount
                     OR NEW."exclusive" IS DISTINCT FROM OLD."exclusive" THEN
                    RAISE EXCEPTION 'Los datos de apertura de una sesion de caja no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER trg_cash_sessions_guard_update BEFORE UPDATE ON cash.cash_sessions
                  FOR EACH ROW EXECUTE FUNCTION cash.guard_session_update();

                -- La inmutabilidad de los pagos ahora incluye la caja que recibió el efectivo.
                CREATE OR REPLACE FUNCTION billing.guard_payment_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.member_id IS DISTINCT FROM OLD.member_id OR NEW.receipt_number IS DISTINCT FROM OLD.receipt_number
                     OR NEW.method IS DISTINCT FROM OLD.method OR NEW.method_detail IS DISTINCT FROM OLD.method_detail
                     OR NEW.amount IS DISTINCT FROM OLD.amount OR NEW.reference IS DISTINCT FROM OLD.reference
                     OR NEW.bank_name IS DISTINCT FROM OLD.bank_name OR NEW.paid_at IS DISTINCT FROM OLD.paid_at
                     OR NEW.notes IS DISTINCT FROM OLD.notes OR NEW.created_at IS DISTINCT FROM OLD.created_at
                     OR NEW.created_by IS DISTINCT FROM OLD.created_by OR NEW.cash_session_id IS DISTINCT FROM OLD.cash_session_id THEN
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

                -- Un cobro en efectivo solo se registra contra una sesión abierta.
                CREATE FUNCTION billing.require_open_cash_session() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  session_status text;
                BEGIN
                  IF NEW.method = 'Cash' THEN
                    SELECT status INTO session_status FROM cash.cash_sessions WHERE id = NEW.cash_session_id;
                    IF session_status IS DISTINCT FROM 'Open' THEN
                      RAISE EXCEPTION 'El cobro en efectivo requiere una sesion de caja abierta' USING ERRCODE = '23514';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER trg_payments_open_cash_session BEFORE INSERT ON billing.payments
                  FOR EACH ROW EXECUTE FUNCTION billing.require_open_cash_session();

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    REVOKE DELETE ON cash.cash_registers, cash.cash_sessions FROM fitcore_app;
                    REVOKE UPDATE, DELETE ON cash.cash_movements FROM fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_payments_open_cash_session ON billing.payments;
                DROP FUNCTION IF EXISTS billing.require_open_cash_session();
                DROP TRIGGER IF EXISTS trg_cash_sessions_guard_update ON cash.cash_sessions;
                DROP FUNCTION IF EXISTS cash.guard_session_update();
                DROP TRIGGER IF EXISTS trg_cash_movements_immutable ON cash.cash_movements;
                DROP FUNCTION IF EXISTS cash.forbid_movement_change();
                DROP TRIGGER IF EXISTS trg_cash_movements_open_session ON cash.cash_movements;
                DROP FUNCTION IF EXISTS cash.require_open_session();
                ALTER TABLE billing.payments DROP CONSTRAINT IF EXISTS ck_payments_cash_session;
                ALTER TABLE cash.cash_movements
                  DROP CONSTRAINT IF EXISTS ck_cash_movements_type, DROP CONSTRAINT IF EXISTS ck_cash_movements_direction,
                  DROP CONSTRAINT IF EXISTS ck_cash_movements_amount, DROP CONSTRAINT IF EXISTS ck_cash_movements_payment,
                  DROP CONSTRAINT IF EXISTS ck_cash_movements_reason;
                DROP INDEX IF EXISTS cash.ux_cash_sessions_open_per_user;
                DROP INDEX IF EXISTS cash.ux_cash_sessions_open_exclusive;
                ALTER TABLE cash.cash_sessions
                  DROP CONSTRAINT IF EXISTS ck_cash_sessions_status, DROP CONSTRAINT IF EXISTS ck_cash_sessions_opening,
                  DROP CONSTRAINT IF EXISTS ck_cash_sessions_closed, DROP CONSTRAINT IF EXISTS ck_cash_sessions_difference,
                  DROP CONSTRAINT IF EXISTS ck_cash_sessions_difference_notes;
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT DELETE ON cash.cash_registers, cash.cash_sessions TO fitcore_app;
                    GRANT UPDATE, DELETE ON cash.cash_movements TO fitcore_app;
                  END IF;
                END
                $do$;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE cash.cash_movements DROP CONSTRAINT IF EXISTS fk_cash_movements_branch;");
            migrationBuilder.Sql("ALTER TABLE cash.cash_sessions DROP CONSTRAINT IF EXISTS fk_cash_sessions_branch;");
            migrationBuilder.Sql("ALTER TABLE cash.cash_registers DROP CONSTRAINT IF EXISTS fk_cash_registers_branch;");
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_{table.Split('.')[1]}_tenant;");
            }
        }
    }
}
