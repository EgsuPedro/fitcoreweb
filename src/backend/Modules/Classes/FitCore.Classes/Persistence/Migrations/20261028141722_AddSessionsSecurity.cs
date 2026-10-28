using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Classes.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionsSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "classes.class_series",
            "classes.class_sessions"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "classes.class_series", "fk_class_series_tenant");
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "classes.class_sessions", "fk_class_sessions_tenant");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "classes.class_series", "fk_class_series_branch");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "classes.class_sessions", "fk_class_sessions_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "classes");

            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS btree_gist;

                -- La sesión y la serie heredan la sucursal de su clase y la base lo exige: la sucursal de una clase no cambia (trigger),
                -- así que ninguna sesión puede quedar en una sucursal distinta de la de su clase.
                ALTER TABLE classes.group_classes ADD CONSTRAINT uq_group_classes_tenant_id_branch UNIQUE (tenant_id, id, branch_id);

                ALTER TABLE classes.class_series
                  ADD CONSTRAINT fk_class_series_class FOREIGN KEY (tenant_id, class_id, branch_id)
                    REFERENCES classes.group_classes (tenant_id, id, branch_id),
                  ADD CONSTRAINT fk_class_series_instructor FOREIGN KEY (tenant_id, instructor_id)
                    REFERENCES training.trainer_profiles (tenant_id, id);

                ALTER TABLE classes.class_sessions
                  ADD CONSTRAINT fk_class_sessions_class FOREIGN KEY (tenant_id, class_id, branch_id)
                    REFERENCES classes.group_classes (tenant_id, id, branch_id),
                  ADD CONSTRAINT fk_class_sessions_series FOREIGN KEY (tenant_id, series_id)
                    REFERENCES classes.class_series (tenant_id, id),
                  ADD CONSTRAINT fk_class_sessions_instructor FOREIGN KEY (tenant_id, instructor_id)
                    REFERENCES training.trainer_profiles (tenant_id, id);

                ALTER TABLE classes.class_series
                  ADD CONSTRAINT ck_class_series_status CHECK (status IN ('Active', 'Cancelled')),
                  ADD CONSTRAINT ck_class_series_days CHECK (days_mask BETWEEN 1 AND 127),
                  ADD CONSTRAINT ck_class_series_range CHECK (end_date >= start_date AND end_date - start_date <= 366),
                  ADD CONSTRAINT ck_class_series_capacity CHECK (capacity BETWEEN 1 AND 500),
                  ADD CONSTRAINT ck_class_series_duration CHECK (duration_minutes BETWEEN 5 AND 480),
                  ADD CONSTRAINT ck_class_series_cancel CHECK (
                    (status = 'Cancelled') = (cancel_reason IS NOT NULL AND cancelled_at IS NOT NULL));

                ALTER TABLE classes.class_sessions
                  ADD CONSTRAINT ck_class_sessions_status CHECK (status IN ('Scheduled', 'Cancelled', 'Completed')),
                  ADD CONSTRAINT ck_class_sessions_range CHECK (ends_at > starts_at AND ends_at - starts_at <= interval '8 hours'),
                  ADD CONSTRAINT ck_class_sessions_capacity CHECK (capacity BETWEEN 1 AND 500),
                  ADD CONSTRAINT ck_class_sessions_cancel CHECK (
                    (status = 'Cancelled') = (cancel_reason IS NOT NULL AND cancelled_at IS NOT NULL)),
                  ADD CONSTRAINT ck_class_sessions_override CHECK (
                    availability_override_reason IS NULL OR length(btrim(availability_override_reason)) >= 5);

                -- Regenerar una serie no duplica: cada hueco de la serie existe una sola vez, aunque la sesión se haya movido o cancelado.
                CREATE UNIQUE INDEX ux_class_sessions_series_slot ON classes.class_sessions (tenant_id, series_id, original_starts_at)
                  WHERE series_id IS NOT NULL;

                -- Un instructor no puede dar dos sesiones a la vez (en cualquier sucursal). La aplicación lo valida primero;
                -- esta restricción decide cuando dos solicitudes simultáneas pretenden el mismo horario. Las canceladas no cuentan.
                -- El rango es [inicio, fin): una sesión puede empezar justo cuando termina la anterior.
                ALTER TABLE classes.class_sessions
                  ADD CONSTRAINT ex_class_sessions_instructor_overlap
                  EXCLUDE USING gist (tenant_id WITH =, instructor_id WITH =, tstzrange(starts_at, ends_at) WITH &&)
                  WHERE (status <> 'Cancelled');

                -- Lo que ancla la sesión a su origen no cambia, y una cancelación no se deshace (se programa otra sesión).
                CREATE FUNCTION classes.guard_class_session_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.class_id IS DISTINCT FROM OLD.class_id OR NEW.series_id IS DISTINCT FROM OLD.series_id
                     OR NEW.original_starts_at IS DISTINCT FROM OLD.original_starts_at
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'El gimnasio, la sucursal, la clase, la serie y el origen de una sesión no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Cancelled' AND NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION 'Una sesión cancelada no se reactiva: programe una nueva' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_class_sessions_guard_update BEFORE UPDATE ON classes.class_sessions
                  FOR EACH ROW EXECUTE FUNCTION classes.guard_class_session_update();

                CREATE FUNCTION classes.guard_class_series_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.class_id IS DISTINCT FROM OLD.class_id OR NEW.start_date IS DISTINCT FROM OLD.start_date
                     OR NEW.start_time IS DISTINCT FROM OLD.start_time OR NEW.days_mask IS DISTINCT FROM OLD.days_mask
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'La regla y el origen de una serie no se pueden modificar: cree otra serie' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.end_date < OLD.end_date THEN
                    RAISE EXCEPTION 'La serie solo se puede extender' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Cancelled' AND NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION 'Una serie cancelada no se reactiva: cree otra' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_class_series_guard_update BEFORE UPDATE ON classes.class_series
                  FOR EACH ROW EXECUTE FUNCTION classes.guard_class_series_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_class_sessions_guard_update ON classes.class_sessions;
                DROP TRIGGER IF EXISTS trg_class_series_guard_update ON classes.class_series;
                DROP FUNCTION IF EXISTS classes.guard_class_session_update();
                DROP FUNCTION IF EXISTS classes.guard_class_series_update();
                ALTER TABLE classes.class_sessions DROP CONSTRAINT IF EXISTS ex_class_sessions_instructor_overlap;
                DROP INDEX IF EXISTS classes.ux_class_sessions_series_slot;
                ALTER TABLE classes.class_sessions
                  DROP CONSTRAINT IF EXISTS fk_class_sessions_instructor,
                  DROP CONSTRAINT IF EXISTS fk_class_sessions_series,
                  DROP CONSTRAINT IF EXISTS fk_class_sessions_class;
                ALTER TABLE classes.class_series
                  DROP CONSTRAINT IF EXISTS fk_class_series_instructor,
                  DROP CONSTRAINT IF EXISTS fk_class_series_class;
                ALTER TABLE classes.group_classes DROP CONSTRAINT IF EXISTS uq_group_classes_tenant_id_branch;
                """);
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("""
                ALTER TABLE classes.class_sessions DROP CONSTRAINT IF EXISTS fk_class_sessions_branch;
                ALTER TABLE classes.class_sessions DROP CONSTRAINT IF EXISTS fk_class_sessions_tenant;
                ALTER TABLE classes.class_series DROP CONSTRAINT IF EXISTS fk_class_series_branch;
                ALTER TABLE classes.class_series DROP CONSTRAINT IF EXISTS fk_class_series_tenant;
                """);
        }
    }
}
