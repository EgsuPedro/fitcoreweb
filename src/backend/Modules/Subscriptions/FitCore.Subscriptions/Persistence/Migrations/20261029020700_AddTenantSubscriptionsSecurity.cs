using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSubscriptionsSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE subscriptions.subscriptions ADD CONSTRAINT fk_subscriptions_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);
                ALTER TABLE subscriptions.subscriptions ADD CONSTRAINT fk_subscriptions_plan_version FOREIGN KEY (plan_version_id) REFERENCES subscriptions.plan_versions (id);
                ALTER TABLE subscriptions.subscription_events ADD CONSTRAINT fk_subscription_events_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);

                ALTER TABLE subscriptions.subscriptions
                  ADD CONSTRAINT ck_subscriptions_status CHECK (status IN ('Trialing', 'Active', 'PastDue', 'Suspended', 'Cancelled', 'Expired')),
                  ADD CONSTRAINT ck_subscriptions_period CHECK (period IN ('Monthly', 'Quarterly', 'Yearly')),
                  ADD CONSTRAINT ck_subscriptions_currency CHECK (currency ~ '^[A-Z]{3}$'),
                  ADD CONSTRAINT ck_subscriptions_price CHECK (price >= 0 AND price <= 1000000),
                  ADD CONSTRAINT ck_subscriptions_dates CHECK (current_period_end > current_period_start),
                  ADD CONSTRAINT ck_subscriptions_before_suspension CHECK (status_before_suspension IS NULL OR status_before_suspension IN ('Trialing', 'Active', 'PastDue')),
                  -- Una suscripción suspendida dice por qué y desde cuándo; una cancelada, cuándo y por qué; una en prueba, cuándo termina.
                  ADD CONSTRAINT ck_subscriptions_suspended CHECK (status <> 'Suspended' OR (suspension_reason IS NOT NULL AND suspended_at IS NOT NULL AND status_before_suspension IS NOT NULL)),
                  ADD CONSTRAINT ck_subscriptions_cancelled CHECK ((status = 'Cancelled') = (cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL)),
                  ADD CONSTRAINT ck_subscriptions_trial CHECK (status <> 'Trialing' OR trial_ends_on IS NOT NULL);

                ALTER TABLE subscriptions.subscription_events
                  ADD CONSTRAINT ck_subscription_events_type CHECK (type IN ('Created', 'TrialStarted', 'Activated', 'PastDue', 'Suspended', 'Reactivated', 'Expired', 'Cancelled'));

                -- Transiciones válidas (la misma tabla del dominio): una transición inválida se rechaza aunque se escriba directo en la base.
                CREATE FUNCTION subscriptions.guard_subscription_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.id IS DISTINCT FROM OLD.id OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                     OR NEW.started_at IS DISTINCT FROM OLD.started_at OR NEW.created_at IS DISTINCT FROM OLD.created_at
                     OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'El gimnasio, el inicio y el origen de una suscripción no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
                       (OLD.status = 'Trialing'  AND NEW.status IN ('Active', 'Expired', 'Cancelled', 'Suspended'))
                    OR (OLD.status = 'Active'    AND NEW.status IN ('PastDue', 'Cancelled', 'Suspended', 'Expired'))
                    OR (OLD.status = 'PastDue'   AND NEW.status IN ('Active', 'Suspended', 'Expired', 'Cancelled'))
                    OR (OLD.status = 'Suspended' AND NEW.status IN ('Active', 'Trialing', 'PastDue', 'Cancelled'))
                    OR (OLD.status = 'Expired'   AND NEW.status IN ('Active', 'Cancelled'))) THEN
                    RAISE EXCEPTION 'Transición de suscripción no permitida: % -> %', OLD.status, NEW.status USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_subscriptions_guard_update BEFORE UPDATE ON subscriptions.subscriptions
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.guard_subscription_update();

                -- Nada se borra: ni la suscripción ni su historial.
                CREATE TRIGGER trg_subscriptions_no_delete BEFORE DELETE ON subscriptions.subscriptions
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                CREATE TRIGGER trg_subscription_events_immutable BEFORE UPDATE OR DELETE ON subscriptions.subscription_events
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                """);

            // Aislamiento: cada gimnasio ve solo su suscripción; el gimnasio «platform» ve y escribe la de todos; nadie más escribe, ni
            // siquiera el propietario de un gimnasio con una inyección SQL en la aplicación.
            migrationBuilder.Sql("""
                CREATE FUNCTION subscriptions.is_platform_context() RETURNS boolean LANGUAGE sql STABLE AS $fn$
                  SELECT EXISTS (
                    SELECT 1 FROM organizations.tenants t
                    WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid AND t.slug = 'platform')
                $fn$;

                ALTER TABLE subscriptions.subscriptions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscriptions FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_own_or_platform ON subscriptions.subscriptions FOR SELECT
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());
                CREATE POLICY platform_insert ON subscriptions.subscriptions FOR INSERT WITH CHECK (subscriptions.is_platform_context());
                CREATE POLICY platform_update ON subscriptions.subscriptions FOR UPDATE
                  USING (subscriptions.is_platform_context()) WITH CHECK (subscriptions.is_platform_context());

                ALTER TABLE subscriptions.subscription_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscription_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_own_or_platform ON subscriptions.subscription_events FOR SELECT
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());
                CREATE POLICY platform_insert ON subscriptions.subscription_events FOR INSERT WITH CHECK (subscriptions.is_platform_context());
                """);

            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON subscriptions.subscriptions TO fitcore_app;
                    GRANT SELECT, INSERT ON subscriptions.subscription_events TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS platform_insert ON subscriptions.subscription_events;
                DROP POLICY IF EXISTS read_own_or_platform ON subscriptions.subscription_events;
                ALTER TABLE subscriptions.subscription_events NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscription_events DISABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS platform_update ON subscriptions.subscriptions;
                DROP POLICY IF EXISTS platform_insert ON subscriptions.subscriptions;
                DROP POLICY IF EXISTS read_own_or_platform ON subscriptions.subscriptions;
                ALTER TABLE subscriptions.subscriptions NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscriptions DISABLE ROW LEVEL SECURITY;
                DROP FUNCTION IF EXISTS subscriptions.is_platform_context();
                DROP TRIGGER IF EXISTS trg_subscription_events_immutable ON subscriptions.subscription_events;
                DROP TRIGGER IF EXISTS trg_subscriptions_no_delete ON subscriptions.subscriptions;
                DROP TRIGGER IF EXISTS trg_subscriptions_guard_update ON subscriptions.subscriptions;
                DROP FUNCTION IF EXISTS subscriptions.guard_subscription_update();
                ALTER TABLE subscriptions.subscription_events DROP CONSTRAINT IF EXISTS ck_subscription_events_type, DROP CONSTRAINT IF EXISTS fk_subscription_events_tenant;
                ALTER TABLE subscriptions.subscriptions
                  DROP CONSTRAINT IF EXISTS ck_subscriptions_status, DROP CONSTRAINT IF EXISTS ck_subscriptions_period, DROP CONSTRAINT IF EXISTS ck_subscriptions_currency,
                  DROP CONSTRAINT IF EXISTS ck_subscriptions_price, DROP CONSTRAINT IF EXISTS ck_subscriptions_dates, DROP CONSTRAINT IF EXISTS ck_subscriptions_before_suspension,
                  DROP CONSTRAINT IF EXISTS ck_subscriptions_suspended, DROP CONSTRAINT IF EXISTS ck_subscriptions_cancelled, DROP CONSTRAINT IF EXISTS ck_subscriptions_trial,
                  DROP CONSTRAINT IF EXISTS fk_subscriptions_plan_version, DROP CONSTRAINT IF EXISTS fk_subscriptions_tenant;
                """);
        }
    }
}
