using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntitlementsSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE subscriptions.tenant_entitlements ADD CONSTRAINT fk_tenant_entitlements_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);
                ALTER TABLE subscriptions.tenant_entitlements ADD CONSTRAINT ck_tenant_entitlements_mode CHECK (access_mode IN ('Full', 'Grace', 'Restricted'));

                -- Mismo aislamiento que las suscripciones: cada gimnasio lee lo suyo; solo el contexto de plataforma escribe.
                ALTER TABLE subscriptions.tenant_entitlements ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.tenant_entitlements FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_own_or_platform ON subscriptions.tenant_entitlements FOR SELECT
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());
                CREATE POLICY platform_insert ON subscriptions.tenant_entitlements FOR INSERT WITH CHECK (subscriptions.is_platform_context());
                CREATE POLICY platform_update ON subscriptions.tenant_entitlements FOR UPDATE
                  USING (subscriptions.is_platform_context()) WITH CHECK (subscriptions.is_platform_context());

                -- Los derechos vigentes los deriva la base de la suscripción actual del gimnasio (la no cancelada o, si no hay, la última),
                -- en la misma transacción que cualquier cambio: no pueden quedar desfasados ni por una escritura directa.
                CREATE FUNCTION subscriptions.sync_entitlements() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  INSERT INTO subscriptions.tenant_entitlements
                    (tenant_id, subscription_id, plan_code, plan_name, plan_version, status, access_mode, features, limits, updated_at)
                  SELECT s.tenant_id, s.id, s.plan_code, s.plan_name, s.plan_version_number, s.status,
                         CASE s.status WHEN 'Trialing' THEN 'Full' WHEN 'Active' THEN 'Full' WHEN 'PastDue' THEN 'Grace' ELSE 'Restricted' END,
                         COALESCE((SELECT array_agg(f.code ORDER BY f.code) FROM subscriptions.plan_features f WHERE f.plan_version_id = s.plan_version_id), ARRAY[]::text[]),
                         COALESCE((SELECT jsonb_object_agg(l.code, l.value) FROM subscriptions.plan_limits l WHERE l.plan_version_id = s.plan_version_id), '{}'::jsonb),
                         now()
                  FROM (SELECT * FROM subscriptions.subscriptions WHERE tenant_id = NEW.tenant_id
                        ORDER BY (status <> 'Cancelled') DESC, created_at DESC LIMIT 1) s
                  ON CONFLICT (tenant_id) DO UPDATE SET
                    subscription_id = EXCLUDED.subscription_id, plan_code = EXCLUDED.plan_code, plan_name = EXCLUDED.plan_name,
                    plan_version = EXCLUDED.plan_version, status = EXCLUDED.status, access_mode = EXCLUDED.access_mode,
                    features = EXCLUDED.features, limits = EXCLUDED.limits, updated_at = EXCLUDED.updated_at;
                  RETURN NULL;
                END
                $fn$;
                CREATE TRIGGER trg_subscriptions_sync_entitlements AFTER INSERT OR UPDATE ON subscriptions.subscriptions
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.sync_entitlements();
                """);

            // Límites del plan. Un bloqueo asesor por gimnasio y límite serializa las altas simultáneas: la segunda espera a que la primera
            // termine y recién entonces cuenta, así que dos altas a la vez nunca pasan el tope. Sin derechos registrados (alta del gimnasio en
            // curso, gimnasio de plataforma) no se bloquea nada. Con la suscripción restringida no se crean recursos nuevos.
            migrationBuilder.Sql("""
                CREATE FUNCTION subscriptions.limit_for(p_tenant uuid, p_limit text) RETURNS bigint LANGUAGE plpgsql AS $fn$
                DECLARE e record;
                BEGIN
                  PERFORM pg_advisory_xact_lock(hashtextextended('fc-limit:' || p_limit || ':' || p_tenant::text, 0));
                  SELECT access_mode, limits INTO e FROM subscriptions.tenant_entitlements WHERE tenant_id = p_tenant;
                  IF NOT FOUND THEN
                    RETURN NULL;
                  END IF;
                  IF e.access_mode = 'Restricted' THEN
                    RAISE EXCEPTION 'La suscripción está restringida: no se pueden crear recursos nuevos' USING ERRCODE = 'FC102';
                  END IF;
                  RETURN (e.limits ->> p_limit)::bigint;
                END
                $fn$;

                CREATE FUNCTION subscriptions.enforce_branch_limit() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE lim bigint; cnt bigint;
                BEGIN
                  lim := subscriptions.limit_for(NEW.tenant_id, 'branches');
                  IF lim IS NOT NULL THEN
                    SELECT count(*) INTO cnt FROM organizations.branches WHERE tenant_id = NEW.tenant_id AND is_active;
                    IF cnt + 1 > lim THEN
                      RAISE EXCEPTION 'Límite del plan alcanzado: sucursales (máximo %, actuales %)', lim, cnt USING ERRCODE = 'FC101';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_branches_plan_limit BEFORE INSERT ON organizations.branches
                  FOR EACH ROW WHEN (NEW.is_active) EXECUTE FUNCTION subscriptions.enforce_branch_limit();
                CREATE TRIGGER trg_branches_plan_limit_reactivate BEFORE UPDATE OF is_active ON organizations.branches
                  FOR EACH ROW WHEN (NEW.is_active AND NOT OLD.is_active) EXECUTE FUNCTION subscriptions.enforce_branch_limit();

                CREATE FUNCTION subscriptions.enforce_user_limit() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE lim bigint; cnt bigint;
                BEGIN
                  lim := subscriptions.limit_for(NEW.tenant_id, 'internal_users');
                  IF lim IS NOT NULL THEN
                    SELECT count(*) INTO cnt FROM identity.app_users WHERE tenant_id = NEW.tenant_id AND is_active AND account_kind = 'Staff';
                    IF cnt + 1 > lim THEN
                      RAISE EXCEPTION 'Límite del plan alcanzado: usuarios internos (máximo %, actuales %)', lim, cnt USING ERRCODE = 'FC101';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_app_users_plan_limit BEFORE INSERT ON identity.app_users
                  FOR EACH ROW WHEN (NEW.is_active AND NEW.account_kind = 'Staff') EXECUTE FUNCTION subscriptions.enforce_user_limit();
                CREATE TRIGGER trg_app_users_plan_limit_reactivate BEFORE UPDATE OF is_active, account_kind ON identity.app_users
                  FOR EACH ROW WHEN (NEW.is_active AND NEW.account_kind = 'Staff' AND NOT (OLD.is_active AND OLD.account_kind = 'Staff'))
                  EXECUTE FUNCTION subscriptions.enforce_user_limit();

                CREATE FUNCTION subscriptions.enforce_member_limit() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE lim bigint; cnt bigint;
                BEGIN
                  lim := subscriptions.limit_for(NEW.tenant_id, 'members');
                  IF lim IS NOT NULL THEN
                    SELECT count(*) INTO cnt FROM members.members WHERE tenant_id = NEW.tenant_id;
                    IF cnt + 1 > lim THEN
                      RAISE EXCEPTION 'Límite del plan alcanzado: alumnos (máximo %, actuales %)', lim, cnt USING ERRCODE = 'FC101';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_members_plan_limit BEFORE INSERT ON members.members
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.enforce_member_limit();
                """);

            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON subscriptions.tenant_entitlements TO fitcore_app;
                  END IF;
                END
                $do$;
                """);

            // Derechos de las suscripciones que ya existen (los gimnasios del plan heredado): una actualización sin cambios dispara la derivación.
            migrationBuilder.Sql("""
                DO $do$
                DECLARE p uuid;
                BEGIN
                  SELECT id INTO p FROM organizations.tenants WHERE slug = 'platform';
                  IF p IS NULL THEN
                    RETURN;
                  END IF;
                  PERFORM set_config('app.tenant_id', p::text, true);
                  UPDATE subscriptions.subscriptions SET updated_at = updated_at;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_members_plan_limit ON members.members;
                DROP TRIGGER IF EXISTS trg_app_users_plan_limit_reactivate ON identity.app_users;
                DROP TRIGGER IF EXISTS trg_app_users_plan_limit ON identity.app_users;
                DROP TRIGGER IF EXISTS trg_branches_plan_limit_reactivate ON organizations.branches;
                DROP TRIGGER IF EXISTS trg_branches_plan_limit ON organizations.branches;
                DROP FUNCTION IF EXISTS subscriptions.enforce_member_limit();
                DROP FUNCTION IF EXISTS subscriptions.enforce_user_limit();
                DROP FUNCTION IF EXISTS subscriptions.enforce_branch_limit();
                DROP FUNCTION IF EXISTS subscriptions.limit_for(uuid, text);
                DROP TRIGGER IF EXISTS trg_subscriptions_sync_entitlements ON subscriptions.subscriptions;
                DROP FUNCTION IF EXISTS subscriptions.sync_entitlements();
                DROP POLICY IF EXISTS platform_update ON subscriptions.tenant_entitlements;
                DROP POLICY IF EXISTS platform_insert ON subscriptions.tenant_entitlements;
                DROP POLICY IF EXISTS read_own_or_platform ON subscriptions.tenant_entitlements;
                ALTER TABLE subscriptions.tenant_entitlements NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.tenant_entitlements DISABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.tenant_entitlements DROP CONSTRAINT IF EXISTS ck_tenant_entitlements_mode, DROP CONSTRAINT IF EXISTS fk_tenant_entitlements_tenant;
                """);
        }
    }
}
