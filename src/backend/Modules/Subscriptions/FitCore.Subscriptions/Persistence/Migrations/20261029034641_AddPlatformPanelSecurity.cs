using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformPanelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Notas administrativas y fotos de consumo son del equipo de plataforma: el gimnasio no las lee ni las escribe (solo el contexto «platform»).
            migrationBuilder.Sql("""
                ALTER TABLE subscriptions.platform_notes
                  ADD CONSTRAINT fk_platform_notes_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_platform_notes_text CHECK (length(btrim(text)) BETWEEN 1 AND 1000);
                ALTER TABLE subscriptions.tenant_usage_snapshots
                  ADD CONSTRAINT fk_usage_snapshots_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id),
                  ADD CONSTRAINT ck_usage_snapshots_counts CHECK (branches >= 0 AND internal_users >= 0 AND members >= 0);

                -- Las notas solo se agregan: no se editan ni se borran.
                CREATE TRIGGER trg_platform_notes_immutable BEFORE UPDATE OR DELETE ON subscriptions.platform_notes
                  FOR EACH ROW EXECUTE FUNCTION subscriptions.forbid_change();

                ALTER TABLE subscriptions.platform_notes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.platform_notes FORCE ROW LEVEL SECURITY;
                CREATE POLICY platform_only ON subscriptions.platform_notes FOR SELECT USING (subscriptions.is_platform_context());
                CREATE POLICY platform_insert ON subscriptions.platform_notes FOR INSERT WITH CHECK (subscriptions.is_platform_context());

                ALTER TABLE subscriptions.tenant_usage_snapshots ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.tenant_usage_snapshots FORCE ROW LEVEL SECURITY;
                CREATE POLICY platform_only ON subscriptions.tenant_usage_snapshots FOR SELECT USING (subscriptions.is_platform_context());
                CREATE POLICY platform_insert ON subscriptions.tenant_usage_snapshots FOR INSERT WITH CHECK (subscriptions.is_platform_context());
                CREATE POLICY platform_update ON subscriptions.tenant_usage_snapshots FOR UPDATE
                  USING (subscriptions.is_platform_context()) WITH CHECK (subscriptions.is_platform_context());

                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT SELECT, INSERT ON subscriptions.platform_notes TO fitcore_app;
                    GRANT SELECT, INSERT, UPDATE ON subscriptions.tenant_usage_snapshots TO fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS platform_update ON subscriptions.tenant_usage_snapshots;
                DROP POLICY IF EXISTS platform_insert ON subscriptions.tenant_usage_snapshots;
                DROP POLICY IF EXISTS platform_only ON subscriptions.tenant_usage_snapshots;
                ALTER TABLE subscriptions.tenant_usage_snapshots NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.tenant_usage_snapshots DISABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS platform_insert ON subscriptions.platform_notes;
                DROP POLICY IF EXISTS platform_only ON subscriptions.platform_notes;
                ALTER TABLE subscriptions.platform_notes NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions.platform_notes DISABLE ROW LEVEL SECURITY;
                DROP TRIGGER IF EXISTS trg_platform_notes_immutable ON subscriptions.platform_notes;
                ALTER TABLE subscriptions.tenant_usage_snapshots DROP CONSTRAINT IF EXISTS ck_usage_snapshots_counts, DROP CONSTRAINT IF EXISTS fk_usage_snapshots_tenant;
                ALTER TABLE subscriptions.platform_notes DROP CONSTRAINT IF EXISTS ck_platform_notes_text, DROP CONSTRAINT IF EXISTS fk_platform_notes_tenant;
                """);
        }
    }
}
