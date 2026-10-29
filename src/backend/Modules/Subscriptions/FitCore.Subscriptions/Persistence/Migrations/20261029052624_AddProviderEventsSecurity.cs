using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderEventsSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los eventos del proveedor son solo del equipo de plataforma: el gimnasio no los lee ni los escribe. Solo se agregan (constancia para la
            // idempotencia y la auditoría); no se editan ni se borran. No hay columnas para el cuerpo de la petición ni para datos de tarjeta.
            migrationBuilder.Sql("""
                ALTER TABLE subscriptions.provider_events
                  ADD CONSTRAINT fk_provider_events_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT fk_provider_events_invoice FOREIGN KEY (invoice_id) REFERENCES subscriptions.invoices (id),
                  ADD CONSTRAINT ck_provider_events_outcome CHECK (outcome IN ('Applied', 'Rejected', 'Ignored')),
                  ADD CONSTRAINT ck_provider_events_type CHECK (type IN ('PaymentSucceeded', 'PaymentFailed', 'PaymentRefunded')),
                  ADD CONSTRAINT ck_provider_events_amount CHECK (amount IS NULL OR amount > 0),
                  ADD CONSTRAINT ck_provider_events_currency CHECK (currency IS NULL OR currency ~ '^[A-Z]{3}$'),
                  ADD CONSTRAINT ck_provider_events_code CHECK ((outcome = 'Applied') = (outcome_code IS NULL) OR outcome = 'Ignored');

                CREATE TRIGGER trg_provider_events_immutable BEFORE UPDATE OR DELETE ON subscriptions.provider_events
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();

                ALTER TABLE subscriptions.provider_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.provider_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY platform_only ON subscriptions.provider_events FOR SELECT USING (subscriptions.is_platform_context());
                CREATE POLICY platform_insert ON subscriptions.provider_events FOR INSERT WITH CHECK (subscriptions.is_platform_context());

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT ON subscriptions.provider_events TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS platform_insert ON subscriptions.provider_events;
                DROP POLICY IF EXISTS platform_only ON subscriptions.provider_events;
                ALTER TABLE subscriptions.provider_events NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.provider_events DISABLE ROW LEVEL SECURITY;
                DROP TRIGGER IF EXISTS trg_provider_events_immutable ON subscriptions.provider_events;
                ALTER TABLE subscriptions.provider_events
                  DROP CONSTRAINT IF EXISTS ck_provider_events_code, DROP CONSTRAINT IF EXISTS ck_provider_events_currency, DROP CONSTRAINT IF EXISTS ck_provider_events_amount,
                  DROP CONSTRAINT IF EXISTS ck_provider_events_type, DROP CONSTRAINT IF EXISTS ck_provider_events_outcome,
                  DROP CONSTRAINT IF EXISTS fk_provider_events_invoice, DROP CONSTRAINT IF EXISTS fk_provider_events_tenant;
                """);
        }
    }
}
