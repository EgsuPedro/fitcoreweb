using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionsSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los planes comerciales son globales (los administra el equipo de plataforma): no llevan tenant_id ni RLS.
            // Lo que la base garantiza por sí misma: restricciones de valores, versiones inmutables y que nada se borra.
            migrationBuilder.Sql("""
                ALTER TABLE subscriptions.plans
                  ADD CONSTRAINT ck_plans_code CHECK (code ~ '^[a-z0-9][a-z0-9_-]{1,38}[a-z0-9]$');

                ALTER TABLE subscriptions.plan_versions
                  ADD CONSTRAINT ck_plan_versions_number CHECK (number >= 1),
                  ADD CONSTRAINT ck_plan_versions_currency CHECK (currency ~ '^[A-Z]{3}$'),
                  ADD CONSTRAINT ck_plan_versions_period CHECK (period IN ('Monthly', 'Quarterly', 'Yearly')),
                  ADD CONSTRAINT ck_plan_versions_price CHECK (price >= 0 AND price <= 1000000),
                  ADD CONSTRAINT ck_plan_versions_trial CHECK (trial_days BETWEEN 0 AND 365),
                  ADD CONSTRAINT ck_plan_versions_grace CHECK (grace_days IS NULL OR grace_days BETWEEN 0 AND 90);

                ALTER TABLE subscriptions.plan_limits
                  ADD CONSTRAINT ck_plan_limits_value CHECK (value IS NULL OR value >= 0);

                -- Un plan jamás se borra (se desactiva) y su identidad no cambia.
                CREATE FUNCTION subscriptions.guard_plan_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.id IS DISTINCT FROM OLD.id OR NEW.code IS DISTINCT FROM OLD.code
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'El código y el origen de un plan comercial no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_plans_guard_update BEFORE UPDATE ON subscriptions.plans
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.guard_plan_update();

                -- Las condiciones de una versión son inmutables: un cambio comercial crea otra versión (el histórico de precios no se reescribe).
                CREATE FUNCTION subscriptions.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION '% de % no está permitido: los planes y sus versiones son históricos', TG_OP, TG_TABLE_NAME USING ERRCODE = '23514';
                END
                $fn$;
                CREATE TRIGGER trg_plans_no_delete BEFORE DELETE ON subscriptions.plans
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                CREATE TRIGGER trg_plan_versions_immutable BEFORE UPDATE OR DELETE ON subscriptions.plan_versions
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                CREATE TRIGGER trg_plan_features_immutable BEFORE UPDATE OR DELETE ON subscriptions.plan_features
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();
                CREATE TRIGGER trg_plan_limits_immutable BEFORE UPDATE OR DELETE ON subscriptions.plan_limits
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();

                -- Las versiones son consecutivas (1, 2, 3…) por plan: sin huecos ni números repetidos.
                CREATE FUNCTION subscriptions.check_plan_version_number() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE last_number int;
                BEGIN
                  SELECT COALESCE(MAX(number), 0) INTO last_number FROM subscriptions.plan_versions WHERE plan_id = NEW.plan_id;
                  IF NEW.number <> last_number + 1 THEN
                    RAISE EXCEPTION 'La versión de un plan debe ser la siguiente a la última (%).', last_number + 1 USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_plan_versions_number BEFORE INSERT ON subscriptions.plan_versions
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.check_plan_version_number();
                """);

            // Permisos del rol de la aplicación: leer, crear y (solo en plans) actualizar; nunca borrar planes ni versiones.
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT USAGE ON SCHEMA subscriptions TO fitcore_app;
                    GRANT SELECT, INSERT, UPDATE ON subscriptions.plans TO fitcore_app;
                    GRANT SELECT, INSERT ON subscriptions.plan_versions, subscriptions.plan_features, subscriptions.plan_limits TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_plan_versions_number ON subscriptions.plan_versions;
                DROP TRIGGER IF EXISTS trg_plan_limits_immutable ON subscriptions.plan_limits;
                DROP TRIGGER IF EXISTS trg_plan_features_immutable ON subscriptions.plan_features;
                DROP TRIGGER IF EXISTS trg_plan_versions_immutable ON subscriptions.plan_versions;
                DROP TRIGGER IF EXISTS trg_plans_no_delete ON subscriptions.plans;
                DROP TRIGGER IF EXISTS trg_plans_guard_update ON subscriptions.plans;
                DROP FUNCTION IF EXISTS subscriptions.check_plan_version_number();
                DROP FUNCTION IF EXISTS subscriptions.forbid_change();
                DROP FUNCTION IF EXISTS subscriptions.guard_plan_update();
                ALTER TABLE subscriptions.plan_limits DROP CONSTRAINT IF EXISTS ck_plan_limits_value;
                ALTER TABLE subscriptions.plan_versions
                  DROP CONSTRAINT IF EXISTS ck_plan_versions_number, DROP CONSTRAINT IF EXISTS ck_plan_versions_currency,
                  DROP CONSTRAINT IF EXISTS ck_plan_versions_period, DROP CONSTRAINT IF EXISTS ck_plan_versions_price,
                  DROP CONSTRAINT IF EXISTS ck_plan_versions_trial, DROP CONSTRAINT IF EXISTS ck_plan_versions_grace;
                ALTER TABLE subscriptions.plans DROP CONSTRAINT IF EXISTS ck_plans_code;
                """);
        }
    }
}
