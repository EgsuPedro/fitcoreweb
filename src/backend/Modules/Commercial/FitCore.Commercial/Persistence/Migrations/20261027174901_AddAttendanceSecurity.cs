using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceSecurity : Migration
    {
        private static readonly string[] TenantTables = ["access.attendance"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "access.attendance", "fk_attendance_tenant");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "access.attendance", "fk_attendance_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "members", "billing", "cash", "access");

            migrationBuilder.Sql("""
                ALTER TABLE access.attendance
                  ADD CONSTRAINT ck_attendance_method CHECK (method IN ('Manual', 'Qr', 'Device')),
                  ADD CONSTRAINT ck_attendance_checkout_method CHECK (check_out_method IS NULL OR check_out_method IN ('Manual', 'Qr', 'Device')),
                  ADD CONSTRAINT ck_attendance_exception CHECK (is_exception = (exception_reason IS NOT NULL AND length(btrim(exception_reason)) > 0)),
                  ADD CONSTRAINT ck_attendance_checkout CHECK (
                    (check_out_at IS NULL) = (check_out_by IS NULL AND check_out_method IS NULL)
                    AND (check_out_at IS NULL OR check_out_at >= check_in_at));

                -- Un alumno no puede tener dos entradas abiertas a la vez (en ninguna sucursal). La aplicación lo valida primero;
                -- este índice decide cuando dos entradas llegan simultáneamente.
                CREATE UNIQUE INDEX ux_attendance_open_member ON access.attendance (member_id) WHERE check_out_at IS NULL;

                -- Una visita solo cambia al registrar su salida; una vez cerrada es definitiva y nada de la entrada se reescribe.
                CREATE FUNCTION access.guard_attendance_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.check_out_at IS NOT NULL THEN
                    RAISE EXCEPTION 'Una visita con salida registrada no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.member_id IS DISTINCT FROM OLD.member_id OR NEW.membership_id IS DISTINCT FROM OLD.membership_id
                     OR NEW.check_in_at IS DISTINCT FROM OLD.check_in_at OR NEW.method IS DISTINCT FROM OLD.method
                     OR NEW.is_exception IS DISTINCT FROM OLD.is_exception OR NEW.exception_reason IS DISTINCT FROM OLD.exception_reason
                     OR NEW.registered_by IS DISTINCT FROM OLD.registered_by THEN
                    RAISE EXCEPTION 'Los datos de la entrada no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE TRIGGER trg_attendance_guard_update BEFORE UPDATE ON access.attendance
                  FOR EACH ROW EXECUTE FUNCTION access.guard_attendance_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_attendance_guard_update ON access.attendance;
                DROP FUNCTION IF EXISTS access.guard_attendance_update();
                DROP INDEX IF EXISTS access.ux_attendance_open_member;
                ALTER TABLE access.attendance
                  DROP CONSTRAINT IF EXISTS ck_attendance_method, DROP CONSTRAINT IF EXISTS ck_attendance_checkout_method,
                  DROP CONSTRAINT IF EXISTS ck_attendance_exception, DROP CONSTRAINT IF EXISTS ck_attendance_checkout;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE access.attendance DROP CONSTRAINT IF EXISTS fk_attendance_branch;");
            migrationBuilder.Sql("ALTER TABLE access.attendance DROP CONSTRAINT IF EXISTS fk_attendance_tenant;");
        }
    }
}
