using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Analytics.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Aislamiento por gimnasio: Row-Level Security forzado (ni el propietario de la tabla se salta la política).
            migrationBuilder.Sql("ALTER TABLE analytics.report_exports ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE analytics.report_exports FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON analytics.report_exports
                  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("""
                -- Quien exporta es un usuario del mismo gimnasio: la base rechaza el de otro.
                ALTER TABLE analytics.report_exports ADD CONSTRAINT fk_report_exports_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);
                ALTER TABLE analytics.report_exports ADD CONSTRAINT fk_report_exports_user FOREIGN KEY (tenant_id, requested_by) REFERENCES identity.app_users (tenant_id, id);

                ALTER TABLE analytics.report_exports
                  ADD CONSTRAINT ck_report_exports_format CHECK (format = 'csv'),
                  ADD CONSTRAINT ck_report_exports_report CHECK (report IN (
                    'Collections', 'PaymentMethods', 'OutstandingBalances', 'CashRegisters', 'Reversals', 'MemberAcquisitions', 'MemberRetention',
                    'MemberRenewals', 'MemberExpirations', 'Attendance', 'ClassOccupancy', 'ClassCancellations', 'BranchComparison')),
                  ADD CONSTRAINT ck_report_exports_rows CHECK (row_count BETWEEN 0 AND 20000),
                  ADD CONSTRAINT ck_report_exports_size CHECK (file_size >= 0),
                  ADD CONSTRAINT ck_report_exports_expiry CHECK (expires_at > requested_at),
                  ADD CONSTRAINT ck_report_exports_downloads CHECK (download_count >= 0),
                  -- Un archivo purgado ya no tiene clave, y uno con clave nunca está purgado.
                  ADD CONSTRAINT ck_report_exports_purge CHECK ((file_key IS NULL) = (purged_at IS NOT NULL));

                -- Lo exportado no cambia: solo se cuenta cada descarga y se purga el archivo al vencer.
                CREATE FUNCTION analytics.guard_report_export_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.requested_by IS DISTINCT FROM OLD.requested_by
                     OR NEW.requested_at IS DISTINCT FROM OLD.requested_at OR NEW.report IS DISTINCT FROM OLD.report
                     OR NEW.format IS DISTINCT FROM OLD.format OR NEW.filters IS DISTINCT FROM OLD.filters
                     OR NEW.row_count IS DISTINCT FROM OLD.row_count OR NEW.file_size IS DISTINCT FROM OLD.file_size
                     OR NEW.expires_at IS DISTINCT FROM OLD.expires_at THEN
                    RAISE EXCEPTION 'Una exportación registrada no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.purged_at IS NOT NULL AND (NEW.file_key IS NOT NULL OR NEW.purged_at IS DISTINCT FROM OLD.purged_at) THEN
                    RAISE EXCEPTION 'Un archivo purgado no se recupera' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_report_exports_guard BEFORE UPDATE ON analytics.report_exports
                  FOR EACH ROW EXECUTE FUNCTION analytics.guard_report_export_update();
                """);

            // Permisos del rol de la aplicación: leer y escribir su propio registro, sin poder borrarlo (queda la huella de quién exportó).
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitcore_app') THEN
                    GRANT USAGE ON SCHEMA analytics TO fitcore_app;
                    GRANT SELECT, INSERT, UPDATE ON analytics.report_exports TO fitcore_app;
                    REVOKE DELETE ON analytics.report_exports FROM fitcore_app;
                  END IF;
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_report_exports_guard ON analytics.report_exports;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.guard_report_export_update();");
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON analytics.report_exports;");
            migrationBuilder.Sql("ALTER TABLE analytics.report_exports NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE analytics.report_exports DISABLE ROW LEVEL SECURITY;");
        }
    }
}
