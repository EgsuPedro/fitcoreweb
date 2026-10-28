using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Classes.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClassAttendanceSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "classes.class_attendance"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "classes.class_attendance", "fk_class_attendance_tenant");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "classes.class_attendance", "fk_class_attendance_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "classes");

            migrationBuilder.Sql("""
                -- La asistencia cuelga de UNA reserva y hereda su sesión, alumno y sucursal: la base no deja que apunten a cosas distintas.
                ALTER TABLE classes.class_reservations
                  ADD CONSTRAINT uq_class_reservations_attendance_ref UNIQUE (tenant_id, id, session_id, member_id, branch_id);

                ALTER TABLE classes.class_attendance
                  ADD CONSTRAINT fk_class_attendance_reservation FOREIGN KEY (tenant_id, reservation_id, session_id, member_id, branch_id)
                    REFERENCES classes.class_reservations (tenant_id, id, session_id, member_id, branch_id),
                  ADD CONSTRAINT ck_class_attendance_outcome CHECK (outcome IN ('Attended', 'NoShow')),
                  ADD CONSTRAINT ck_class_attendance_gym_entry CHECK (gym_entry_id IS NULL OR outcome = 'Attended');

                -- Solo una reserva confirmada de una sesión que ya empezó tiene asistencia (FC006, FC008). La fila de la reserva se comparte
                -- mientras se inserta, así una cancelación simultánea espera y después ve que ya tiene asistencia.
                CREATE FUNCTION classes.enforce_class_attendance() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  r record;
                  s record;
                BEGIN
                  SELECT status INTO r FROM classes.class_reservations WHERE id = NEW.reservation_id FOR SHARE;
                  IF r.status IS DISTINCT FROM 'Confirmed' THEN
                    RAISE EXCEPTION 'Solo una reserva confirmada tiene asistencia' USING ERRCODE = 'FC006';
                  END IF;

                  SELECT status, starts_at INTO s FROM classes.class_sessions WHERE id = NEW.session_id;
                  IF s.status = 'Cancelled' OR s.starts_at > now() THEN
                    RAISE EXCEPTION 'La asistencia se registra desde el inicio de una sesión no cancelada' USING ERRCODE = 'FC008';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_class_attendance_enforce BEFORE INSERT ON classes.class_attendance
                  FOR EACH ROW EXECUTE FUNCTION classes.enforce_class_attendance();

                -- Lo que ancla la asistencia no cambia; el resultado sí se puede corregir (queda en la auditoría).
                CREATE FUNCTION classes.guard_class_attendance_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.session_id IS DISTINCT FROM OLD.session_id OR NEW.reservation_id IS DISTINCT FROM OLD.reservation_id
                     OR NEW.member_id IS DISTINCT FROM OLD.member_id
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'La reserva, la sesión, el alumno y el origen de una asistencia no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_class_attendance_guard_update BEFORE UPDATE ON classes.class_attendance
                  FOR EACH ROW EXECUTE FUNCTION classes.guard_class_attendance_update();

                -- Una reserva con asistencia registrada ya no se cancela, y una sesión completada no vuelve a programada.
                CREATE OR REPLACE FUNCTION classes.guard_class_reservation_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.session_id IS DISTINCT FROM OLD.session_id OR NEW.member_id IS DISTINCT FROM OLD.member_id
                     OR NEW.source IS DISTINCT FROM OLD.source
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'El gimnasio, la sucursal, la sesión, el alumno y el origen de una reserva no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status IN ('Cancelled', 'Expired') AND NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION 'Una reserva cancelada o vencida no se reactiva: reserve de nuevo' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Waitlisted' AND NEW.status = 'Confirmed' THEN
                    RAISE EXCEPTION 'Desde la lista de espera se confirma aceptando una invitación' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Confirmed' AND NEW.status NOT IN ('Confirmed', 'Cancelled') THEN
                    RAISE EXCEPTION 'Una reserva confirmada solo se puede cancelar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Offered' AND NEW.status = 'Waitlisted' THEN
                    RAISE EXCEPTION 'Una invitación no vuelve a la lista de espera' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.status = 'Cancelled' AND OLD.status IS DISTINCT FROM 'Cancelled'
                     AND EXISTS (SELECT 1 FROM classes.class_attendance WHERE reservation_id = NEW.id) THEN
                    RAISE EXCEPTION 'Una reserva con asistencia registrada no se cancela' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                CREATE OR REPLACE FUNCTION classes.guard_class_session_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
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
                  IF OLD.status = 'Completed' AND NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION 'Una sesión completada no se reabre ni se cancela' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION classes.guard_class_session_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
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

                CREATE OR REPLACE FUNCTION classes.guard_class_reservation_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.session_id IS DISTINCT FROM OLD.session_id OR NEW.member_id IS DISTINCT FROM OLD.member_id
                     OR NEW.source IS DISTINCT FROM OLD.source
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                    RAISE EXCEPTION 'El gimnasio, la sucursal, la sesión, el alumno y el origen de una reserva no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status IN ('Cancelled', 'Expired') AND NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION 'Una reserva cancelada o vencida no se reactiva: reserve de nuevo' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Waitlisted' AND NEW.status = 'Confirmed' THEN
                    RAISE EXCEPTION 'Desde la lista de espera se confirma aceptando una invitación' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Confirmed' AND NEW.status NOT IN ('Confirmed', 'Cancelled') THEN
                    RAISE EXCEPTION 'Una reserva confirmada solo se puede cancelar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status = 'Offered' AND NEW.status = 'Waitlisted' THEN
                    RAISE EXCEPTION 'Una invitación no vuelve a la lista de espera' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;

                DROP TRIGGER IF EXISTS trg_class_attendance_guard_update ON classes.class_attendance;
                DROP FUNCTION IF EXISTS classes.guard_class_attendance_update();
                DROP TRIGGER IF EXISTS trg_class_attendance_enforce ON classes.class_attendance;
                DROP FUNCTION IF EXISTS classes.enforce_class_attendance();
                ALTER TABLE classes.class_attendance
                  DROP CONSTRAINT IF EXISTS fk_class_attendance_reservation;
                ALTER TABLE classes.class_reservations DROP CONSTRAINT IF EXISTS uq_class_reservations_attendance_ref;
                """);
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("""
                ALTER TABLE classes.class_attendance DROP CONSTRAINT IF EXISTS fk_class_attendance_branch;
                ALTER TABLE classes.class_attendance DROP CONSTRAINT IF EXISTS fk_class_attendance_tenant;
                """);
        }
    }
}
