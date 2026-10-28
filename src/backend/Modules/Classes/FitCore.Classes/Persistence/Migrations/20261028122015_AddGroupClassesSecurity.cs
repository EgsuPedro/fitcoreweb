using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Classes.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupClassesSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "classes.group_classes"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "classes.group_classes", "fk_group_classes_tenant");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "classes.group_classes", "fk_group_classes_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "classes");

            migrationBuilder.Sql("""
                -- El instructor es un entrenador del mismo gimnasio: la base rechaza el de otro.
                ALTER TABLE classes.group_classes ADD CONSTRAINT fk_group_classes_instructor
                  FOREIGN KEY (tenant_id, instructor_id) REFERENCES training.trainer_profiles (tenant_id, id);

                -- Los mismos rangos y coherencias del dominio, como respaldo ante cualquier ruta que no pase por él.
                ALTER TABLE classes.group_classes
                  ADD CONSTRAINT ck_group_classes_name CHECK (length(btrim(name)) >= 2),
                  ADD CONSTRAINT ck_group_classes_activity CHECK (activity IN
                    ('Yoga', 'Spinning', 'Functional', 'Zumba', 'Pilates', 'Stretching', 'Boxing', 'Dance', 'Other')),
                  ADD CONSTRAINT ck_group_classes_level CHECK (level IN ('AllLevels', 'Beginner', 'Intermediate', 'Advanced')),
                  ADD CONSTRAINT ck_group_classes_duration CHECK (duration_minutes BETWEEN 5 AND 480),
                  ADD CONSTRAINT ck_group_classes_capacity CHECK (capacity BETWEEN 1 AND 500),
                  ADD CONSTRAINT ck_group_classes_notice CHECK (min_booking_notice_minutes BETWEEN 0 AND 10080),
                  ADD CONSTRAINT ck_group_classes_advance CHECK (max_booking_advance_days BETWEEN 1 AND 365),
                  ADD CONSTRAINT ck_group_classes_booking_window CHECK (min_booking_notice_minutes < max_booking_advance_days * 1440),
                  ADD CONSTRAINT ck_group_classes_cancellation CHECK (cancellation_deadline_minutes BETWEEN 0 AND 10080),
                  ADD CONSTRAINT ck_group_classes_waitlist CHECK (
                    (waitlist_enabled AND waitlist_capacity BETWEEN 1 AND 200) OR (NOT waitlist_enabled AND waitlist_capacity = 0)),
                  ADD CONSTRAINT ck_group_classes_offer_window CHECK (offer_window_minutes BETWEEN 5 AND 1440),
                  ADD CONSTRAINT ck_group_classes_payment_policy CHECK (NOT apply_payment_policy OR require_active_membership),
                  ADD CONSTRAINT ck_group_classes_min_age CHECK (min_age IS NULL OR min_age BETWEEN 0 AND 100);

                -- Un nombre por sucursal, sin distinguir mayúsculas: lo decide la base cuando dos altas llegan a la vez.
                CREATE UNIQUE INDEX ux_group_classes_branch_name ON classes.group_classes (tenant_id, branch_id, lower(btrim(name)));

                -- La clase pertenece para siempre a su gimnasio y sucursal: sus sesiones y reservas dependen de eso.
                CREATE FUNCTION classes.guard_group_class_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'El gimnasio, la sucursal y el origen de una clase no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_group_classes_guard_update BEFORE UPDATE ON classes.group_classes
                  FOR EACH ROW EXECUTE FUNCTION classes.guard_group_class_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_group_classes_guard_update ON classes.group_classes;
                DROP FUNCTION IF EXISTS classes.guard_group_class_update();
                DROP INDEX IF EXISTS classes.ux_group_classes_branch_name;
                ALTER TABLE classes.group_classes DROP CONSTRAINT IF EXISTS fk_group_classes_instructor;
                """);
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("""
                ALTER TABLE classes.group_classes DROP CONSTRAINT IF EXISTS fk_group_classes_branch;
                ALTER TABLE classes.group_classes DROP CONSTRAINT IF EXISTS fk_group_classes_tenant;
                """);
        }
    }
}
