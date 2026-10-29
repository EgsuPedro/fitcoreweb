using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPortalSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE subscriptions.subscription_requests
                  ADD CONSTRAINT fk_subscription_requests_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_subscription_requests_subscription FOREIGN KEY (subscription_id) REFERENCES subscriptions.subscriptions (id),
                  ADD CONSTRAINT fk_subscription_requests_plan FOREIGN KEY (requested_plan_id) REFERENCES subscriptions.plans (id),
                  ADD CONSTRAINT ck_subscription_requests_kind CHECK (kind IN ('ChangePlan', 'Cancellation', 'Reactivation')),
                  ADD CONSTRAINT ck_subscription_requests_status CHECK (status IN ('Pending', 'Approved', 'Rejected', 'Withdrawn')),
                  ADD CONSTRAINT ck_subscription_requests_plan CHECK ((kind = 'ChangePlan') = (requested_plan_id IS NOT NULL)),
                  ADD CONSTRAINT ck_subscription_requests_resolved CHECK ((status = 'Pending') = (resolved_at IS NULL)),
                  ADD CONSTRAINT ck_subscription_requests_text CHECK (
                    (message IS NULL OR length(message) <= 500) AND (resolution_note IS NULL OR length(resolution_note) <= 500));
                ALTER TABLE subscriptions.subscription_notices
                  ADD CONSTRAINT fk_subscription_notices_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_subscription_notices_subscription FOREIGN KEY (subscription_id) REFERENCES subscriptions.subscriptions (id),
                  ADD CONSTRAINT ck_subscription_notices_severity CHECK (severity IN ('Info', 'Warning', 'Critical'));

                -- Una solicitud solo se resuelve una vez y su contenido no cambia. El gimnasio solo puede retirar la suya; aprobar o rechazar es de la plataforma.
                CREATE FUNCTION subscriptions.guard_request_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Las solicitudes no se borran' USING ERRCODE = 'FC110';
                  END IF;
                  IF NEW.id <> OLD.id OR NEW.tenant_id <> OLD.tenant_id OR NEW.subscription_id <> OLD.subscription_id OR NEW.kind <> OLD.kind
                     OR NEW.requested_plan_id IS DISTINCT FROM OLD.requested_plan_id OR NEW.message IS DISTINCT FROM OLD.message
                     OR NEW.requested_by IS DISTINCT FROM OLD.requested_by OR NEW.created_at <> OLD.created_at THEN
                    RAISE EXCEPTION 'El contenido de la solicitud no se modifica' USING ERRCODE = 'FC110';
                  END IF;
                  IF OLD.status <> 'Pending' THEN
                    RAISE EXCEPTION 'La solicitud ya fue resuelta' USING ERRCODE = 'FC110';
                  END IF;
                  IF NEW.status = 'Pending' THEN
                    RAISE EXCEPTION 'La solicitud pendiente no se modifica' USING ERRCODE = 'FC110';
                  END IF;
                  IF NOT subscriptions.is_platform_context() AND NEW.status <> 'Withdrawn' THEN
                    RAISE EXCEPTION 'Solo la plataforma aprueba o rechaza solicitudes' USING ERRCODE = 'FC110';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_subscription_requests_guard BEFORE UPDATE OR DELETE ON subscriptions.subscription_requests
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.guard_request_change();

                -- La solicitud se crea pendiente y sobre una suscripción del mismo gimnasio.
                CREATE FUNCTION subscriptions.guard_request_insert() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.status <> 'Pending' THEN
                    RAISE EXCEPTION 'La solicitud nace pendiente' USING ERRCODE = 'FC110';
                  END IF;
                  IF NOT EXISTS (SELECT 1 FROM subscriptions.subscriptions s WHERE s.id = NEW.subscription_id AND s.tenant_id = NEW.tenant_id) THEN
                    RAISE EXCEPTION 'La suscripción no pertenece al gimnasio' USING ERRCODE = 'FC110';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_subscription_requests_insert BEFORE INSERT ON subscriptions.subscription_requests
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.guard_request_insert();

                -- Un aviso solo cambia al marcarse leído (una vez); nunca se borra.
                CREATE FUNCTION subscriptions.guard_notice_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Los avisos no se borran' USING ERRCODE = 'FC110';
                  END IF;
                  IF NEW.id <> OLD.id OR NEW.tenant_id <> OLD.tenant_id OR NEW.subscription_id IS DISTINCT FROM OLD.subscription_id OR NEW.type <> OLD.type
                     OR NEW.severity <> OLD.severity OR NEW.dedupe_key <> OLD.dedupe_key OR NEW.plan_name IS DISTINCT FROM OLD.plan_name
                     OR NEW.related_date IS DISTINCT FROM OLD.related_date OR NEW.reference IS DISTINCT FROM OLD.reference OR NEW.created_at <> OLD.created_at THEN
                    RAISE EXCEPTION 'El contenido del aviso no se modifica' USING ERRCODE = 'FC110';
                  END IF;
                  IF OLD.read_at IS NOT NULL AND NEW.read_at IS DISTINCT FROM OLD.read_at THEN
                    RAISE EXCEPTION 'El aviso ya estaba leído' USING ERRCODE = 'FC110';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_subscription_notices_guard BEFORE UPDATE OR DELETE ON subscriptions.subscription_notices
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.guard_notice_change();

                -- Solicitudes: el gimnasio lee y crea las suyas y puede retirarlas; la plataforma lee todas y las resuelve.
                ALTER TABLE subscriptions.subscription_requests ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscription_requests FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_own_or_platform ON subscriptions.subscription_requests FOR SELECT
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());
                CREATE POLICY insert_own ON subscriptions.subscription_requests FOR INSERT
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid AND NOT subscriptions.is_platform_context());
                CREATE POLICY update_own_or_platform ON subscriptions.subscription_requests FOR UPDATE
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context())
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());

                -- Avisos: los crea solo la plataforma (el proceso de vencimientos); el gimnasio lee los suyos y los marca leídos.
                ALTER TABLE subscriptions.subscription_notices ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscription_notices FORCE ROW LEVEL SECURITY;
                CREATE POLICY read_own_or_platform ON subscriptions.subscription_notices FOR SELECT
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());
                CREATE POLICY platform_insert ON subscriptions.subscription_notices FOR INSERT WITH CHECK (subscriptions.is_platform_context());
                CREATE POLICY update_own_or_platform ON subscriptions.subscription_notices FOR UPDATE
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context())
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR subscriptions.is_platform_context());

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON subscriptions.subscription_requests TO fitcore_app;
                    GRANT SELECT, INSERT, UPDATE ON subscriptions.subscription_notices TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS update_own_or_platform ON subscriptions.subscription_notices;
                DROP POLICY IF EXISTS platform_insert ON subscriptions.subscription_notices;
                DROP POLICY IF EXISTS read_own_or_platform ON subscriptions.subscription_notices;
                ALTER TABLE subscriptions.subscription_notices NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscription_notices DISABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS update_own_or_platform ON subscriptions.subscription_requests;
                DROP POLICY IF EXISTS insert_own ON subscriptions.subscription_requests;
                DROP POLICY IF EXISTS read_own_or_platform ON subscriptions.subscription_requests;
                ALTER TABLE subscriptions.subscription_requests NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.subscription_requests DISABLE ROW LEVEL SECURITY;
                DROP TRIGGER IF EXISTS trg_subscription_notices_guard ON subscriptions.subscription_notices;
                DROP FUNCTION IF EXISTS subscriptions.guard_notice_change();
                DROP TRIGGER IF EXISTS trg_subscription_requests_insert ON subscriptions.subscription_requests;
                DROP FUNCTION IF EXISTS subscriptions.guard_request_insert();
                DROP TRIGGER IF EXISTS trg_subscription_requests_guard ON subscriptions.subscription_requests;
                DROP FUNCTION IF EXISTS subscriptions.guard_request_change();
                ALTER TABLE subscriptions.subscription_notices DROP CONSTRAINT IF EXISTS ck_subscription_notices_severity, DROP CONSTRAINT IF EXISTS fk_subscription_notices_subscription, DROP CONSTRAINT IF EXISTS fk_subscription_notices_tenant;
                ALTER TABLE subscriptions.subscription_requests DROP CONSTRAINT IF EXISTS ck_subscription_requests_text, DROP CONSTRAINT IF EXISTS ck_subscription_requests_resolved, DROP CONSTRAINT IF EXISTS ck_subscription_requests_plan, DROP CONSTRAINT IF EXISTS ck_subscription_requests_status, DROP CONSTRAINT IF EXISTS ck_subscription_requests_kind, DROP CONSTRAINT IF EXISTS fk_subscription_requests_plan, DROP CONSTRAINT IF EXISTS fk_subscription_requests_subscription, DROP CONSTRAINT IF EXISTS fk_subscription_requests_tenant;
                """);
        }
    }
}
