using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlansSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "members.membership_plans", "members.plan_branches", "members.promotions",
            "members.promotion_plans", "members.commercial_settings"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantTableSecurity.AddTenantForeignKey(migrationBuilder, table, $"fk_{table.Split('.')[1]}_tenant");
            }

            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "members.plan_branches", "fk_plan_branches_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "members", "billing");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_membership_plans_tenant_name ON members.membership_plans (tenant_id, lower(name));

                ALTER TABLE members.membership_plans
                  ADD CONSTRAINT ck_membership_plans_price CHECK (price > 0 AND price <= 100000),
                  ADD CONSTRAINT ck_membership_plans_duration CHECK (duration_value >= 1),
                  ADD CONSTRAINT ck_membership_plans_freeze CHECK (max_freeze_days >= 0 AND (allow_freeze OR max_freeze_days = 0)),
                  ADD CONSTRAINT ck_membership_plans_validity CHECK (valid_until IS NULL OR valid_from IS NULL OR valid_until >= valid_from),
                  ADD CONSTRAINT ck_membership_plans_duration_unit CHECK (duration_unit IN ('Day', 'Week', 'Month', 'Year')),
                  ADD CONSTRAINT ck_membership_plans_tax_kind CHECK (tax_kind IN ('General', 'Zero', 'NotSubject', 'Exempt'));

                ALTER TABLE members.promotions
                  ADD CONSTRAINT ck_promotions_value CHECK (value > 0 AND (discount_type <> 'Percentage' OR value <= 100)),
                  ADD CONSTRAINT ck_promotions_validity CHECK (valid_until >= valid_from),
                  ADD CONSTRAINT ck_promotions_uses CHECK (used_count >= 0 AND (max_uses IS NULL OR (max_uses >= 1 AND used_count <= max_uses))),
                  ADD CONSTRAINT ck_promotions_discount_type CHECK (discount_type IN ('Percentage', 'FixedAmount'));

                ALTER TABLE members.commercial_settings
                  ADD CONSTRAINT ck_commercial_settings_policy CHECK (access_policy IN ('RequireFullPayment', 'AllowUntilOverdue', 'AllowWithMinimumPayment')),
                  ADD CONSTRAINT ck_commercial_settings_ranges CHECK (
                    min_payment_percent BETWEEN 1 AND 100 AND default_due_days BETWEEN 0 AND 90 AND max_discount_percent BETWEEN 0 AND 100);

                -- Una sola tarifa abierta por tipo de impuesto: dos decretos simultáneos no pueden dejar tarifas superpuestas.
                CREATE UNIQUE INDEX ux_tax_rates_open ON billing.tax_rates (kind) WHERE valid_until IS NULL;
                ALTER TABLE billing.tax_rates
                  ADD CONSTRAINT ck_tax_rates_percent CHECK (percent >= 0 AND percent <= 100),
                  ADD CONSTRAINT ck_tax_rates_validity CHECK (valid_until IS NULL OR valid_until >= valid_from),
                  ADD CONSTRAINT ck_tax_rates_kind CHECK (kind IN ('General', 'Zero', 'NotSubject', 'Exempt'));

                -- Tarifas iniciales. El IVA general es 15 % (confirmado con el contador del cliente, ADR 0003); los cambios
                -- por decreto se registran con POST /platform/tax-rates. La fecha de inicio solo marca el arranque del sistema.
                INSERT INTO billing.tax_rates (id, kind, percent, valid_from, valid_until, created_at) VALUES
                  ('0198f0a0-0000-7000-8000-000000000001', 'General', 15, DATE '2000-01-01', NULL, now()),
                  ('0198f0a0-0000-7000-8000-000000000002', 'Zero', 0, DATE '2000-01-01', NULL, now()),
                  ('0198f0a0-0000-7000-8000-000000000003', 'NotSubject', 0, DATE '2000-01-01', NULL, now()),
                  ('0198f0a0-0000-7000-8000-000000000004', 'Exempt', 0, DATE '2000-01-01', NULL, now());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM billing.tax_rates WHERE id::text LIKE '0198f0a0-0000-7000-8000-00000000000%';
                DROP INDEX IF EXISTS billing.ux_tax_rates_open;
                ALTER TABLE billing.tax_rates DROP CONSTRAINT IF EXISTS ck_tax_rates_percent, DROP CONSTRAINT IF EXISTS ck_tax_rates_validity, DROP CONSTRAINT IF EXISTS ck_tax_rates_kind;
                ALTER TABLE members.commercial_settings DROP CONSTRAINT IF EXISTS ck_commercial_settings_policy, DROP CONSTRAINT IF EXISTS ck_commercial_settings_ranges;
                ALTER TABLE members.promotions DROP CONSTRAINT IF EXISTS ck_promotions_value, DROP CONSTRAINT IF EXISTS ck_promotions_validity, DROP CONSTRAINT IF EXISTS ck_promotions_uses, DROP CONSTRAINT IF EXISTS ck_promotions_discount_type;
                ALTER TABLE members.membership_plans DROP CONSTRAINT IF EXISTS ck_membership_plans_price, DROP CONSTRAINT IF EXISTS ck_membership_plans_duration, DROP CONSTRAINT IF EXISTS ck_membership_plans_freeze, DROP CONSTRAINT IF EXISTS ck_membership_plans_validity, DROP CONSTRAINT IF EXISTS ck_membership_plans_duration_unit, DROP CONSTRAINT IF EXISTS ck_membership_plans_tax_kind;
                DROP INDEX IF EXISTS members.ux_membership_plans_tenant_name;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE members.plan_branches DROP CONSTRAINT IF EXISTS fk_plan_branches_branch;");
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_{table.Split('.')[1]}_tenant;");
            }
        }
    }
}
