using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedLegacyPlan : Migration
    {
        private const string PlanId = "0198f0a0-0000-7000-8000-0000000000a1";
        private const string VersionId = "0198f0a0-0000-7000-8000-0000000000a2";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El plan «legacy» recibe a los gimnasios que ya existían antes de los planes comerciales: todas las funciones, sin límites y
            // sin cobro (precio 0). No se vende (nace inactivo) y se usa como plan por defecto cuando un alta no elige otro.
            var features = string.Join(", ", Capabilities.FeatureCatalog.Select(f => $"('{VersionId}', '{f.Code}')"));
            var limits = string.Join(", ", Capabilities.LimitCatalog.Select(l => $"('{VersionId}', '{l.Code}', NULL)"));
            migrationBuilder.Sql($"""
                INSERT INTO subscriptions.plans (id, code, name, description, rules, is_active, created_at)
                VALUES ('{PlanId}', 'legacy', 'Plan heredado',
                        'Gimnasios que ya existían antes de los planes comerciales: todas las funciones, sin límites y sin cobro. No se vende.',
                        NULL, false, now());
                INSERT INTO subscriptions.plan_versions (id, plan_id, number, currency, period, price, trial_days, grace_days, change_note, created_at)
                VALUES ('{VersionId}', '{PlanId}', 1, 'USD', 'Yearly', 0, 0, NULL, 'Versión inicial', now());
                INSERT INTO subscriptions.plan_features (plan_version_id, code) VALUES {features};
                INSERT INTO subscriptions.plan_limits (plan_version_id, code, value) VALUES {limits};
                """);

            // Cada gimnasio existente (menos la plataforma) queda con una suscripción activa en el plan heredado. La política de la base solo
            // deja escribir desde el contexto de plataforma, así que se fija ese contexto mientras dura la migración.
            migrationBuilder.Sql($"""
                DO $do$
                DECLARE p uuid; t record; sid uuid;
                BEGIN
                  SELECT id INTO p FROM organizations.tenants WHERE slug = 'platform';
                  IF p IS NULL THEN
                    RETURN; -- Base nueva: los gimnasios se crearán después, ya con su plan.
                  END IF;
                  PERFORM set_config('app.tenant_id', p::text, true);
                  FOR t IN SELECT id FROM organizations.tenants WHERE slug <> 'platform' LOOP
                    sid := gen_random_uuid();
                    INSERT INTO subscriptions.subscriptions (
                      id, tenant_id, plan_id, plan_version_id, plan_version_number, plan_code, plan_name, price, currency, period, status,
                      started_at, current_period_start, current_period_end, trial_ends_on, next_due_on, cancel_at_period_end, created_at)
                    VALUES (sid, t.id, '{PlanId}', '{VersionId}', 1, 'legacy', 'Plan heredado', 0, 'USD', 'Yearly', 'Active',
                      now(), current_date, (current_date + interval '1 year')::date, NULL, (current_date + interval '1 year')::date, false, now());
                    INSERT INTO subscriptions.subscription_events (id, subscription_id, tenant_id, type, occurred_at, from_status, to_status, detail)
                    VALUES (gen_random_uuid(), sid, t.id, 'Created', now(), NULL, 'Active', 'legacy v1 · Migración: gimnasio existente asignado al plan heredado');
                  END LOOP;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El historial es inmutable y la base impide borrarlo: revertir esta migración obliga a desactivar esos disparadores a propósito.
            migrationBuilder.Sql($"""
                ALTER TABLE subscriptions.subscription_events DISABLE TRIGGER trg_subscription_events_immutable;
                ALTER TABLE subscriptions.subscriptions DISABLE TRIGGER trg_subscriptions_no_delete;
                ALTER TABLE subscriptions.plan_versions DISABLE TRIGGER trg_plan_versions_immutable;
                ALTER TABLE subscriptions.plan_features DISABLE TRIGGER trg_plan_features_immutable;
                ALTER TABLE subscriptions.plan_limits DISABLE TRIGGER trg_plan_limits_immutable;
                ALTER TABLE subscriptions.plans DISABLE TRIGGER trg_plans_no_delete;
                DELETE FROM subscriptions.subscription_events WHERE subscription_id IN (SELECT id FROM subscriptions.subscriptions WHERE plan_id = '{PlanId}');
                DELETE FROM subscriptions.subscriptions WHERE plan_id = '{PlanId}';
                DELETE FROM subscriptions.plan_limits WHERE plan_version_id = '{VersionId}';
                DELETE FROM subscriptions.plan_features WHERE plan_version_id = '{VersionId}';
                DELETE FROM subscriptions.plan_versions WHERE id = '{VersionId}';
                DELETE FROM subscriptions.plans WHERE id = '{PlanId}';
                ALTER TABLE subscriptions.plans ENABLE TRIGGER trg_plans_no_delete;
                ALTER TABLE subscriptions.plan_limits ENABLE TRIGGER trg_plan_limits_immutable;
                ALTER TABLE subscriptions.plan_features ENABLE TRIGGER trg_plan_features_immutable;
                ALTER TABLE subscriptions.plan_versions ENABLE TRIGGER trg_plan_versions_immutable;
                ALTER TABLE subscriptions.subscriptions ENABLE TRIGGER trg_subscriptions_no_delete;
                ALTER TABLE subscriptions.subscription_events ENABLE TRIGGER trg_subscription_events_immutable;
                """);
        }
    }
}
