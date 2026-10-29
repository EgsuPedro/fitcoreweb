using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionLifecycleSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE subscriptions.subscription_events DROP CONSTRAINT ck_subscription_events_type;
                ALTER TABLE subscriptions.subscription_events ADD CONSTRAINT ck_subscription_events_type CHECK (type IN (
                  'Created', 'TrialStarted', 'Activated', 'PastDue', 'Suspended', 'Reactivated', 'Expired', 'Cancelled',
                  'CancellationScheduled', 'CancellationResumed', 'Renewed', 'PlanChanged', 'PlanChangeScheduled', 'Extended'));

                ALTER TABLE subscriptions.subscriptions
                  ADD CONSTRAINT fk_subscriptions_pending_plan_version FOREIGN KEY (pending_plan_version_id) REFERENCES subscriptions.plan_versions (id),
                  -- Una baja de plan programada indica el plan y la versión; una cancelación programada, su motivo.
                  ADD CONSTRAINT ck_subscriptions_pending_plan CHECK ((pending_plan_id IS NULL) = (pending_plan_version_id IS NULL)),
                  ADD CONSTRAINT ck_subscriptions_scheduled_cancel CHECK (NOT cancel_at_period_end OR scheduled_cancel_reason IS NOT NULL);

                ALTER TABLE subscriptions.subscription_plan_changes
                  ADD CONSTRAINT fk_plan_changes_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_plan_changes_from_version FOREIGN KEY (from_plan_version_id) REFERENCES subscriptions.plan_versions (id),
                  ADD CONSTRAINT fk_plan_changes_to_version FOREIGN KEY (to_plan_version_id) REFERENCES subscriptions.plan_versions (id),
                  ADD CONSTRAINT ck_plan_changes_kind CHECK (kind IN ('Upgrade', 'Downgrade', 'Lateral')),
                  ADD CONSTRAINT ck_plan_changes_status CHECK (status IN ('Applied', 'Scheduled', 'Superseded')),
                  ADD CONSTRAINT ck_plan_changes_applied CHECK ((status = 'Applied') = (applied_at IS NOT NULL));

                -- Un cambio de plan registrado no se reescribe: solo avanza de «programado» a «aplicado» o «reemplazado», y nunca se borra.
                CREATE FUNCTION subscriptions.guard_plan_change_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.id IS DISTINCT FROM OLD.id OR NEW.subscription_id IS DISTINCT FROM OLD.subscription_id OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.from_plan_version_id IS DISTINCT FROM OLD.from_plan_version_id OR NEW.to_plan_id IS DISTINCT FROM OLD.to_plan_id
                     OR NEW.to_plan_version_id IS DISTINCT FROM OLD.to_plan_version_id OR NEW.kind IS DISTINCT FROM OLD.kind
                     OR NEW.requested_at IS DISTINCT FROM OLD.requested_at OR NEW.effective_on IS DISTINCT FROM OLD.effective_on
                     OR NEW.requested_by IS DISTINCT FROM OLD.requested_by OR NEW.note IS DISTINCT FROM OLD.note THEN
                    RAISE EXCEPTION 'Un cambio de plan registrado no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status <> 'Scheduled' AND NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION 'Un cambio de plan ya resuelto no cambia de estado' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_plan_changes_guard_update BEFORE UPDATE ON subscriptions.subscription_plan_changes
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.guard_plan_change_update();
                CREATE TRIGGER trg_plan_changes_no_delete BEFORE DELETE ON subscriptions.subscription_plan_changes
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();

                ALTER TABLE subscriptions.subscription_plan_changes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscription_plan_changes FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_own_or_platform ON subscriptions.subscription_plan_changes FOR SELECT
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());
                CREATE POLICY platform_insert ON subscriptions.subscription_plan_changes FOR INSERT WITH CHECK (subscriptions.is_platform_context());
                CREATE POLICY platform_update ON subscriptions.subscription_plan_changes FOR UPDATE
                  USING (subscriptions.is_platform_context()) WITH CHECK (subscriptions.is_platform_context());
                """);

            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON subscriptions.subscription_plan_changes TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS platform_update ON subscriptions.subscription_plan_changes;
                DROP POLICY IF EXISTS platform_insert ON subscriptions.subscription_plan_changes;
                DROP POLICY IF EXISTS read_own_or_platform ON subscriptions.subscription_plan_changes;
                ALTER TABLE subscriptions.subscription_plan_changes NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscription_plan_changes DISABLE ROW LEVEL SECURITY;
                DROP TRIGGER IF EXISTS trg_plan_changes_no_delete ON subscriptions.subscription_plan_changes;
                DROP TRIGGER IF EXISTS trg_plan_changes_guard_update ON subscriptions.subscription_plan_changes;
                DROP FUNCTION IF EXISTS subscriptions.guard_plan_change_update();
                ALTER TABLE subscriptions.subscription_plan_changes
                  DROP CONSTRAINT IF EXISTS ck_plan_changes_applied, DROP CONSTRAINT IF EXISTS ck_plan_changes_status, DROP CONSTRAINT IF EXISTS ck_plan_changes_kind,
                  DROP CONSTRAINT IF EXISTS fk_plan_changes_to_version, DROP CONSTRAINT IF EXISTS fk_plan_changes_from_version, DROP CONSTRAINT IF EXISTS fk_plan_changes_tenant;
                ALTER TABLE subscriptions.subscriptions
                  DROP CONSTRAINT IF EXISTS ck_subscriptions_scheduled_cancel, DROP CONSTRAINT IF EXISTS ck_subscriptions_pending_plan,
                  DROP CONSTRAINT IF EXISTS fk_subscriptions_pending_plan_version;
                ALTER TABLE subscriptions.subscription_events DROP CONSTRAINT ck_subscription_events_type;
                ALTER TABLE subscriptions.subscription_events ADD CONSTRAINT ck_subscription_events_type CHECK (type IN (
                  'Created', 'TrialStarted', 'Activated', 'PastDue', 'Suspended', 'Reactivated', 'Expired', 'Cancelled'));
                """);
        }
    }
}
