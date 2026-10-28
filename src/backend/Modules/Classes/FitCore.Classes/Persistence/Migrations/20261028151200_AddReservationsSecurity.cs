using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Classes.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationsSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "classes.class_reservations",
            "classes.idempotency_keys"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "classes.class_reservations", "fk_class_reservations_tenant");
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "classes.idempotency_keys", "fk_idempotency_keys_tenant");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "classes.class_reservations", "fk_class_reservations_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "classes");

            migrationBuilder.Sql("""
                -- La reserva hereda la sucursal de su sesión y la base lo exige; el alumno es del mismo gimnasio.
                ALTER TABLE classes.class_sessions ADD CONSTRAINT uq_class_sessions_tenant_id_branch UNIQUE (tenant_id, id, branch_id);

                ALTER TABLE classes.class_reservations
                  ADD CONSTRAINT fk_class_reservations_session FOREIGN KEY (tenant_id, session_id, branch_id)
                    REFERENCES classes.class_sessions (tenant_id, id, branch_id),
                  ADD CONSTRAINT fk_class_reservations_member FOREIGN KEY (tenant_id, member_id)
                    REFERENCES members.members (tenant_id, id);

                ALTER TABLE classes.class_reservations
                  ADD CONSTRAINT ck_class_reservations_status CHECK (status IN ('Confirmed', 'Waitlisted', 'Offered', 'Cancelled', 'Expired')),
                  ADD CONSTRAINT ck_class_reservations_source CHECK (source IN ('Member', 'Staff')),
                  ADD CONSTRAINT ck_class_reservations_cancel CHECK ((status = 'Cancelled') = (cancelled_at IS NOT NULL)),
                  ADD CONSTRAINT ck_class_reservations_late CHECK (
                    NOT late_cancellation OR (status = 'Cancelled' AND cancel_reason IS NOT NULL AND length(btrim(cancel_reason)) >= 5));

                ALTER TABLE classes.idempotency_keys
                  ADD CONSTRAINT ck_idempotency_keys_key CHECK (length(btrim(key)) BETWEEN 1 AND 100);

                -- Un alumno tiene a lo sumo una reserva viva por sesión: lo decide la base cuando dos solicitudes llegan a la vez.
                -- Las canceladas y vencidas conservan la historia y no impiden volver a reservar.
                CREATE UNIQUE INDEX ux_class_reservations_live ON classes.class_reservations (tenant_id, session_id, member_id)
                  WHERE status IN ('Confirmed', 'Waitlisted', 'Offered');

                -- La capacidad la decide la base: bloquea la fila de la sesión (quien llegue segundo espera y ve el recuento ya actualizado)
                -- y no deja ocupar más cupos de los que tiene, ni reservar en una sesión que no está programada. FC001/FC002 son códigos
                -- propios que la aplicación traduce a «sesión llena» y «sesión no disponible».
                CREATE FUNCTION classes.enforce_reservation_capacity() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  s record;
                  taken integer;
                BEGIN
                  IF NEW.status NOT IN ('Confirmed', 'Offered') THEN
                    RETURN NEW;
                  END IF;
                  -- Pasar de ofrecida a confirmada no cambia los cupos ocupados: el cupo ya estaba reservado para esa persona.
                  IF TG_OP = 'UPDATE' AND OLD.status IN ('Confirmed', 'Offered') THEN
                    RETURN NEW;
                  END IF;

                  SELECT status, capacity INTO s FROM classes.class_sessions WHERE id = NEW.session_id FOR UPDATE;
                  IF s.status IS DISTINCT FROM 'Scheduled' THEN
                    RAISE EXCEPTION 'La sesión no admite reservas' USING ERRCODE = 'FC002';
                  END IF;

                  SELECT count(*) INTO taken FROM classes.class_reservations
                    WHERE session_id = NEW.session_id AND status IN ('Confirmed', 'Offered') AND id <> NEW.id;
                  IF taken >= s.capacity THEN
                    RAISE EXCEPTION 'La sesión no tiene cupos disponibles' USING ERRCODE = 'FC001';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_class_reservations_capacity BEFORE INSERT OR UPDATE ON classes.class_reservations
                  FOR EACH ROW EXECUTE FUNCTION classes.enforce_reservation_capacity();

                -- Lo que ancla la reserva no cambia, y una cancelada o vencida no se reactiva (se reserva de nuevo).
                CREATE FUNCTION classes.guard_class_reservation_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
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
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_class_reservations_guard_update BEFORE UPDATE ON classes.class_reservations
                  FOR EACH ROW EXECUTE FUNCTION classes.guard_class_reservation_update();

                -- Nadie baja la capacidad de una sesión por debajo de los cupos ya ocupados (FC003).
                CREATE FUNCTION classes.guard_session_capacity() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE taken integer;
                BEGIN
                  SELECT count(*) INTO taken FROM classes.class_reservations
                    WHERE session_id = NEW.id AND status IN ('Confirmed', 'Offered');
                  IF taken > NEW.capacity THEN
                    RAISE EXCEPTION 'La capacidad no puede ser menor que los cupos ya ocupados' USING ERRCODE = 'FC003';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_class_sessions_capacity BEFORE UPDATE OF capacity ON classes.class_sessions
                  FOR EACH ROW WHEN (NEW.capacity < OLD.capacity) EXECUTE FUNCTION classes.guard_session_capacity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_class_sessions_capacity ON classes.class_sessions;
                DROP FUNCTION IF EXISTS classes.guard_session_capacity();
                DROP TRIGGER IF EXISTS trg_class_reservations_guard_update ON classes.class_reservations;
                DROP FUNCTION IF EXISTS classes.guard_class_reservation_update();
                DROP TRIGGER IF EXISTS trg_class_reservations_capacity ON classes.class_reservations;
                DROP FUNCTION IF EXISTS classes.enforce_reservation_capacity();
                DROP INDEX IF EXISTS classes.ux_class_reservations_live;
                ALTER TABLE classes.class_reservations
                  DROP CONSTRAINT IF EXISTS fk_class_reservations_member,
                  DROP CONSTRAINT IF EXISTS fk_class_reservations_session;
                ALTER TABLE classes.class_sessions DROP CONSTRAINT IF EXISTS uq_class_sessions_tenant_id_branch;
                """);
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("""
                ALTER TABLE classes.class_reservations DROP CONSTRAINT IF EXISTS fk_class_reservations_branch;
                ALTER TABLE classes.class_reservations DROP CONSTRAINT IF EXISTS fk_class_reservations_tenant;
                ALTER TABLE classes.idempotency_keys DROP CONSTRAINT IF EXISTS fk_idempotency_keys_tenant;
                """);
        }
    }
}
