using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Classes.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlistSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE classes.class_reservations
                  ADD CONSTRAINT ck_class_reservations_offer CHECK (
                    (offered_at IS NULL) = (offer_expires_at IS NULL)
                    AND (offer_expires_at IS NULL OR offer_expires_at > offered_at)
                    AND (status <> 'Offered' OR offer_expires_at IS NOT NULL));

                -- El Worker busca invitaciones vencidas y la lista de espera se recorre en orden de llegada.
                CREATE INDEX ix_class_reservations_open_offers ON classes.class_reservations (offer_expires_at) WHERE status = 'Offered';
                CREATE INDEX ix_class_reservations_waiting ON classes.class_reservations (tenant_id, session_id, created_at, id) WHERE status = 'Waitlisted';

                -- La capacidad la decide la base. Confirmadas y ofrecidas ocupan cupo; la lista de espera no, pero tiene su propio tope y solo
                -- existe si la clase la admite. FC001 sin cupo, FC002 sesión no programada, FC004 clase sin lista de espera, FC005 lista llena.
                CREATE OR REPLACE FUNCTION classes.enforce_reservation_capacity() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  w record;
                  s record;
                  taken integer;
                  waiting integer;
                BEGIN
                  IF NEW.status = 'Waitlisted' THEN
                    IF TG_OP = 'UPDATE' THEN
                      RETURN NEW;
                    END IF;

                    SELECT se.status AS status, c.waitlist_enabled AS enabled, c.waitlist_capacity AS capacity INTO w
                      FROM classes.class_sessions se
                      JOIN classes.group_classes c ON c.tenant_id = se.tenant_id AND c.id = se.class_id
                      WHERE se.id = NEW.session_id FOR UPDATE OF se;
                    IF w.status IS DISTINCT FROM 'Scheduled' THEN
                      RAISE EXCEPTION 'La sesión no admite reservas' USING ERRCODE = 'FC002';
                    END IF;
                    IF NOT w.enabled THEN
                      RAISE EXCEPTION 'La clase no tiene lista de espera' USING ERRCODE = 'FC004';
                    END IF;

                    SELECT count(*) INTO waiting FROM classes.class_reservations
                      WHERE session_id = NEW.session_id AND status = 'Waitlisted' AND id <> NEW.id;
                    IF waiting >= w.capacity THEN
                      RAISE EXCEPTION 'La lista de espera está llena' USING ERRCODE = 'FC005';
                    END IF;
                    RETURN NEW;
                  END IF;

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

                -- Transiciones válidas: de la lista de espera se pasa a ofrecida (nunca directo a confirmada), y una confirmada solo se cancela.
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS classes.ix_class_reservations_waiting;
                DROP INDEX IF EXISTS classes.ix_class_reservations_open_offers;
                ALTER TABLE classes.class_reservations DROP CONSTRAINT IF EXISTS ck_class_reservations_offer;

                CREATE OR REPLACE FUNCTION classes.enforce_reservation_capacity() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  s record;
                  taken integer;
                BEGIN
                  IF NEW.status NOT IN ('Confirmed', 'Offered') THEN
                    RETURN NEW;
                  END IF;
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
                  RETURN NEW;
                END
                $fn$;
                """);
        }
    }
}
