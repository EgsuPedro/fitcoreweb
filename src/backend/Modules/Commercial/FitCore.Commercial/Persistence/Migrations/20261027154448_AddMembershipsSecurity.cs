using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipsSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "members.memberships", "members.membership_events", "billing.payment_obligations"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantTableSecurity.AddTenantForeignKey(migrationBuilder, table, $"fk_{table.Split('.')[1]}_tenant");
            }

            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "members.memberships", "fk_memberships_branch");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "billing.payment_obligations", "fk_payment_obligations_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "members", "billing");

            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS btree_gist;

                ALTER TABLE members.memberships
                  ADD CONSTRAINT ck_memberships_status CHECK (status IN ('PendingPayment', 'Active', 'Frozen', 'Cancelled')),
                  ADD CONSTRAINT ck_memberships_dates CHECK (start_date <= end_date),
                  ADD CONSTRAINT ck_memberships_amounts CHECK (
                    plan_price > 0 AND discount_amount >= 0 AND discount_amount < plan_price
                    AND agreed_price = plan_price - discount_amount AND agreed_price > 0),
                  ADD CONSTRAINT ck_memberships_tax CHECK (tax_percent >= 0 AND tax_percent <= 100),
                  ADD CONSTRAINT ck_memberships_freeze CHECK (
                    frozen_days_used >= 0 AND frozen_days_used <= max_freeze_days AND ((status = 'Frozen') = (freeze_started_on IS NOT NULL))),
                  ADD CONSTRAINT ck_memberships_cancel CHECK ((status = 'Cancelled') = (cancel_reason IS NOT NULL)),
                  ADD CONSTRAINT ck_memberships_tax_kind CHECK (tax_kind IN ('General', 'Zero', 'NotSubject', 'Exempt')),
                  ADD CONSTRAINT ck_memberships_duration_unit CHECK (duration_unit IN ('Day', 'Week', 'Month', 'Year'));

                -- Un alumno no puede tener dos membresías pendientes, activas o congeladas con fechas que se solapan.
                -- La aplicación lo valida primero; esta restricción cubre solicitudes simultáneas.
                ALTER TABLE members.memberships
                  ADD CONSTRAINT ex_memberships_no_overlap
                  EXCLUDE USING gist (member_id WITH =, daterange(start_date, end_date, '[]') WITH &&)
                  WHERE (status IN ('PendingPayment', 'Active', 'Frozen'));

                ALTER TABLE members.membership_events
                  ADD CONSTRAINT ck_membership_events_type CHECK (type IN ('Created', 'Activated', 'Frozen', 'Reactivated', 'Cancelled', 'Renewed'));

                ALTER TABLE billing.payment_obligations
                  ADD CONSTRAINT ck_payment_obligations_status CHECK (status IN ('Pending', 'PartiallyPaid', 'Paid', 'Voided')),
                  ADD CONSTRAINT ck_payment_obligations_amounts CHECK (
                    original_amount > 0 AND discount_amount >= 0 AND discount_amount < original_amount
                    AND payable_amount = original_amount - discount_amount
                    AND subtotal >= 0 AND tax_amount >= 0 AND subtotal + tax_amount = payable_amount),
                  ADD CONSTRAINT ck_payment_obligations_tax CHECK (tax_percent >= 0 AND tax_percent <= 100),
                  ADD CONSTRAINT ck_payment_obligations_dates CHECK (due_date >= issue_date),
                  ADD CONSTRAINT ck_payment_obligations_void CHECK ((status = 'Voided') = (void_reason IS NOT NULL)),
                  ADD CONSTRAINT ck_payment_obligations_tax_kind CHECK (tax_kind IN ('General', 'Zero', 'NotSubject', 'Exempt'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE billing.payment_obligations
                  DROP CONSTRAINT IF EXISTS ck_payment_obligations_status, DROP CONSTRAINT IF EXISTS ck_payment_obligations_amounts,
                  DROP CONSTRAINT IF EXISTS ck_payment_obligations_tax, DROP CONSTRAINT IF EXISTS ck_payment_obligations_dates,
                  DROP CONSTRAINT IF EXISTS ck_payment_obligations_void, DROP CONSTRAINT IF EXISTS ck_payment_obligations_tax_kind;
                ALTER TABLE members.membership_events DROP CONSTRAINT IF EXISTS ck_membership_events_type;
                ALTER TABLE members.memberships
                  DROP CONSTRAINT IF EXISTS ex_memberships_no_overlap, DROP CONSTRAINT IF EXISTS ck_memberships_status,
                  DROP CONSTRAINT IF EXISTS ck_memberships_dates, DROP CONSTRAINT IF EXISTS ck_memberships_amounts,
                  DROP CONSTRAINT IF EXISTS ck_memberships_tax, DROP CONSTRAINT IF EXISTS ck_memberships_freeze,
                  DROP CONSTRAINT IF EXISTS ck_memberships_cancel, DROP CONSTRAINT IF EXISTS ck_memberships_tax_kind,
                  DROP CONSTRAINT IF EXISTS ck_memberships_duration_unit;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE billing.payment_obligations DROP CONSTRAINT IF EXISTS fk_payment_obligations_branch;");
            migrationBuilder.Sql("ALTER TABLE members.memberships DROP CONSTRAINT IF EXISTS fk_memberships_branch;");
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_{table.Split('.')[1]}_tenant;");
            }
        }
    }
}
